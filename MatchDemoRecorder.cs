// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using CounterStrikeSharp.API;
using System.Diagnostics;

namespace ArenaMatch;

// GOTV 命令只在游戏线程调用；文件稳定检查与桥通知在工作线程进行。
public sealed class MatchDemoRecorder
{
    private readonly MatchSession owner;
    private readonly string gameDirectory;
    private readonly Func<string> socketPath;
    private readonly Func<long, int, bool> isRecording;
    private readonly CancellationToken lifetime;
    private readonly Action<string> log;
    private int activeMap = -1;

    public MatchDemoRecorder(MatchSession owner, string gameDirectory, Func<string> socketPath,
        Func<long, int, bool> isRecording, CancellationToken lifetime, Action<string> log)
    {
        this.owner = owner;
        this.gameDirectory = gameDirectory;
        this.socketPath = socketPath;
        this.isRecording = isRecording;
        this.lifetime = lifetime;
        this.log = log;
    }

    public static string RelativePath(long matchId, int mapNumber) =>
        DemoNoticeJournal.RelativePath(matchId, mapNumber);

    public static string DemoDirectory(string gameDirectory) => Path.Combine(gameDirectory, "ArenaMatch");

    public static void PrepareDirectory(string gameDirectory) => Directory.CreateDirectory(DemoDirectory(gameDirectory));

    public static async Task RecoverPendingAsync(string gameDirectory, Func<string> socketPath,
        Func<long, int, bool> isRecording, CancellationToken lifetime, Action<string> log)
    {
        var pending = new List<Task>();
        try
        {
            foreach (var file in DemoNoticeJournal.PendingFiles(DemoDirectory(gameDirectory)))
            {
                try
                {
                    var notice = DemoNoticeJournal.Read(file);
                    if (notice.Done) continue;
                    log($"[ArenaMatch] recovering GOTV notice: match={notice.MatchId}, map={notice.MapNumber}");
                    pending.Add(NotifyBridgeAsync(notice, gameDirectory, socketPath, isRecording, lifetime, log));
                }
                catch (Exception ex) { log($"[ArenaMatch] invalid GOTV notice {Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { log($"[ArenaMatch] GOTV notice recovery failed: {ex.Message}"); }
        await Task.WhenAll(pending);
    }

    public void StartMap()
    {
        if (!owner.Contract.RecordDemo || activeMap >= 0) return;
        var mapNumber = owner.MapNumber;
        var relative = RelativePath(owner.Contract.MatchId, mapNumber);
        // 当前 CS2 会把相对 tv_record 路径解析到 addons/metamod/；必须传绝对路径，
        // 文件仍落在本实例 csgo/ArenaMatch，供桥按相对 csgo 的白名单路径读取。
        var absolute = Path.Combine(gameDirectory, relative);
        if (absolute.Contains('"') || absolute.Contains('\n') || absolute.Contains('\r'))
            throw new IOException("invalid GOTV path");
        Server.ExecuteCommand($"tv_record \"{absolute[..^4]}\"");
        activeMap = mapNumber;
        log($"[ArenaMatch] GOTV recording started: match={owner.Contract.MatchId}, map={mapNumber}");
    }

    public bool IsRecording(long matchId, int mapNumber) =>
        owner.Contract.MatchId == matchId && Volatile.Read(ref activeMap) == mapNumber;

    public void StopMap()
    {
        if (activeMap < 0) return;
        var mapNumber = activeMap;
        activeMap = -1;
        try { Server.ExecuteCommand("tv_stoprecord"); }
        catch (Exception ex)
        {
            log($"[ArenaMatch] GOTV stop failed: match={owner.Contract.MatchId}, map={mapNumber}: {ex.Message}");
            return;
        }
        var notice = DemoNotice.Create(owner.Contract.MatchId, mapNumber, owner.RoundNumber);
        try
        {
            var watch = Stopwatch.StartNew();
            DemoNoticeJournal.Write(DemoDirectory(gameDirectory), notice);
            if (watch.ElapsedMilliseconds > 20)
                log($"[ArenaMatch] GOTV notice write took {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { log($"[ArenaMatch] GOTV notice journal failed: match={notice.MatchId}, map={mapNumber}: {ex.Message}"); }
        _ = Task.Run(() => NotifyBridgeAsync(notice, gameDirectory, socketPath, isRecording, lifetime, log));
    }

    private static async Task NotifyBridgeAsync(DemoNotice notice, string gameDirectory, Func<string> socketPath,
        Func<long, int, bool> isRecording, CancellationToken lifetime, Action<string> log)
    {
        var relative = notice.Path;
        try
        {
            var absolute = Path.Combine(gameDirectory, "ArenaMatch", Path.GetFileName(relative));
            // MatchZy 也在停录后留 15 秒；再确认长度稳定，避免桥读取尚未写完的文件。
            await Task.Delay(TimeSpan.FromSeconds(15), lifetime);
            long previousLength = -1;
            var missingChecks = 0;
            while (!lifetime.IsCancellationRequested)
            {
                // 旧通知可能在重载后遇到同一比赛/地图重新开录；录制中即使大小暂时
                // 稳定（tv_delay 缓冲）也绝不能登记到桥，否则会上传不完整录像。
                if (isRecording(notice.MatchId, notice.MapNumber))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), lifetime);
                    continue;
                }
                long length = 0;
                try { length = new FileInfo(absolute).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (length == 0 && ++missingChecks % 10 == 0)
                    log($"[ArenaMatch] GOTV file missing/empty: match={notice.MatchId}, map={notice.MapNumber}, path={relative}");
                if (length > 0 && length == previousLength)
                {
                    try
                    {
                        if (isRecording(notice.MatchId, notice.MapNumber)) continue;
                        await BridgeIpcClient.QueueDemoAsync(socketPath(), notice.MatchId, notice.MapNumber,
                            notice.RoundNumber, relative, lifetime);
                        DemoNoticeJournal.Write(DemoDirectory(gameDirectory), notice with { Done = true });
                        log($"[ArenaMatch] GOTV queued with bridge: match={notice.MatchId}, map={notice.MapNumber}, bytes={length}");
                        return;
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                    catch (BridgeIpcException ex) when (ex.Code is "demo_conflict" or "demo_disabled")
                    {
                        log($"[ArenaMatch] GOTV queue rejected permanently: match={notice.MatchId}, map={notice.MapNumber}: {ex.Code}");
                        return;
                    }
                    catch (Exception ex) { log($"[ArenaMatch] GOTV queue deferred: match={notice.MatchId}, map={notice.MapNumber}: {ex.Message}"); }
                }
                previousLength = length;
                await Task.Delay(TimeSpan.FromSeconds(3), lifetime);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { log($"[ArenaMatch] GOTV notifier stopped unexpectedly: match={notice.MatchId}, map={notice.MapNumber}: {ex.Message}"); }
    }
}
