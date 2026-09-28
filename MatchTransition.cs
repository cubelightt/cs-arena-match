// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

namespace ArenaMatch;

public enum MatchTransitionKind { Close, ChangeMap }

public sealed class MatchTransition(MatchTransitionKind kind)
{
    public const int DurationSeconds = 60;
    public const int ReminderSeconds = 10;
    public MatchTransitionKind Kind { get; } = kind;
    public int RemainingSeconds { get; private set; } = DurationSeconds;
    public bool Completed => RemainingSeconds == 0;
    public string Message => Kind == MatchTransitionKind.Close
        ? $"服务器将在{TimeText}后关闭"
        : $"服务器将在{TimeText}后切换地图，请不要退出";
    private string TimeText => RemainingSeconds == DurationSeconds ? "1分钟" : $"{RemainingSeconds}秒";
    public bool Tick()
    {
        RemainingSeconds = Math.Max(0, RemainingSeconds - ReminderSeconds);
        return Completed;
    }
}
