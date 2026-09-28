// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

namespace ArenaMatch;

public enum MatchPauseKind { None, Tactical, Technical }
public enum TacticalPauseRequestResult
{
    AcceptedRegular,
    AcceptedOvertime,
    AlreadyRequested,
    InvalidTeam,
    RegularLimitReached,
    OvertimeLimitReached,
}
public enum PauseResumeVoteResult { NoPause, InvalidTeam, DuplicateVote, VoteRecorded, Resumed }
public sealed record TacticalPauseAllowance(bool IsOvertime, int OvertimeBlock, int Remaining);

// Tactical pauses are budgeted per team, per map; technical pauses are unlimited.
public sealed class MatchPauseState
{
    public static bool CanStartTacticalTimer(bool waitingForResume, bool freezePeriod) =>
        waitingForResume && freezePeriod;

    public const int RegularPausesPerTeam = 3;
    public const int RegularPauseSeconds = 30;
    public const int OvertimeBlockRounds = 6;
    public const int OvertimePausesPerTeamAndBlock = 1;

    private readonly HashSet<int> resumeVotes = [];
    private readonly Dictionary<int, int[]> overtimePausesUsed = [];
    private readonly int[] regularPausesUsed = new int[3];
    private int trackedMap = -1;

    public MatchPauseKind Kind { get; private set; }
    public bool Requested => Kind != MatchPauseKind.None;
    public int TacticalTeam { get; private set; }
    public int TacticalPauseNumber { get; private set; }
    public int TacticalPauseLimit { get; private set; }

    public TacticalPauseRequestResult RequestTactical(int team, int mapNumber, int nextRound,
        int regulationRounds)
    {
        if (Requested) return TacticalPauseRequestResult.AlreadyRequested;
        if (team is not (1 or 2)) return TacticalPauseRequestResult.InvalidTeam;
        EnsureMap(mapNumber);

        if (nextRound <= regulationRounds)
        {
            if (regularPausesUsed[team] >= RegularPausesPerTeam)
                return TacticalPauseRequestResult.RegularLimitReached;
            regularPausesUsed[team]++;
            BeginTactical(team, regularPausesUsed[team], RegularPausesPerTeam);
            return TacticalPauseRequestResult.AcceptedRegular;
        }

        var overtimeBlock = GetOvertimeBlock(nextRound, regulationRounds);
        if (!overtimePausesUsed.TryGetValue(overtimeBlock, out var used))
        {
            used = new int[3];
            overtimePausesUsed.Add(overtimeBlock, used);
        }
        if (used[team] >= OvertimePausesPerTeamAndBlock)
            return TacticalPauseRequestResult.OvertimeLimitReached;
        used[team]++;
        BeginTactical(team, used[team], OvertimePausesPerTeamAndBlock);
        return TacticalPauseRequestResult.AcceptedOvertime;
    }

    public TacticalPauseAllowance GetAllowance(int team, int mapNumber, int nextRound, int regulationRounds)
    {
        if (team is not (1 or 2)) return new(nextRound > regulationRounds, -1, 0);
        EnsureMap(mapNumber);
        if (nextRound <= regulationRounds)
            return new(false, -1, RegularPausesPerTeam - regularPausesUsed[team]);

        var block = GetOvertimeBlock(nextRound, regulationRounds);
        var used = overtimePausesUsed.TryGetValue(block, out var count) ? count[team] : 0;
        return new(true, block, OvertimePausesPerTeamAndBlock - used);
    }

    public bool RequestTechnical()
    {
        if (Requested) return false;
        Begin(MatchPauseKind.Technical);
        return true;
    }

    public static string FormatTacticalHud(string teamName, int pauseNumber, int pauseLimit, int secondsRemaining) =>
        $"{teamName} （{pauseNumber}/{pauseLimit}）\n战术暂停 剩余 {secondsRemaining} 秒";

    public static string FormatTacticalEndedHud(string teamName, int pauseNumber, int pauseLimit) =>
        $"{teamName} （{pauseNumber}/{pauseLimit}）\n暂停结束 比赛继续";

    public PauseResumeVoteResult VoteResume(int team, bool opposingTeamEmpty)
    {
        if (!Requested) return PauseResumeVoteResult.NoPause;
        if (team is not (1 or 2)) return PauseResumeVoteResult.InvalidTeam;
        if (!resumeVotes.Add(team)) return PauseResumeVoteResult.DuplicateVote;
        if (opposingTeamEmpty || resumeVotes.Count >= 2)
        {
            ClearActive();
            return PauseResumeVoteResult.Resumed;
        }
        return PauseResumeVoteResult.VoteRecorded;
    }

    public bool ForceResume()
    {
        if (!Requested) return false;
        ClearActive();
        return true;
    }

    public void Reset()
    {
        ClearActive();
        Array.Clear(regularPausesUsed);
        overtimePausesUsed.Clear();
        trackedMap = -1;
    }

    private void EnsureMap(int mapNumber)
    {
        if (trackedMap == mapNumber) return;
        ClearActive();
        Array.Clear(regularPausesUsed);
        overtimePausesUsed.Clear();
        trackedMap = mapNumber;
    }

    private static int GetOvertimeBlock(int nextRound, int regulationRounds) =>
        Math.Max(0, (nextRound - regulationRounds - 1) / OvertimeBlockRounds);

    private void Begin(MatchPauseKind kind)
    {
        Kind = kind;
        TacticalTeam = 0;
        TacticalPauseNumber = 0;
        TacticalPauseLimit = 0;
        resumeVotes.Clear();
    }

    private void BeginTactical(int team, int pauseNumber, int pauseLimit)
    {
        Begin(MatchPauseKind.Tactical);
        TacticalTeam = team;
        TacticalPauseNumber = pauseNumber;
        TacticalPauseLimit = pauseLimit;
    }

    private void ClearActive()
    {
        Kind = MatchPauseKind.None;
        TacticalTeam = 0;
        TacticalPauseNumber = 0;
        TacticalPauseLimit = 0;
        resumeVotes.Clear();
    }
}
