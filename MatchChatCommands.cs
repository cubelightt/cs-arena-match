// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

namespace ArenaMatch;

public static class MatchChatCommands
{
    private static readonly HashSet<string> known = new(StringComparer.Ordinal)
    {
        ".ready", ".unready", ".start", ".stay", ".switch", ".swap", ".guns",
        ".p", ".pause", ".tech", ".un", ".unpause", ".resume", ".forceun",
    };

    public static string Normalize(string? text) =>
        (text ?? "").Trim().Replace('。', '.').ToLowerInvariant();

    public static bool IsKnown(string command) => known.Contains(command);
}
