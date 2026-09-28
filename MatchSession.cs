// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

namespace ArenaMatch;

public enum MatchPhase { WaitingForAck, Warmup, ReadyToStart, Knife, KnifeChoice, Live, MapEnd, SeriesEnd, MapChanging }
public enum RosterRole { None, Team1, Team2, Spectator }
public sealed record TeamReadyProgress(int ReadyCount, int Total,
    IReadOnlyList<string> NotConnected, IReadOnlyList<string> NotReady);
public sealed record MatchReadyProgress(TeamReadyProgress Team1, TeamReadyProgress Team2)
{
    public bool AllReady => Team1.ReadyCount == Team1.Total && Team2.ReadyCount == Team2.Total;
}
public sealed record RoundOutcome(int MapNumber, int RoundNumber, int WinningSide, RosterRole Winner,
    int Team1Score, int Team2Score);
public sealed record MapOutcome(int MapNumber, RosterRole Winner, int Team1Score, int Team2Score,
    int Team1SeriesScore, int Team2SeriesScore, bool SeriesFinished);

// 只保存本站名单和比赛状态；所有方法只由游戏线程调用。
public sealed class MatchSession
{
    private readonly HashSet<string> ready = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> extraSpectators = new(StringComparer.Ordinal);
    private bool roundOpen;
    private bool liveAnnounced;

    public MatchSession(MatchContract contract, string sha256)
    {
        Contract = contract;
        Sha256 = sha256;
        Team1IsCT = contract.MapSides[0] is "knife" or "team1_ct" or "team2_t";
    }

    public MatchContract Contract { get; }
    public string Sha256 { get; }
    public MatchPhase Phase { get; private set; } = MatchPhase.WaitingForAck;
    public int MapNumber { get; private set; }
    public string CurrentMap => Contract.Maps[MapNumber];
    public bool Team1IsCT { get; private set; } = true;
    public string CTTeamName => Team1IsCT ? Contract.Team1Name : Contract.Team2Name;
    public string TTeamName => Team1IsCT ? Contract.Team2Name : Contract.Team1Name;
    public RosterRole SeriesWinner => Team1SeriesScore == Team2SeriesScore ? RosterRole.None
        : Team1SeriesScore > Team2SeriesScore ? RosterRole.Team1 : RosterRole.Team2;
    public int RoundNumber { get; private set; }
    public int Team1Score { get; private set; }
    public int Team2Score { get; private set; }
    public int Team1CTScore { get; private set; }
    public int Team1TScore { get; private set; }
    public int Team2CTScore { get; private set; }
    public int Team2TScore { get; private set; }
    public int Team1SeriesScore { get; private set; }
    public int Team2SeriesScore { get; private set; }
    public RosterRole KnifeWinner { get; private set; }
    public IReadOnlyCollection<string> ReadyPlayers => ready;

    public RosterRole RoleOf(string steamId)
    {
        if (Contract.Team1Players.ContainsKey(steamId)) return RosterRole.Team1;
        if (Contract.Team2Players.ContainsKey(steamId)) return RosterRole.Team2;
        if (Contract.Spectators.ContainsKey(steamId) || extraSpectators.ContainsKey(steamId)) return RosterRole.Spectator;
        return RosterRole.None;
    }

    public string? DisplayNameOf(string steamId)
    {
        if (Contract.Team1Players.TryGetValue(steamId, out var name)) return name;
        if (Contract.Team2Players.TryGetValue(steamId, out name)) return name;
        if (Contract.Spectators.TryGetValue(steamId, out name)) return name;
        return extraSpectators.GetValueOrDefault(steamId);
    }

    public bool AddSpectator(string steamId, string name)
    {
        if (RoleOf(steamId) is RosterRole.Team1 or RosterRole.Team2 || string.IsNullOrWhiteSpace(name)) return false;
        if (Contract.Spectators.ContainsKey(steamId)) return Contract.Spectators[steamId] == name;
        if (extraSpectators.TryGetValue(steamId, out var existing)) return existing == name;
        extraSpectators.Add(steamId, name);
        return true;
    }

    public void Acknowledge()
    {
        if (Phase == MatchPhase.WaitingForAck) Phase = MatchPhase.Warmup;
    }

    public bool SetReady(string steamId, bool value)
    {
        if (Phase is not (MatchPhase.Warmup or MatchPhase.ReadyToStart) ||
            RoleOf(steamId) is not (RosterRole.Team1 or RosterRole.Team2)) return false;
        if (value) ready.Add(steamId);
        else { ready.Remove(steamId); Phase = MatchPhase.Warmup; }
        return true;
    }

    public void Disconnect(string steamId)
    {
        if (ready.Remove(steamId) && Phase == MatchPhase.ReadyToStart) Phase = MatchPhase.Warmup;
    }

    public bool AllReady(IEnumerable<string> connectedHumans)
    {
        var connected = connectedHumans.ToHashSet(StringComparer.Ordinal);
        // 网站 JSON 的每队真人名单是人数权威；空队伍（增强人机）自然满足。
        return Contract.Team1Players.Keys.All(id => connected.Contains(id) && ready.Contains(id)) &&
            Contract.Team2Players.Keys.All(id => connected.Contains(id) && ready.Contains(id));
    }

    public MatchReadyProgress GetReadyProgress(IEnumerable<string> connectedHumans)
    {
        var connected = connectedHumans.ToHashSet(StringComparer.Ordinal);
        return new(TeamProgress(Contract.Team1Players, connected), TeamProgress(Contract.Team2Players, connected));
    }

    private TeamReadyProgress TeamProgress(IReadOnlyDictionary<string, string> players, ISet<string> connected)
    {
        var notConnected = players.Where(player => !connected.Contains(player.Key))
            .Select(player => player.Value).ToArray();
        var notReady = players.Where(player => connected.Contains(player.Key) && !ready.Contains(player.Key))
            .Select(player => player.Value).ToArray();
        var readyCount = players.Keys.Count(id => connected.Contains(id) && ready.Contains(id));
        return new TeamReadyProgress(readyCount, players.Count, notConnected, notReady);
    }

    public bool PrepareStart(IEnumerable<string> connectedHumans)
    {
        if (Phase != MatchPhase.Warmup || !AllReady(connectedHumans)) return false;
        Phase = MatchPhase.ReadyToStart;
        return true;
    }

    public bool ForcePrepareStart()
    {
        if (Phase != MatchPhase.Warmup) return false;
        Phase = MatchPhase.ReadyToStart;
        return true;
    }

    public bool BeginMap()
    {
        if (Phase != MatchPhase.ReadyToStart) return false;
        Team1IsCT = Contract.MapSides[MapNumber] is "knife" or "team1_ct" or "team2_t";
        Phase = Contract.MapSides[MapNumber] == "knife" ? MatchPhase.Knife : MatchPhase.Live;
        roundOpen = false;
        liveAnnounced = false;
        return true;
    }

    public bool RoundStarted()
    {
        if (Phase is not (MatchPhase.Knife or MatchPhase.Live)) return false;
        roundOpen = true;
        if (Phase != MatchPhase.Live || liveAnnounced) return false;
        liveAnnounced = true;
        return true;
    }

    public RosterRole TeamForSide(int side)
    {
        if (side is not (2 or 3)) return RosterRole.None;
        return (side == 3) == Team1IsCT ? RosterRole.Team1 : RosterRole.Team2;
    }

    public void SyncTeam1Side(bool isCT)
    {
        if (Phase == MatchPhase.Live) Team1IsCT = isCT;
    }

    public RoundOutcome? RoundEnded(int winningSide)
    {
        if (!roundOpen || Phase is not (MatchPhase.Knife or MatchPhase.Live)) return null;
        roundOpen = false;
        var winner = TeamForSide(winningSide);
        if (winner == RosterRole.None) return null;
        if (Phase == MatchPhase.Knife)
        {
            KnifeWinner = winner;
            Phase = MatchPhase.KnifeChoice;
            return null;
        }
        RoundNumber++;
        if (winner == RosterRole.Team1)
        {
            Team1Score++;
            if (winningSide == 3) Team1CTScore++; else Team1TScore++;
        }
        else
        {
            Team2Score++;
            if (winningSide == 3) Team2CTScore++; else Team2TScore++;
        }
        return new RoundOutcome(MapNumber, RoundNumber, winningSide, winner, Team1Score, Team2Score);
    }

    public bool ChooseKnife(string winnerSteamId, bool switchSides)
    {
        if (Phase != MatchPhase.KnifeChoice || RoleOf(winnerSteamId) != KnifeWinner) return false;
        if (switchSides) Team1IsCT = !Team1IsCT;
        Phase = MatchPhase.Live;
        roundOpen = false;
        liveAnnounced = false;
        return true;
    }

    public bool ChooseKnifeForEmptyRoster()
    {
        if (Phase != MatchPhase.KnifeChoice ||
            (KnifeWinner == RosterRole.Team1 ? Contract.Team1Players.Count : Contract.Team2Players.Count) != 0) return false;
        Phase = MatchPhase.Live;
        roundOpen = liveAnnounced = false;
        return true;
    }

    public MapOutcome? FinishMap()
    {
        if (Phase != MatchPhase.Live || (Team1Score == Team2Score && Contract.OvertimeEnabled)) return null;
        var winner = Team1Score == Team2Score ? RosterRole.None
            : Team1Score > Team2Score ? RosterRole.Team1 : RosterRole.Team2;
        if (winner == RosterRole.Team1) Team1SeriesScore++;
        else if (winner == RosterRole.Team2) Team2SeriesScore++;
        var target = Contract.Maps.Count / 2 + 1;
        var finished = MapNumber + 1 == Contract.Maps.Count ||
            (Contract.ClinchSeries && (Team1SeriesScore >= target || Team2SeriesScore >= target));
        Phase = finished ? MatchPhase.SeriesEnd : MatchPhase.MapEnd;
        return new MapOutcome(MapNumber, winner, Team1Score, Team2Score, Team1SeriesScore, Team2SeriesScore, finished);
    }

    public bool AdvanceMap()
    {
        if (Phase != MatchPhase.MapEnd || MapNumber + 1 >= Contract.Maps.Count) return false;
        MapNumber++;
        Team1IsCT = Contract.MapSides[MapNumber] is "knife" or "team1_ct" or "team2_t";
        RoundNumber = Team1Score = Team2Score = 0;
        Team1CTScore = Team1TScore = Team2CTScore = Team2TScore = 0;
        KnifeWinner = RosterRole.None;
        roundOpen = liveAnnounced = false;
        ready.Clear();
        Phase = MatchPhase.MapChanging;
        return true;
    }

    public bool MapLoaded()
    {
        if (Phase != MatchPhase.MapChanging) return false;
        Phase = MatchPhase.Warmup;
        return true;
    }

    public RosterRole ForceEnd()
    {
        Phase = MatchPhase.SeriesEnd;
        if (Team1SeriesScore != Team2SeriesScore)
            return Team1SeriesScore > Team2SeriesScore ? RosterRole.Team1 : RosterRole.Team2;
        return RosterRole.None;
    }
}
