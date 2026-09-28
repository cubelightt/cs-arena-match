// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using CounterStrikeSharp.API.Modules.Utils;

namespace ArenaMatch;

internal static class ArenaChat
{
    private static string Prefix => $" {ChatColors.Green}[Arena]{ChatColors.Default}";

    public static string Message(string text) => $"{Prefix} {text}";

    public static string ReadyResult(bool ready) => Message(
        $"{(ready ? ChatColors.Green : ChatColors.Red)}{(ready ? "已标记为准备状态" : "已取消准备")}{ChatColors.Default}");

    public static string TeamReady(string team, int readyCount, int total) =>
        $"{Prefix} {ChatColors.Green}{team}{ChatColors.Default} " +
        $"{ChatColors.Green}已准备{ChatColors.Default} {readyCount}/{total}";

    public static string TeamMissing(string team, string status, string names) =>
        $"{Prefix} {ChatColors.Green}{team}{ChatColors.Default} " +
        $"{ChatColors.Red}{status}{ChatColors.Default}：{names}";

    public static string TeamNeutral(string team, string status) =>
        $"{Prefix} {ChatColors.Green}{team}{ChatColors.Default} {status}";
}
