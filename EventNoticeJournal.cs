// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using System.Text.Json;

namespace ArenaMatch;

// 进程在桥 IPC 确认前退出时，仍能按原先次序补交事件。
public sealed record EventNotice(int Version, long MatchId, long Sequence, string Payload, bool Done);

public sealed class EventNoticeJournal
{
    private readonly string directory;
    private long sequence;
    public IReadOnlyList<EventNotice> RecoverPending { get; }

    public EventNoticeJournal(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        var pending = new List<EventNotice>();
        foreach (var file in Directory.EnumerateFiles(directory, "event_*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var notice = Read(file);
            sequence = Math.Max(sequence, notice.Sequence);
            if (!notice.Done) pending.Add(notice);
        }
        RecoverPending = pending;
    }

    public EventNotice Append(long matchId, JsonElement payload)
    {
        if (matchId <= 0 || sequence == long.MaxValue) throw new FormatException("invalid event sequence");
        var notice = new EventNotice(1, matchId, ++sequence, payload.GetRawText(), false);
        Write(notice);
        return notice;
    }

    public void Complete(EventNotice notice) => Write(notice with { Done = true });

    private void Write(EventNotice notice)
    {
        Validate(notice);
        var file = Path.Combine(directory, FileName(notice.Sequence));
        var temporary = Path.Combine(directory, $".event_{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(notice));
                // 游戏线程只需保证进程重启可恢复；强制磁盘 fsync 在实服会造成 100+ ms 长帧。
                // 桥收到 IPC 后仍负责 fsync 的断电持久保证。
                stream.Flush();
            }
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static EventNotice Read(string file)
    {
        var notice = JsonSerializer.Deserialize<EventNotice>(File.ReadAllBytes(file))
            ?? throw new FormatException("empty event notice");
        Validate(notice);
        if (Path.GetFileName(file) != FileName(notice.Sequence))
            throw new FormatException("event notice filename mismatch");
        return notice;
    }

    private static string FileName(long sequence) => $"event_{sequence:D20}.json";

    private static void Validate(EventNotice notice)
    {
        if (notice.Version != 1 || notice.MatchId <= 0 || notice.Sequence <= 0 ||
            string.IsNullOrWhiteSpace(notice.Payload) || notice.Payload.Length > 64 * 1024)
            throw new FormatException("invalid event notice");
        using var document = JsonDocument.Parse(notice.Payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("matchid", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var matchId) || matchId != notice.MatchId ||
            !root.TryGetProperty("event", out var name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()))
            throw new FormatException("invalid event notice payload");
    }
}
