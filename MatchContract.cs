// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArenaMatch;

// 纯数据边界，可由独立测试程序运行，无需启动游戏服务器。
public sealed record MatchContract(
    long MatchId,
    string Team1Id,
    string Team2Id,
    string Team1Name,
    string Team2Name,
    IReadOnlyDictionary<string, string> Team1Players,
    IReadOnlyDictionary<string, string> Team2Players,
    IReadOnlyDictionary<string, string> Spectators,
    IReadOnlyList<string> Maps,
    IReadOnlyList<string> MapSides,
    IReadOnlyDictionary<string, string> Cvars,
    int PlayersPerTeam,
    int MinPlayersToReady,
    bool ClinchSeries,
    bool RecordDemo)
{
    public bool OvertimeEnabled => !Cvars.TryGetValue("mp_overtime_enable", out var enabled) || enabled != "0";

    public const int MaxJsonBytes = 1024 * 1024;
    private static readonly Regex Steam64 = new("^7656119[0-9]{10}$", RegexOptions.CultureInvariant);
    private static readonly Regex MapName = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Sides = ["knife", "team1_ct", "team1_t", "team2_ct", "team2_t"];

    public static MatchContract Parse(ReadOnlySpan<byte> utf8, long requestedMatchId)
    {
        if (utf8.Length == 0 || utf8.Length > MaxJsonBytes) throw new FormatException("match JSON size is invalid");
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("match JSON must be an object");
            var matchId = RequiredInt(root, "matchid");
            if (matchId <= 0 || matchId != requestedMatchId) throw new FormatException("matchid mismatch");
            var team1 = RequiredObject(root, "team1");
            var team2 = RequiredObject(root, "team2");
            var spectators = RequiredObject(root, "spectators");
            var team1Id = RequiredString(team1, "id");
            var team2Id = RequiredString(team2, "id");
            if (team1Id == team2Id) throw new FormatException("team IDs must differ");
            var team1Name = RequiredString(team1, "name");
            var team2Name = RequiredString(team2, "name");
            var a = Players(team1);
            var b = Players(team2);
            var spec = Players(spectators);
            if (a.Count == 0 && b.Count == 0) throw new FormatException("both team lists are empty");
            if (a.Keys.Concat(b.Keys).Concat(spec.Keys).Distinct().Count() != a.Count + b.Count + spec.Count)
                throw new FormatException("Steam64 appears in more than one list");

            var maps = StringArray(root, "maplist");
            var sides = StringArray(root, "map_sides");
            var numMaps = RequiredInt(root, "num_maps");
            if (numMaps is not (1 or 3) || maps.Count != numMaps || sides.Count != numMaps)
                throw new FormatException("BO1/BO3 map count mismatch");
            if (maps.Any(m => !MapName.IsMatch(m)) || maps.Distinct().Count() != maps.Count)
                throw new FormatException("invalid or repeated map name");
            if (sides.Any(s => !Sides.Contains(s))) throw new FormatException("invalid map_sides value");
            if (!RequiredBool(root, "skip_veto")) throw new FormatException("plugin veto is unsupported");
            if (RequiredBool(root, "wingman")) throw new FormatException("wingman is unsupported");
            if (RequiredInt(root, "min_spectators_to_ready") != 0)
                throw new FormatException("spectator ready is unsupported");
            var playersPerTeam = RequiredInt(root, "players_per_team");
            var minPlayers = RequiredInt(root, "min_players_to_ready");
            if (playersPerTeam is < 1 or > 128 || minPlayers is < 1 or > 256)
                throw new FormatException("ready thresholds out of range");

            var cvars = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in RequiredObject(root, "cvars").EnumerateObject())
            {
                // 桥必须剥离网站地址和认证；插件不能持有网站凭据。
                if (property.Name.StartsWith("matchzy_remote_log_", StringComparison.Ordinal) ||
                    property.Name.StartsWith("matchzy_demo_upload_", StringComparison.Ordinal))
                    throw new FormatException("transport credential leaked into plugin cvars");
                if (!Regex.IsMatch(property.Name, "^[a-zA-Z_][a-zA-Z0-9_]{0,63}$") ||
                    property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException("invalid cvar");
                cvars.Add(property.Name, property.Value.ToString());
            }
            _ = DuelRules.Parse(cvars);
            if (numMaps == 3 && cvars.TryGetValue("mp_overtime_enable", out var overtime) &&
                overtime is "0" or "false" or "False")
                throw new FormatException("BO3 requires overtime");
            var recordDemo = !root.TryGetProperty("record_demo", out var demo) ||
                (demo.ValueKind == JsonValueKind.True ? true : demo.ValueKind == JsonValueKind.False ? false : throw new FormatException("record_demo must be boolean"));
            return new MatchContract(matchId, team1Id, team2Id, team1Name, team2Name, a, b, spec, maps, sides, cvars,
                (int)playersPerTeam, (int)minPlayers, RequiredBool(root, "clinch_series"), recordDemo);
        }
        catch (JsonException ex)
        {
            throw new FormatException("invalid match JSON", ex);
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{key} must be an object");
        return value;
    }

    private static string RequiredString(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString())) throw new FormatException($"{key} must be a nonempty string");
        return value.GetString()!;
    }

    private static long RequiredInt(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var number)) throw new FormatException($"{key} must be an integer");
        return number;
    }

    private static bool RequiredBool(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException($"{key} must be boolean");
        return value.GetBoolean();
    }

    private static IReadOnlyList<string> StringArray(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{key} must be an array");
        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new FormatException($"{key} contains an invalid item");
            values.Add(item.GetString()!);
        }
        return values;
    }

    private static IReadOnlyDictionary<string, string> Players(JsonElement parent)
    {
        var players = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var player in RequiredObject(parent, "players").EnumerateObject())
        {
            if (!Steam64.IsMatch(player.Name) || player.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(player.Value.GetString())) throw new FormatException("invalid Steam64 player entry");
            players.Add(player.Name, player.Value.GetString()!);
        }
        return players;
    }
}
