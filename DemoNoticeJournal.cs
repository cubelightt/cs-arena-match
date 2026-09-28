// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using System.Text.Json;

namespace ArenaMatch;

// 停录后先留下极小的本地通知凭据；插件重载时可继续把已完成的录像交给桥。
public sealed record DemoNotice(int Version, long MatchId, int MapNumber, int RoundNumber, string Path, bool Done)
{
    public static DemoNotice Create(long matchId, int mapNumber, int roundNumber) =>
        new(1, matchId, mapNumber, roundNumber, DemoNoticeJournal.RelativePath(matchId, mapNumber), false);
}

public static class DemoNoticeJournal
{
    public static string RelativePath(long matchId, int mapNumber) =>
        $"ArenaMatch/match_{matchId}_map_{mapNumber}.dem";

    public static string FileName(long matchId, int mapNumber) => $"pending_{matchId}_{mapNumber}.json";

    public static void Write(string directory, DemoNotice notice)
    {
        Validate(notice);
        var path = System.IO.Path.Combine(directory, FileName(notice.MatchId, notice.MapNumber));
        var temporary = System.IO.Path.Combine(directory, $".pending_{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(notice));
                // 停录回调在游戏线程；普通 flush 足以跨插件/进程重启恢复，
                // fsync 在实服测得 100+ ms 长帧。桥接收后负责 fsync 上传任务。
                file.Flush();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static DemoNotice Read(string file)
    {
        var notice = JsonSerializer.Deserialize<DemoNotice>(File.ReadAllBytes(file))
            ?? throw new FormatException("empty demo notice");
        Validate(notice);
        if (System.IO.Path.GetFileName(file) != FileName(notice.MatchId, notice.MapNumber))
            throw new FormatException("demo notice filename mismatch");
        return notice;
    }

    public static IEnumerable<string> PendingFiles(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "pending_*_*.json") : [];

    private static void Validate(DemoNotice notice)
    {
        if (notice.Version != 1 || notice.MatchId <= 0 || notice.MapNumber is < 0 or > 2 ||
            notice.RoundNumber is < 0 or > 200 || notice.Path != RelativePath(notice.MatchId, notice.MapNumber))
            throw new FormatException("invalid demo notice");
    }
}
