// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

namespace ArenaMatch;

// 单挑规则只取本场 JSON 快照；普通房不读取或保留任何单挑状态。
public sealed record DuelRules(bool Enabled, string Preset, int PistolRounds, int RifleRounds, int SniperRounds)
{
    private static readonly HashSet<string> Presets = ["rifle", "pistol", "sniper", "solo"];
    private static readonly HashSet<string> Keys = ["arena_duel_roundswap", "arena_duel_preset",
        "arena_duel_phase_pistol", "arena_duel_phase_rifle", "arena_duel_phase_sniper"];

    public static DuelRules Parse(IReadOnlyDictionary<string, string> cvars)
    {
        if (cvars.Keys.Any(k => k.StartsWith("arena_duel_", StringComparison.Ordinal) && !Keys.Contains(k)))
            throw new FormatException("unknown duel cvar");
        var gate = Value("arena_duel_roundswap", "0");
        if (gate is not ("0" or "1")) throw new FormatException("invalid duel gate");
        var preset = Value("arena_duel_preset", "rifle");
        if (!Presets.Contains(preset)) throw new FormatException("invalid duel preset");
        var pistol = Rounds("arena_duel_phase_pistol", 10);
        var rifle = Rounds("arena_duel_phase_rifle", 28);
        var sniper = Rounds("arena_duel_phase_sniper", 13);
        if (gate == "1" && !cvars.ContainsKey("arena_duel_preset"))
            throw new FormatException("duel preset missing");
        return new DuelRules(gate == "1", preset, pistol, rifle, sniper);

        string Value(string key, string fallback) => cvars.TryGetValue(key, out var raw) ? raw.Trim().Trim('"').Trim() : fallback;
        int Rounds(string key, int fallback)
        {
            if (!int.TryParse(Value(key, fallback.ToString()), out var value) || value is < 1 or > 128)
                throw new FormatException($"invalid duel phase: {key}");
            return value;
        }
    }

    public string Category(int completedRounds)
    {
        if (Preset != "solo") return Preset;
        if (completedRounds < PistolRounds) return "pistol";
        if (completedRounds < PistolRounds + RifleRounds) return "rifle";
        return "sniper";
    }

    public (string Title, string Progress) HudLines(int completedRounds, int maxRounds)
    {
        completedRounds = Math.Max(0, completedRounds);
        if (Preset != "solo")
            return (DisplayName(Preset), $"{Math.Clamp(completedRounds, 0, Math.Max(1, maxRounds))}/{Math.Max(1, maxRounds)}");

        var pistolPlayed = Math.Clamp(completedRounds, 0, PistolRounds);
        var riflePlayed = Math.Clamp(completedRounds - PistolRounds, 0, RifleRounds);
        var sniperPlayed = Math.Clamp(completedRounds - PistolRounds - RifleRounds, 0, SniperRounds);
        return ($"当前为 {DisplayName(Category(completedRounds))}",
            $"手枪 （{pistolPlayed}/{PistolRounds}）长枪（{riflePlayed}/{RifleRounds}） 狙击（{sniperPlayed}/{SniperRounds}）");
    }

    public static string DisplayName(string category) => category switch
    {
        "pistol" => "手枪对决",
        "rifle" => "长枪对决",
        "sniper" => "狙击对决",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "unknown duel category"),
    };

    public bool RotateSpawns(int completedRounds) => Enabled && completedRounds % 2 == 1;
}
