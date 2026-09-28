// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics;

namespace ArenaMatch;

// 事件先在实例目录落盘，再由工作线程按序交给桥；失败时保留队首。
public sealed class MatchEventOutbox : IDisposable
{
    private readonly Channel<EventNotice> channel = Channel.CreateUnbounded<EventNotice>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource stop = new();
    private readonly Func<long, JsonElement, CancellationToken, Task> send;
    private readonly Action<string> log;
    private readonly Task worker;
    private readonly TimeSpan retryDelay;
    private readonly EventNoticeJournal? journal;
    private readonly IReadOnlyList<EventNotice> recovered;
    private long volatileSequence;

    public MatchEventOutbox(Func<long, JsonElement, CancellationToken, Task> send, Action<string> log,
        TimeSpan? retryDelay = null, string? journalDirectory = null)
    {
        this.send = send;
        this.log = log;
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);
        journal = journalDirectory == null ? null : new EventNoticeJournal(journalDirectory);
        recovered = journal?.RecoverPending ?? [];
        worker = Task.Run(RunAsync);
    }

    public bool Enqueue(long matchId, JsonElement matchEvent)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var notice = journal?.Append(matchId, matchEvent) ??
                new EventNotice(1, matchId, ++volatileSequence, matchEvent.GetRawText(), false);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed > 20) log($"[ArenaMatch] event journal write took {elapsed:F1} ms");
            return channel.Writer.TryWrite(notice);
        }
        catch (Exception ex)
        {
            log($"[ArenaMatch] event journal failed: match={matchId}: {ex.Message}");
            return false;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            foreach (var item in recovered)
                await DeliverAsync(item);
            await foreach (var item in channel.Reader.ReadAllAsync(stop.Token))
                await DeliverAsync(item);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task DeliverAsync(EventNotice item)
    {
        using var document = JsonDocument.Parse(item.Payload);
        var payload = document.RootElement.Clone();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await send(item.MatchId, payload, stop.Token);
                journal?.Complete(item);
                return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { log($"[ArenaMatch] event delivery deferred: {ex.Message}"); }
            await Task.Delay(retryDelay, stop.Token);
        }
    }

    public void Dispose()
    {
        channel.Writer.TryComplete();
        stop.Cancel();
        _ = worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
