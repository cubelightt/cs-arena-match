// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace ArenaMatch;

// 从 ArenaDuel 0.4.4 迁入的单挑行为；只在本场 JSON 门控开启且处于 Live 时执行。
public sealed class DuelMode
{
    private sealed record WeaponOption(string Label, string Entity);
    private sealed record MenuState(string Category, DateTime Expires);

    private static readonly Dictionary<string, WeaponOption[]> Weapons = new()
    {
        ["rifle"] = [new("AK47", "weapon_ak47"), new("M4A1-S", "weapon_m4a1_silencer"),
            new("M4A4", "weapon_m4a1"), new("加利尔AR", "weapon_galilar"), new("法玛斯", "weapon_famas")],
        ["pistol"] = [new("USP-S", "weapon_usp_silencer"), new("格洛克18", "weapon_glock"),
            new("沙漠之鹰", "weapon_deagle"), new("双持贝瑞塔", "weapon_elite"),
            new("P250", "weapon_p250"), new("FN57", "weapon_fiveseven"),
            new("Tec-9", "weapon_tec9"), new("CZ-75", "weapon_cz75a"), new("R8", "weapon_revolver")],
        ["sniper"] = [new("AWP", "weapon_awp"), new("SSG-08", "weapon_ssg08")],
    };
    private static readonly HashSet<string> AlwaysAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_usp_silencer", "weapon_hkp2000",
    };

    private readonly MatchSession owner;
    private readonly DuelRules rules;
    private readonly Func<MatchSession?> current;
    private readonly Action<float, Action> timer;
    private readonly Action<string> log;
    private readonly Func<bool> centerHudSuppressed;
    private readonly Dictionary<string, Dictionary<string, string>> choices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MenuState> menus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> entityNames = new(StringComparer.Ordinal);
    private List<(Vector3 Position, Vector3? Angle)> ctSpawns = [];
    private List<(Vector3 Position, Vector3? Angle)> tSpawns = [];

    public DuelMode(MatchSession owner, DuelRules rules, Func<MatchSession?> current,
        Action<float, Action> timer, Action<string> log, Func<bool> centerHudSuppressed)
    {
        this.owner = owner;
        this.rules = rules;
        this.current = current;
        this.timer = timer;
        this.log = log;
        this.centerHudSuppressed = centerHudSuppressed;
    }

    private bool Active => ReferenceEquals(current(), owner) && owner.Phase == MatchPhase.Live;
    private int CompletedRounds => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
        .FirstOrDefault()?.GameRules?.TotalRoundsPlayed ?? owner.RoundNumber;
    private string Category => rules.Category(CompletedRounds);

    public void MaintainHud()
    {
        if (!Active || centerHudSuppressed()) return;
        var maxRounds = int.TryParse(owner.Contract.Cvars.GetValueOrDefault("mp_maxrounds"), out var configuredRounds)
            ? configuredRounds : 31;
        var (title, progress) = rules.HudLines(CompletedRounds, maxRounds);
        var message = $"{title}\n{progress}";
        foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot && !p.IsHLTV))
            player.PrintToCenter(message);
    }

    public void OnMapStart()
    {
        ctSpawns.Clear();
        tSpawns.Clear();
        menus.Clear();
    }

    public void OnDisconnect(string steamId) => menus.Remove(steamId);

    public void OnRoundStart()
    {
        if (!Active) return;
        foreach (var stale in menus.Where(entry => entry.Value.Category != Category).Select(entry => entry.Key).ToArray())
            menus.Remove(stale);
        CleanMapWeapons();
        if (ctSpawns.Count == 0 || tSpawns.Count == 0) CollectSpawns();
        var played = CompletedRounds;
        var mapNumber = owner.MapNumber;
        if (rules.RotateSpawns(played))
            timer(0.1f, () => { if (Active && owner.MapNumber == mapNumber && CompletedRounds == played) TeleportSwap(played); });
        timer(0.7f, () => { if (Active && owner.MapNumber == mapNumber && CompletedRounds == played) ReconcileAll(); });
    }

    public void OnRoundEnd()
    {
        if (Active) CleanMapWeapons(); // 下一回合出生前删掉地图自带的装备来源。
    }

    public void OnPlayerSpawn(CCSPlayerController player)
    {
        if (!Active || player.IsHLTV) return;
        var steamId = player.SteamID.ToString();
        var mapNumber = owner.MapNumber;
        var roundNumber = CompletedRounds;
        timer(0.15f, () =>
        {
            if (Active && owner.MapNumber == mapNumber && CompletedRounds == roundNumber &&
                player.IsValid && player.SteamID.ToString() == steamId)
                Equip(player);
        });
    }

    public bool OnAutoBuy(CCSPlayerController? player)
    {
        if (!Active || player == null || !player.IsValid || !IsDuelPlayer(player)) return false;
        OpenMenu(player);
        return true;
    }

    public void OnChat(CCSPlayerController player, string text)
    {
        if (!Active || !IsDuelPlayer(player)) return;
        var steamId = player.SteamID.ToString();
        if (text == ".guns") { OpenMenu(player); return; }
        if (!menus.TryGetValue(steamId, out var menu)) return;
        if (DateTime.UtcNow > menu.Expires) { menus.Remove(steamId); return; }
        if (!int.TryParse(text, out var selected)) return;
        var table = Weapons[menu.Category];
        if (selected < 1 || selected > table.Length)
        {
            player.PrintToChat(ArenaChat.Message($"{ChatColors.Red}无效编号 {selected} (1~{table.Length}){ChatColors.Default}"));
            return;
        }
        if (!choices.TryGetValue(steamId, out var byCategory)) choices[steamId] = byCategory = new();
        byCategory[menu.Category] = table[selected - 1].Entity;
        menus.Remove(steamId);
        Equip(player);
        player.PrintToChat(ArenaChat.Message($"{ChatColors.Green}已选择 {table[selected - 1].Label}{ChatColors.Default}"));
    }

    private bool IsDuelPlayer(CCSPlayerController player) => !player.IsBot && !player.IsHLTV &&
        player.IsValid && owner.RoleOf(player.SteamID.ToString()) is RosterRole.Team1 or RosterRole.Team2;

    private bool IsPlayable(CCSPlayerController player) => player.IsBot ||
        owner.RoleOf(player.SteamID.ToString()) is RosterRole.Team1 or RosterRole.Team2;

    private void OpenMenu(CCSPlayerController player)
    {
        var category = Category;
        var steamId = player.SteamID.ToString();
        menus[steamId] = new MenuState(category, DateTime.UtcNow.AddSeconds(30));
        var table = Weapons[category];
        var chosen = ChoiceOf(steamId, category);
        player.PrintToChat(ArenaChat.Message($"{ChatColors.Green}[{DuelRules.DisplayName(category)}] 输入编号选枪，30 秒内有效：{ChatColors.Default}"));
        for (var i = 0; i < table.Length; i++)
        {
            var marker = table[i].Entity == chosen ? $"{ChatColors.Green} ← 当前{ChatColors.Default}" : "";
            player.PrintToChat(ArenaChat.Message($"{ChatColors.Yellow}{i + 1}.{ChatColors.Default} {table[i].Label}{marker}"));
        }
    }

    private string ChoiceOf(string steamId, string category) =>
        choices.TryGetValue(steamId, out var byCategory) && byCategory.TryGetValue(category, out var value)
            ? value : Weapons[category][0].Entity;

    private void Equip(CCSPlayerController player, bool scheduleReconcile = true)
    {
        if (!Active || !player.IsValid || player.IsHLTV || !IsPlayable(player) ||
            player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist)) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.Health <= 0) return;
        var category = Category;
        var weapon = ChoiceOf(player.SteamID.ToString(), category);
        try
        {
            player.RemoveWeapons();
            player.GiveNamedItem("weapon_knife");
            CBasePlayerWeapon? given;
            if (category == "pistol") given = player.GiveNamedItem<CBasePlayerWeapon>(weapon);
            else
            {
                player.GiveNamedItem("weapon_usp_silencer");
                given = player.GiveNamedItem<CBasePlayerWeapon>(weapon);
            }
            player.GiveNamedItem(category == "pistol" ? "item_kevlar" : "item_assaultsuit");
            var entityName = given != null && given.IsValid && !string.IsNullOrEmpty(given.DesignerName)
                ? given.DesignerName : weapon;
            entityNames[weapon] = entityName;
            HoldWeapon(player, given, entityName, category == "pistol" ? 2 : 1);
            if (scheduleReconcile)
            {
                var steamId = player.SteamID.ToString();
                var mapNumber = owner.MapNumber;
                var roundNumber = CompletedRounds;
                timer(0.45f, () =>
                {
                    if (Active && owner.MapNumber == mapNumber && CompletedRounds == roundNumber &&
                        player.IsValid && player.SteamID.ToString() == steamId)
                        Reconcile(player);
                });
            }
        }
        catch (Exception ex) { log($"[ArenaMatch] duel equip failed: {ex.Message}"); }
    }

    private void HoldWeapon(CCSPlayerController player, CBasePlayerWeapon? target, string entityName, int slot)
    {
        var weapons = player.PlayerPawn.Value?.WeaponServices;
        if (weapons == null) return;
        if (target != null && target.IsValid) weapons.ActiveWeapon.Raw = target.EntityHandle.Raw;
        else
        {
            foreach (var handle in weapons.MyWeapons)
            {
                var item = handle.Value;
                if (item == null || !item.IsValid || item.DesignerName != entityName) continue;
                weapons.ActiveWeapon.Raw = handle.Raw;
                break;
            }
        }
        var steamId = player.SteamID.ToString();
        Server.NextFrame(() =>
        {
            if (Active && player.IsValid && player.SteamID.ToString() == steamId)
                player.ExecuteClientCommand($"slot{slot}");
        });
    }

    private void ReconcileAll()
    {
        foreach (var player in Utilities.GetPlayers())
            if (player != null && player.IsValid && !player.IsHLTV) Reconcile(player);
    }

    private void Reconcile(CCSPlayerController player)
    {
        if (!Active || !IsPlayable(player) || player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist)) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.Health <= 0) return;
        var category = Category;
        var want = ChoiceOf(player.SteamID.ToString(), category);
        var entityName = entityNames.GetValueOrDefault(want, want);
        var weapons = pawn.WeaponServices;
        if (weapons == null) return;
        var held = weapons.MyWeapons.FirstOrDefault(h => h.Value is { IsValid: true } item && item.DesignerName == entityName);
        if (held?.Value == null || !held.Value.IsValid) { Equip(player, false); return; }
        if (weapons.ActiveWeapon.Value?.DesignerName != entityName)
            HoldWeapon(player, held.Value, entityName, category == "pistol" ? 2 : 1);
    }

    private void CollectSpawns()
    {
        ctSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist")
            .Where(e => e.IsValid && e.AbsOrigin != null).Select(e => SnapshotSpawn(e)).ToList();
        tSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist")
            .Where(e => e.IsValid && e.AbsOrigin != null).Select(e => SnapshotSpawn(e)).ToList();
        log($"[ArenaMatch] duel spawns: ct={ctSpawns.Count}, t={tSpawns.Count}");
    }

    private static (Vector3 Position, Vector3? Angle) SnapshotSpawn(CBaseEntity entity)
    {
        // AbsOrigin/AbsRotation 包装的是地图实体内存，回合重建后不能继续保留指针。
        var pos = entity.AbsOrigin!;
        var angle = entity.AbsRotation;
        return (new Vector3(pos.X, pos.Y, pos.Z), angle == null ? null : new Vector3(angle.X, angle.Y, angle.Z));
    }

    private void TeleportSwap(int completedRounds)
    {
        var index = completedRounds / 2;
        var ctIndex = 0;
        var tIndex = 0;
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.IsHLTV || !IsPlayable(player)) continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.Health <= 0) continue;
            if (player.Team == CsTeam.CounterTerrorist && tSpawns.Count > 0)
            {
                var spawn = tSpawns[(index + ctIndex++) % tSpawns.Count];
                log($"[ArenaMatch] duel teleport round={completedRounds + 1} player={player.SteamID} team={player.Team} target={spawn.Position}");
                pawn.Teleport(spawn.Position, spawn.Angle, Vector3.Zero);
                log($"[ArenaMatch] duel teleport completed round={completedRounds + 1} player={player.SteamID}");
            }
            else if (player.Team == CsTeam.Terrorist && ctSpawns.Count > 0)
            {
                var spawn = ctSpawns[(index + tIndex++) % ctSpawns.Count];
                log($"[ArenaMatch] duel teleport round={completedRounds + 1} player={player.SteamID} team={player.Team} target={spawn.Position}");
                pawn.Teleport(spawn.Position, spawn.Angle, Vector3.Zero);
                log($"[ArenaMatch] duel teleport completed round={completedRounds + 1} player={player.SteamID}");
            }
        }
    }

    private void CleanMapWeapons()
    {
        try
        {
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("game_player_equip").ToArray())
                if (entity != null && entity.IsValid) TryRemoveMapEntity(entity);
            var allowed = new HashSet<string>(AlwaysAllowed, StringComparer.OrdinalIgnoreCase);
            foreach (var option in Weapons[Category]) allowed.Add(option.Entity);
            var held = new HashSet<uint>();
            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid) continue;
                var weapons = player.PlayerPawn.Value?.WeaponServices;
                if (weapons == null) continue;
                foreach (var handle in weapons.MyWeapons) held.Add(handle.Raw);
            }
            foreach (var entity in Utilities.GetAllEntities().ToArray())
            {
                if (entity == null || !entity.IsValid) continue;
                var name = entity.DesignerName;
                if (string.IsNullOrEmpty(name) || !name.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase) || allowed.Contains(name) ||
                    held.Contains(entity.EntityHandle.Raw)) continue;
                TryRemoveMapEntity(entity);
            }
        }
        catch (Exception ex) { log($"[ArenaMatch] duel map weapon cleanup failed: {ex.Message}"); }
    }

    private void TryRemoveMapEntity(CEntityInstance entity)
    {
        entity.Remove();
    }
}
