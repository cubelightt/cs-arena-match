// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using System.Text.Json;

namespace ArenaMatch;

public sealed record MatchPlayerStats(int Kills, int Deaths, int Assists, int Damage,
    int HeadshotKills, int UtilityDamage);

// 保持网站现有 MatchZy 事件字段；同一事件可安全重传，由后端 dedupKey 去重。
public static class MatchEvents
{
    private static string Team(RosterRole role) => role switch
    {
        RosterRole.Team1 => "team1",
        RosterRole.Team2 => "team2",
        _ => "",
    };

    private static string Side(int side) => side == 3 ? "ct" : side == 2 ? "t" : "";

    private static object[] Players(IReadOnlyDictionary<string, string> roster,
        IReadOnlyDictionary<string, MatchPlayerStats>? stats) => roster.Select(p =>
    {
        MatchPlayerStats? found = null;
        if (stats != null) stats.TryGetValue(p.Key, out found);
        return (object)new { steamid = p.Key, name = p.Value,
            kills = found?.Kills ?? 0, deaths = found?.Deaths ?? 0, assists = found?.Assists ?? 0,
            damage = found?.Damage ?? 0, headshot_kills = found?.HeadshotKills ?? 0,
            utility_damage = found?.UtilityDamage ?? 0 };
    }).ToArray();

    public static JsonElement SeriesStart(MatchSession match) => JsonSerializer.SerializeToElement(new
    {
        @event = "series_start", matchid = match.Contract.MatchId,
    });

    public static JsonElement GoingLive(MatchSession match) => JsonSerializer.SerializeToElement(new
    {
        @event = "going_live", matchid = match.Contract.MatchId,
        map_number = match.MapNumber, map_name = match.CurrentMap,
    });

    public static JsonElement SidePicked(MatchSession match) => JsonSerializer.SerializeToElement(new
    {
        @event = "side_picked", matchid = match.Contract.MatchId,
        map_number = match.MapNumber, team = Team(match.KnifeWinner),
        side = Side(match.KnifeWinner == RosterRole.Team1 ? (match.Team1IsCT ? 3 : 2) :
            (match.Team1IsCT ? 2 : 3)),
    });

    public static JsonElement RoundEnd(MatchSession match, RoundOutcome round, int reason, float roundTime,
        IReadOnlyDictionary<string, MatchPlayerStats>? stats = null) =>
        JsonSerializer.SerializeToElement(new
        {
            @event = "round_end", matchid = match.Contract.MatchId,
            map_number = round.MapNumber, round_number = round.RoundNumber,
            round_time = roundTime, reason,
            winner = new { side = Side(round.WinningSide), team = Team(round.Winner) },
            team1 = new { series_score = match.Team1SeriesScore, score = round.Team1Score,
                score_ct = match.Team1CTScore, score_t = match.Team1TScore,
                players = Players(match.Contract.Team1Players, stats) },
            team2 = new { series_score = match.Team2SeriesScore, score = round.Team2Score,
                score_ct = match.Team2CTScore, score_t = match.Team2TScore,
                players = Players(match.Contract.Team2Players, stats) },
        });

    public static JsonElement MapResult(MatchSession match, MapOutcome map,
        IReadOnlyDictionary<string, MatchPlayerStats>? stats = null) => JsonSerializer.SerializeToElement(new
    {
        @event = "map_result", matchid = match.Contract.MatchId,
        map_number = map.MapNumber, map_name = match.Contract.Maps[map.MapNumber],
        winner = new { side = Side(map.Winner == RosterRole.Team1 ? (match.Team1IsCT ? 3 : 2) :
            map.Winner == RosterRole.Team2 ? (match.Team1IsCT ? 2 : 3) : 0),
            team = Team(map.Winner) },
        team1 = new { series_score = map.Team1SeriesScore, score = map.Team1Score,
            score_ct = match.Team1CTScore, score_t = match.Team1TScore,
            players = Players(match.Contract.Team1Players, stats) },
        team2 = new { series_score = map.Team2SeriesScore, score = map.Team2Score,
            score_ct = match.Team2CTScore, score_t = match.Team2TScore,
            players = Players(match.Contract.Team2Players, stats) },
    });

    public static JsonElement SeriesEnd(MatchSession match, RosterRole winner, bool forced = false) => JsonSerializer.SerializeToElement(new
    {
        @event = "series_end", matchid = match.Contract.MatchId,
        forced,
        time_until_restore = 10,
        winner = new { side = Side(winner == RosterRole.Team1 ? (match.Team1IsCT ? 3 : 2) :
            winner == RosterRole.Team2 ? (match.Team1IsCT ? 2 : 3) : 0), team = Team(winner) },
        team1_series_score = match.Team1SeriesScore, team2_series_score = match.Team2SeriesScore,
    });
}
