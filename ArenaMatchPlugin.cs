// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using System.Diagnostics;
using System.Text.Json;

namespace ArenaMatch;

// Match control, duel modes, event delivery, and optional GOTV recording.
public sealed class ArenaMatchPlugin : BasePlugin
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, string> originalCvars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> knifeCvars = new(StringComparer.Ordinal);
    private MatchEventOutbox? eventOutbox;
    private DuelMode? duel;
    private MatchDemoRecorder? demo;
    private MatchSession? session;
    private LoadedMatch? pendingMap;
    private long loadingMatchId;
    private int generation;
    private bool unloaded;
    private bool idleWarmup;
    private readonly MatchPauseState matchPause = new();
    private int pauseRevision;
    private int tacticalPauseTimerRevision = -1;
    private int tacticalPauseSecondsRemaining;
    private DateTime tacticalPauseEndHudHoldUntilUtc;
    private CounterStrikeSharp.API.Modules.Timers.Timer? knifeChoiceReminder;
    private CounterStrikeSharp.API.Modules.Timers.Timer? transitionTimer;
    private bool naturalSeriesEndPending;
    private string? lastReadyProgress;
    private string gameDirectory = "";

    public override string ModuleName => "ArenaMatch";
    public override string ModuleVersion => "0.6.15";
    public override string ModuleAuthor => "CS Arena";
    public override string ModuleDescription => "CS Arena match controller";

    public override void Load(bool hotReload)
    {
        var pluginDir = Path.GetDirectoryName(ModulePath)
            ?? throw new IOException("cannot locate plugin directory");
        gameDirectory = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "..", ".."));
        Console.WriteLine($"[ArenaMatch {ModuleVersion} LOADED]");
        eventOutbox = new MatchEventOutbox((matchId, payload, token) =>
            BridgeIpcClient.SendEventAsync(SocketPath(), matchId, payload, token), Console.WriteLine,
            journalDirectory: Path.Combine(gameDirectory, "ArenaMatch", "events"));
        _ = Task.Run(() => MatchDemoRecorder.RecoverPendingAsync(gameDirectory, SocketPath, IsCurrentRecording,
            lifetime.Token, Console.WriteLine));
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientPutInServer>(slot => Server.NextFrame(() => EnforceSlot(slot)));
        RegisterListener<Listeners.OnClientAuthorized>((slot, _) => Server.NextFrame(() => EnforceSlot(slot)));
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player != null && player.IsValid && player.SteamID != 0)
            {
                session?.Disconnect(player.SteamID.ToString());
                duel?.OnDisconnect(player.SteamID.ToString());
            }
            Server.NextFrame(AnnounceReadyProgress);
        });
        RegisterEventHandler<EventPlayerSpawn>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
            {
                EnforcePlayer(player);
                var owner = session;
                var steamId = player.SteamID;
                Server.NextFrame(() =>
                {
                    if (!unloaded && owner != null && session == owner && player.IsValid && player.SteamID == steamId)
                        ApplyPlayerName(player, owner.DisplayNameOf(steamId.ToString()));
                });
                if (session?.Phase == MatchPhase.Knife && player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist)
                    AddTimer(0.1f, () => EquipKnife(player));
                else if (session?.Phase == MatchPhase.Live) duel?.OnPlayerSpawn(player);
            }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerChat>(OnChat);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchWinPanel);
        AddCommandListener("jointeam", OnJoinTeam);
        AddCommandListener("autobuy", (player, _) => duel?.OnAutoBuy(player) == true
            ? HookResult.Stop : HookResult.Continue);
        // OnMapStart 之后引擎还会执行 gamemode/server cfg；持续以赛事阶段维护热身。
        MaintainNoIdleKick();
        AddTimer(1f, () => { MaintainNoIdleKick(); MaintainWarmup(); MaintainPlayerNames(); MaintainTacticalPause();
            MaintainDuelHud(); MaintainKnifeMoney(); }, TimerFlags.REPEAT);
    }

    public override void Unload(bool hotReload)
    {
        ResetMatchPause();
        demo?.StopMap();
        RemoveMatchBots();
        unloaded = true;
        generation++;
        lifetime.Cancel();
        eventOutbox?.Dispose();
        duel = null;
        demo = null;
        RestoreKnifeCvars();
        RestoreCvars();
    }

    private string SocketPath()
    {
        return Path.Combine(gameDirectory, ".arena-match", "bridge.sock");
    }

    private bool IsCurrentRecording(long matchId, int mapNumber) =>
        Volatile.Read(ref demo)?.IsRecording(matchId, mapNumber) == true;

    [ConsoleCommand("arena_match_load", "Load an ArenaMatch match ID from the local Go bridge")]
    public void OnLoadMatch(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null) return;
        if (!long.TryParse(command.GetArg(1), out var matchId) || matchId <= 0)
        {
            Console.WriteLine("[ArenaMatch] usage: arena_match_load <matchId>");
            return;
        }
        if (session?.Contract.MatchId == matchId || loadingMatchId == matchId)
        {
            Console.WriteLine($"[ArenaMatch] duplicate load ignored: match={matchId}");
            return;
        }
        if (session != null || loadingMatchId != 0)
        {
            Console.WriteLine("[ArenaMatch] load rejected: another match is active/loading");
            return;
        }
        loadingMatchId = matchId;
        var attempt = ++generation;
        Console.WriteLine($"[ArenaMatch] loading match={matchId}");
        _ = Task.Run(async () =>
        {
            FetchedMatch? fetched = null;
            try
            {
                fetched = await BridgeIpcClient.FetchAsync(SocketPath(), matchId, lifetime.Token);
                var contract = MatchContract.Parse(fetched.Json, matchId);
                if (contract.RecordDemo)
                {
                    try { MatchDemoRecorder.PrepareDirectory(gameDirectory); }
                    catch (Exception ex)
                    {
                        Server.NextFrame(() => FailLoad(attempt, matchId, fetched.Sha256, "apply_failed", ex.Message));
                        return;
                    }
                }
                Server.NextFrame(() => OnFetched(attempt, new LoadedMatch(contract, fetched.Sha256)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Server.NextFrame(() => FailLoad(attempt, matchId, fetched?.Sha256,
                    ex is FormatException ? "invalid_match_json" : "bridge_unavailable", ex.Message));
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnFetched(int attempt, LoadedMatch loaded)
    {
        if (unloaded || attempt != generation || loadingMatchId != loaded.Contract.MatchId) return;
        var firstMap = loaded.Contract.Maps[0];
        if (!Server.IsMapValid(firstMap))
        {
            FailLoad(attempt, loaded.Contract.MatchId, loaded.Sha256, "apply_failed", "first map unavailable");
            return;
        }
        if (!string.Equals(Server.MapName, firstMap, StringComparison.OrdinalIgnoreCase))
        {
            pendingMap = loaded;
            Console.WriteLine($"[ArenaMatch] changing to first map {firstMap} for match={loaded.Contract.MatchId}");
            try
            {
                Server.ExecuteCommand($"changelevel {firstMap}");
                AddTimer(30, () =>
                {
                    if (!unloaded && attempt == generation && pendingMap?.Contract.MatchId == loaded.Contract.MatchId)
                        FailLoad(attempt, loaded.Contract.MatchId, loaded.Sha256, "apply_failed", "first map change timed out");
                });
            }
            catch (Exception ex) { FailLoad(attempt, loaded.Contract.MatchId, loaded.Sha256, "apply_failed", ex.Message); }
            return;
        }
        ApplyLoaded(attempt, loaded);
    }

    private void OnMapStart(string mapName)
    {
        MaintainNoIdleKick();
        ResetMatchPause();
        if (pendingMap is { } loaded && loadingMatchId == loaded.Contract.MatchId &&
            string.Equals(mapName, loaded.Contract.Maps[0], StringComparison.OrdinalIgnoreCase))
        {
            ApplyLoaded(generation, loaded);
            return;
        }
        if (session?.Phase == MatchPhase.MapChanging && session.MapNumber > 0 &&
            string.Equals(mapName, session.CurrentMap, StringComparison.OrdinalIgnoreCase))
        {
            var started = Stopwatch.GetTimestamp();
            var matchId = session.Contract.MatchId;
            try
            {
                session.MapLoaded();
                duel?.OnMapStart();
                ApplyCvars(session.Contract);
                HoldWarmupTimer();
                ApplyBotConfig();
                EnforceAll(forceTeam: true);
                lastReadyProgress = null;
                MaintainWarmup();
                AnnounceReadyProgress();
                var warming = session;
                AddTimer(1f, () =>
                {
                    if (unloaded || session != warming || warming.Phase != MatchPhase.Warmup) return;
                    ApplyCvars(warming.Contract);
                    MaintainWarmup();
                    EnforceAll(forceTeam: true);
                    AnnounceReadyProgress();
                });
            }
            catch (Exception ex) { Console.WriteLine($"[ArenaMatch] next map setup failed: {ex.Message}"); ClearMatch(true); }
            finally { LogStage("next_map_setup", matchId, started, always: true); }
            return;
        }
        if (session != null && !string.Equals(mapName, session.CurrentMap, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ArenaMatch] unexpected map {mapName}; match {session.Contract.MatchId} suspended");
            ClearMatch(forceResult: true);
        }
    }

    private void ApplyLoaded(int attempt, LoadedMatch loaded)
    {
        if (unloaded || attempt != generation || loadingMatchId != loaded.Contract.MatchId) return;
        var started = Stopwatch.GetTimestamp();
        try
        {
            ApplyCvars(loaded.Contract);
            ResetMatchPause();
            HoldWarmupTimer();
            lastReadyProgress = null;
            session = new MatchSession(loaded.Contract, loaded.Sha256);
            idleWarmup = false;
            var active = session;
            AddTimer(1f, () =>
            {
                if (unloaded || session != active || active.Phase is not (MatchPhase.WaitingForAck or MatchPhase.Warmup)) return;
                ApplyCvars(active.Contract);
                MaintainWarmup();
            });
            var duelRules = DuelRules.Parse(loaded.Contract.Cvars);
            duel = duelRules.Enabled ? new DuelMode(session, duelRules, () => unloaded ? null : session,
                (delay, callback) => AddTimer(delay, callback), Console.WriteLine,
                () => tacticalPauseSecondsRemaining > 0 || DateTime.UtcNow < tacticalPauseEndHudHoldUntilUtc) : null;
            demo = loaded.Contract.RecordDemo ? new MatchDemoRecorder(session, gameDirectory, SocketPath,
                IsCurrentRecording, lifetime.Token, Console.WriteLine) : null;
            pendingMap = null;
            loadingMatchId = 0;
            ApplyBotConfig();
            EnforceAll();
            Console.WriteLine($"[ArenaMatch] match={loaded.Contract.MatchId} applied; waiting for backend ack");
            _ = Task.Run(() => ReportAndPollAsync(attempt, loaded));
        }
        catch (Exception ex)
        {
            RemoveMatchBots();
            RestoreCvars();
            session = null;
            duel = null;
            demo = null;
            FailLoad(attempt, loaded.Contract.MatchId, loaded.Sha256, "apply_failed", ex.Message);
        }
        finally { LogStage("apply_loaded", loaded.Contract.MatchId, started, always: true); }
    }

    private static void LogStage(string stage, long matchId, long started, bool always = false)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (always || elapsed >= 20)
            Console.WriteLine($"[ArenaMatch] stage={stage} match={matchId} elapsed_ms={elapsed:F2}");
    }

    private void ApplyCvars(MatchContract contract)
    {
        foreach (var (name, fallback) in new Dictionary<string, string>
        {
            ["mp_maxrounds"] = "24", ["mp_halftime"] = "1", ["mp_match_can_clinch"] = "1",
            ["mp_overtime_enable"] = "1", ["mp_overtime_maxrounds"] = "6",
            ["mp_overtime_startmoney"] = "16000",
        }) SetMatchCvar(name, contract.Cvars.GetValueOrDefault(name, fallback));
        SetMatchCvar("mp_match_end_changelevel", "0");
        SetMatchCvar("mp_match_end_restart", "0");
        SetMatchCvar("mp_match_restart_delay", "65");
        SetMatchCvar("mp_endmatch_votenextmap", "0");
        foreach (var (name, value) in contract.Cvars)
        {
            if (name == "matchzy_match_start_message") continue; // 系列真正开赛时才公告。
            if (name.StartsWith("arena_duel_", StringComparison.Ordinal)) continue; // 插件内部规则，不是引擎 cvar。
            if (ConVar.Find(name) == null) throw new InvalidOperationException($"unknown cvar: {name}");
            SetMatchCvar(name, value);
        }
        ApplyTeamNames();
    }

    private void ApplyTeamNames()
    {
        if (session == null) return;
        SetMatchCvar("mp_teamname_1", session.CTTeamName);
        SetMatchCvar("mp_teamname_2", session.TTeamName);
    }

    private static void MaintainNoIdleKick()
    {
        // 实例空闲/热身/正式比赛均保留挂机玩家；地图 cfg 执行后再次维护。
        if (ConVar.Find("mp_autokick") is { } cvar && cvar.GetPrimitiveValue<bool>())
            cvar.SetValue(false);
    }

    private void HoldWarmupTimer()
    {
        if (ConVar.Find("mp_warmup_pausetimer") == null)
            throw new InvalidOperationException("required cvar mp_warmup_pausetimer is unavailable");
        SetMatchCvar("mp_warmup_pausetimer", "1");
    }

    private void MaintainWarmup()
    {
        if (unloaded || (!idleWarmup && session?.Phase is not (MatchPhase.WaitingForAck or MatchPhase.Warmup or MatchPhase.KnifeChoice))) return;
        ApplyTeamNames();
        var paused = ConVar.Find("mp_warmup_pausetimer");
        if (paused == null) return;
        if (paused.GetPrimitiveValue<int>() != 1) HoldWarmupTimer();
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
        if (rules is { WarmupPeriod: false })
        {
            Server.ExecuteCommand("mp_warmup_start");
            Console.WriteLine($"[ArenaMatch] engine warmup restored: match={session?.Contract.MatchId} phase={session?.Phase}");
        }
    }

    private void SetMatchCvar(string name, string value)
    {
        var cvar = ConVar.Find(name);
        if (cvar == null) return;
        if (!originalCvars.ContainsKey(name)) originalCvars[name] = cvar.StringValue;
        if (cvar.StringValue != value) cvar.StringValue = value;
    }

    private void RestoreCvars()
    {
        foreach (var (name, value) in originalCvars)
        {
            try { if (ConVar.Find(name) is { } cvar) cvar.StringValue = value; }
            catch (Exception ex) { Console.WriteLine($"[ArenaMatch] restore {name} failed: {ex.Message}"); }
        }
        originalCvars.Clear();
    }

    private void ApplyBotConfig()
    {
        // 后端每场覆盖同一份 cfg；普通房是空文件。对可能被 bot cfg 改写的引擎 cvar
        // 留下原值，避免本场结束后影响下一个普通房。
        foreach (var name in new[] { "bot_quota", "mp_autoteambalance", "mp_limitteams", "mp_teamlogo_2",
            "bv_smoke_mode", "bot_aim", "bot_nades" })
        {
            var cvar = ConVar.Find(name);
            if (cvar != null) originalCvars.TryAdd(name, cvar.StringValue);
        }
        Server.ExecuteCommand("exec arena_bots.cfg");
    }

    private void RemoveMatchBots()
    {
        if (session?.Contract is not { } contract ||
            (contract.Team1Players.Count != 0 && contract.Team2Players.Count != 0)) return;
        try { Server.ExecuteCommand("bot_kick"); }
        catch (Exception ex) { Console.WriteLine($"[ArenaMatch] bot cleanup failed: {ex.Message}"); }
    }

    private void FailLoad(int attempt, long matchId, string? sha256, string code, string reason)
    {
        if (unloaded || attempt != generation) return;
        pendingMap = null;
        loadingMatchId = 0;
        Console.WriteLine($"[ArenaMatch] load failed: match={matchId}, code={code}, reason={reason}");
        if (sha256 != null)
            _ = Task.Run(() => ReportFailureAsync(attempt, matchId, sha256, code, reason));
    }

    private async Task ReportFailureAsync(int attempt, long matchId, string sha256, string code, string reason)
    {
        while (!lifetime.IsCancellationRequested && attempt == Volatile.Read(ref generation))
        {
            try
            {
                await BridgeIpcClient.ReportLoadAsync(SocketPath(), matchId, sha256, false, code,
                    reason.Length > 256 ? reason[..256] : reason, lifetime.Token);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Console.WriteLine($"[ArenaMatch] failure report deferred: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReportAndPollAsync(int attempt, LoadedMatch loaded)
    {
        var matchId = loaded.Contract.MatchId;
        while (!lifetime.IsCancellationRequested && attempt == Volatile.Read(ref generation))
        {
            try
            {
                await BridgeIpcClient.ReportLoadAsync(SocketPath(), matchId, loaded.Sha256, true,
                    cancellationToken: lifetime.Token);
                break;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Console.WriteLine($"[ArenaMatch] load report deferred: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
        while (!lifetime.IsCancellationRequested && attempt == Volatile.Read(ref generation))
        {
            try
            {
                var status = await BridgeIpcClient.GetLoadStatusAsync(SocketPath(), matchId, loaded.Sha256, lifetime.Token);
                if (status.Acked && status.Status == "loaded")
                {
                    Server.NextFrame(() =>
                    {
                        if (!unloaded && attempt == generation && session?.Contract.MatchId == matchId &&
                            session.Sha256 == loaded.Sha256 && session.Phase == MatchPhase.WaitingForAck)
                        {
                            session.Acknowledge();
                            QueueEvent(MatchEvents.SeriesStart(session));
                            AnnounceReadyProgress();
                            Console.WriteLine($"[ArenaMatch] match={matchId} backend acked; warmup ready enabled");
                        }
                    });
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Console.WriteLine($"[ArenaMatch] ack poll deferred: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void EnforceSlot(int slot)
    {
        if (unloaded) return;
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player != null && player.IsValid) EnforcePlayer(player);
        AnnounceReadyProgress();
    }

    private void EnforceAll(bool forceTeam = false)
    {
        foreach (var player in Utilities.GetPlayers())
            if (player != null && player.IsValid) EnforcePlayer(player, forceTeam);
    }

    private void EnforcePlayer(CCSPlayerController player, bool forceTeam = false)
    {
        if (player.IsBot || player.IsHLTV || player.SteamID == 0) return;
        if (session == null)
        {
            if (loadingMatchId == 0 && !AdminManager.PlayerHasPermissions(player, "@css/generic"))
                player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECTED_BY_GAME);
            return;
        }
        var id = player.SteamID.ToString();
        var role = session.RoleOf(id);
        if (role == RosterRole.None)
        {
            player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECTED_BY_GAME);
            return;
        }
        var name = session.DisplayNameOf(id);
        ApplyPlayerName(player, name);
        if (session.Phase is MatchPhase.MapEnd or MatchPhase.SeriesEnd or MatchPhase.MapChanging) return;
        // 引擎半场换边会自行交换队伍；仅在装载/刀局选边时强制分队。
        if (!forceTeam && session.Phase == MatchPhase.Live && role != RosterRole.Spectator &&
            player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist) return;
        var team1Ct = session.Team1IsCT;
        var desired = role switch
        {
            RosterRole.Spectator => CsTeam.Spectator,
            RosterRole.Team1 => team1Ct ? CsTeam.CounterTerrorist : CsTeam.Terrorist,
            _ => team1Ct ? CsTeam.Terrorist : CsTeam.CounterTerrorist,
        };
        if (player.Team != desired) player.ChangeTeam(desired);
    }

    private static void ApplyPlayerName(CCSPlayerController player, string? name)
    {
        if (name == null || player.PlayerName == name) return;
        player.PlayerName = name;
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
    }

    private void MaintainPlayerNames()
    {
        if (unloaded || session == null) return;
        foreach (var player in Utilities.GetPlayers())
            if (player.IsValid && !player.IsBot && !player.IsHLTV && player.SteamID != 0)
                ApplyPlayerName(player, session.DisplayNameOf(player.SteamID.ToString()));
    }

    private HookResult OnJoinTeam(CCSPlayerController? player, CommandInfo command)
    {
        if (session == null || player == null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;
        var id = player.SteamID.ToString();
        var role = session.RoleOf(id);
        if (role == RosterRole.None) return HookResult.Stop;
        var team1Ct = session.Team1IsCT;
        var allowed = role switch
        {
            RosterRole.Spectator => "1",
            RosterRole.Team1 => team1Ct ? "3" : "2",
            RosterRole.Team2 => team1Ct ? "2" : "3",
            _ => "",
        };
        if (command.GetArg(1) == allowed) return HookResult.Continue;
        player.PrintToChat(ArenaChat.Message("本场队伍已由房间名单固定"));
        return HookResult.Stop;
    }

    private void QueueEvent(JsonElement payload)
    {
        var matchId = payload.GetProperty("matchid").GetInt64();
        if (eventOutbox?.Enqueue(matchId, payload) != true)
            Console.WriteLine($"[ArenaMatch] event queue unavailable: match={matchId}");
    }

    private void SetKnifeCvar(string name, string value)
    {
        var cvar = ConVar.Find(name);
        if (cvar == null) return;
        knifeCvars.TryAdd(name, cvar.StringValue);
        cvar.StringValue = value;
    }

    private void RestoreKnifeCvars()
    {
        foreach (var (name, value) in knifeCvars)
        {
            try { if (ConVar.Find(name) is { } cvar) cvar.StringValue = value; }
            catch (Exception ex) { Console.WriteLine($"[ArenaMatch] restore knife {name} failed: {ex.Message}"); }
        }
        knifeCvars.Clear();
    }

    private void StartCurrentMap()
    {
        if (session == null || !session.BeginMap()) return;
        var matchId = session.Contract.MatchId;
        var started = Stopwatch.GetTimestamp();
        try
        {
            ApplyTeamNames();
            if (session.Contract.Maps.Count == 3)
                Server.PrintToChatAll(ArenaChat.Message(
                    $"{ChatColors.Green}{session.Contract.Team1Name}{ChatColors.Default} VS " +
                    $"{ChatColors.Green}{session.Contract.Team2Name}{ChatColors.Default} " +
                    $"[{session.Team1SeriesScore}-{session.Team2SeriesScore}]"));
            AnnounceMatchStart(session.Contract);
            if (session.Phase == MatchPhase.Knife)
            {
                SetKnifeCvar("mp_buytime", "0");
                SetKnifeCvar("mp_free_armor", "1");
                SetKnifeCvar("mp_startmoney", "0");
                SetKnifeCvar("mp_maxmoney", "0");
                SetKnifeCvar("mp_give_player_c4", "0");
                SetKnifeCvar("mp_ct_default_primary", "");
                SetKnifeCvar("mp_ct_default_secondary", "");
                SetKnifeCvar("mp_t_default_primary", "");
                SetKnifeCvar("mp_t_default_secondary", "");
                SetKnifeCvar("mp_weapons_allow_map_placed", "0");
            }
            AnnounceOpening(session.Phase == MatchPhase.Knife);
            Server.ExecuteCommand("mp_warmup_end");
            if (session.Phase == MatchPhase.Live) demo?.StartMap();
            Server.ExecuteCommand("mp_restartgame 1");
            Console.WriteLine($"[ArenaMatch] match={session.Contract.MatchId} map={session.MapNumber} phase={session.Phase}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ArenaMatch] map start failed: {ex.Message}");
            ClearMatch(forceResult: true);
        }
        finally { LogStage("start_map", matchId, started, always: true); }
    }

    private static void AnnounceMatchStart(MatchContract contract)
    {
        if (!contract.Cvars.TryGetValue("matchzy_match_start_message", out var message)) return;
        foreach (var line in message.Split("$$$", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var chat = line.Replace("{Green}", ChatColors.Green.ToString(), StringComparison.Ordinal)
                .Replace("{Red}", ChatColors.Red.ToString(), StringComparison.Ordinal)
                .Replace("{Default}", ChatColors.Default.ToString(), StringComparison.Ordinal);
            Server.PrintToChatAll(ArenaChat.Message(chat));
        }
    }

    private void AnnounceOpening(bool knife)
    {
        Server.PrintToChatAll(ArenaChat.Message("赛事已开启"));
        for (var i = 0; i < 3; i++) Server.PrintToChatAll(ArenaChat.Message(knife ? "Knife!" : "Go!"));
        Server.PrintToChatAll(ArenaChat.Message(
            $"Arena {ChatColors.Green}v{ModuleVersion}{ChatColors.Default} by {ChatColors.Green}CubeLight{ChatColors.Default}"));
        if (!knife) Server.PrintToChatAll(ArenaChat.Message("GL & HF!"));
    }

    private void MaintainKnifeMoney()
    {
        if (unloaded || session?.Phase != MatchPhase.Knife) return;
        foreach (var player in Utilities.GetPlayers())
            if (player.IsValid && !player.IsHLTV && player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist)
                ZeroKnifeMoney(player);
    }

    private static void ZeroKnifeMoney(CCSPlayerController player)
    {
        if (player.InGameMoneyServices is not { } money) return;
        money.Account = 0;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    private void EquipKnife(CCSPlayerController player)
    {
        if (session?.Phase != MatchPhase.Knife || !player.IsValid ||
            player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist)) return;
        try
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn is not { IsValid: true, Health: > 0 }) return;
            ZeroKnifeMoney(player);
            player.RemoveWeapons();
            player.GiveNamedItem("weapon_knife");
            player.GiveNamedItem("item_kevlar");
            pawn.ArmorValue = 100;
            if (pawn.ItemServices is CCSPlayer_ItemServices items) items.HasHelmet = false;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        }
        catch (Exception ex) { Console.WriteLine($"[ArenaMatch] knife equip failed: {ex.Message}"); }
    }

    private void SyncTeam1Side()
    {
        if (session?.Phase != MatchPhase.Live) return;
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.IsHLTV || player.SteamID == 0) continue;
            var role = session.RoleOf(player.SteamID.ToString());
            if (role == RosterRole.Team1 && player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist)
            {
                session.SyncTeam1Side(player.Team == CsTeam.CounterTerrorist);
                return;
            }
            if (role == RosterRole.Team2 && player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist)
            {
                session.SyncTeam1Side(player.Team == CsTeam.Terrorist);
                return;
            }
        }
    }

    private static IReadOnlyDictionary<string, MatchPlayerStats> SnapshotPlayerStats()
    {
        var stats = new Dictionary<string, MatchPlayerStats>(StringComparer.Ordinal);
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV || player.SteamID == 0) continue;
            try
            {
                var matchStats = player.ActionTrackingServices?.MatchStats;
                if (matchStats == null) continue;
                stats[player.SteamID.ToString()] = new MatchPlayerStats(matchStats.Kills, matchStats.Deaths,
                    matchStats.Assists, matchStats.Damage, matchStats.HeadShotKills, matchStats.UtilityDamage);
            }
            catch (Exception) { /* 玩家刚断开时不阻断本回合事件，缺失统计用零值。 */ }
        }
        return stats;
    }

    private HookResult OnRoundStart(EventRoundStart _, GameEventInfo __)
    {
        var started = Stopwatch.GetTimestamp();
        var matchId = session?.Contract.MatchId ?? 0;
        MaintainPlayerNames();
        if (session?.Phase == MatchPhase.Live)
        {
            SyncTeam1Side();
            try { ApplyCvars(session.Contract); }
            catch (Exception ex)
            {
                Console.WriteLine($"[ArenaMatch] live cvar apply failed: {ex.Message}");
                ClearMatch(forceResult: true);
                LogStage("round_start", matchId, started);
                return HookResult.Continue;
            }
        }
        if (session?.RoundStarted() == true)
        {
            QueueEvent(MatchEvents.GoingLive(session));
            try { ApplyBotConfig(); }
            catch (Exception ex) { Console.WriteLine($"[ArenaMatch] live bot cfg failed: {ex.Message}"); ClearMatch(true); }
            try { demo?.StartMap(); }
            catch (Exception ex) { Console.WriteLine($"[ArenaMatch] GOTV start failed: {ex.Message}"); ClearMatch(true); }
        }
        if (session?.Phase == MatchPhase.Live) duel?.OnRoundStart();
        if (matchId != 0) LogStage("round_start", matchId, started);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo _)
    {
        if (session == null) return HookResult.Continue;
        var started = Stopwatch.GetTimestamp();
        var matchId = session.Contract.MatchId;
        if (session.Phase == MatchPhase.Live) SyncTeam1Side();
        var phase = session.Phase;
        var winningSide = @event.Winner;
        if (phase == MatchPhase.Knife && KnifeRules.IsTimeout(@event.Reason))
        {
            var living = Utilities.GetPlayers().Where(p => p.IsValid && !p.IsHLTV &&
                p.PlayerPawn.Value is { IsValid: true, Health: > 0 }).ToArray();
            winningSide = KnifeRules.ResolveTimeout(
                living.Where(p => p.Team == CsTeam.CounterTerrorist).Select(p => p.PlayerPawn.Value!.Health),
                living.Where(p => p.Team == CsTeam.Terrorist).Select(p => p.PlayerPawn.Value!.Health),
                Random.Shared.Next(2) == 0);
        }
        var outcome = session.RoundEnded(winningSide);
        if (phase == MatchPhase.Live) duel?.OnRoundEnd();
        if (phase == MatchPhase.Knife && session.Phase == MatchPhase.KnifeChoice)
        {
            Console.WriteLine($"[ArenaMatch] knife winner={session.KnifeWinner}; use .stay or .switch");
            var current = session;
            RestoreKnifeCvars();
            HoldWarmupTimer();
            Server.ExecuteCommand("mp_warmup_start");
            AnnounceKnifeWinner();
            var reminder = AddTimer(15f, () =>
            {
                if (session == current && current.Phase == MatchPhase.KnifeChoice) AnnounceKnifeWinner();
            }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
            // 空真人人机队无法输入命令，仍保留自动留边；真人胜队不限时等待。
            AddTimer(3f, () =>
            {
                if (session == current && current.ChooseKnifeForEmptyRoster()) FinishKnifeChoice();
            });
            knifeChoiceReminder?.Kill();
            knifeChoiceReminder = reminder;
        }
        if (outcome != null)
            QueueEvent(MatchEvents.RoundEnd(session, outcome, @event.Reason, @event.Time, SnapshotPlayerStats()));
        LogStage("round_end", matchId, started);
        return HookResult.Continue;
    }

    private void AnnounceKnifeWinner()
    {
        if (session?.Phase != MatchPhase.KnifeChoice) return;
        var team = TeamLabel(TeamNumber(session.KnifeWinner));
        Server.PrintToChatAll(ArenaChat.Message(
            $"{ChatColors.Green}{team}{ChatColors.Default} 赢下了刀局，等待他们输入 .stay 或 .switch"));
    }

    private void ChooseKnife(CCSPlayerController player, bool switchSides)
    {
        if (session == null || !session.ChooseKnife(player.SteamID.ToString(), switchSides))
        {
            player.PrintToChat(ArenaChat.Message("仅刀局获胜队可以选边"));
            return;
        }
        FinishKnifeChoice();
    }

    private void FinishKnifeChoice()
    {
        if (session?.Phase != MatchPhase.Live) return;
        QueueEvent(MatchEvents.SidePicked(session));
        knifeChoiceReminder?.Kill();
        knifeChoiceReminder = null;
        RestoreKnifeCvars();
        // 刀局起始 Team A 固定 CT；换边使用正常阵营切换，包括不在真人名单中的 bot。
        if (!session.Team1IsCT)
            foreach (var bot in Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && !p.IsHLTV &&
                p.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist))
                bot.ChangeTeam(bot.Team == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist);
        EnforceAll(forceTeam: true);
        ApplyTeamNames();
        AnnounceOpening(false);
        try { Server.ExecuteCommand("mp_warmup_end"); demo?.StartMap(); Server.ExecuteCommand("mp_restartgame 1"); }
        catch (Exception ex) { Console.WriteLine($"[ArenaMatch] knife restart failed: {ex.Message}"); ClearMatch(true); return; }
        Console.WriteLine($"[ArenaMatch] knife choice complete; team1_ct={session.Team1IsCT}");
    }

    private HookResult OnMatchWinPanel(EventCsWinPanelMatch _, GameEventInfo __)
    {
        if (session == null) return HookResult.Continue;
        var started = Stopwatch.GetTimestamp();
        var matchId = session.Contract.MatchId;
        var result = session.FinishMap();
        if (result == null) return HookResult.Continue;
        demo?.StopMap();
        QueueEvent(MatchEvents.MapResult(session, result, SnapshotPlayerStats()));
        ResetMatchPause();
        // 保留引擎胜负/比分面板直到切图或清场；提前热身会变成“比赛已取消”。
        if (result.SeriesFinished)
        {
            naturalSeriesEndPending = true;
            if (session.SeriesWinner is RosterRole.Team1 or RosterRole.Team2)
                Server.PrintToChatAll(ArenaChat.Message(
                    $"{ChatColors.Green}{TeamLabel(TeamNumber(session.SeriesWinner))}{ChatColors.Default} 赢得了本场比赛的胜利"));
            else Server.PrintToChatAll(ArenaChat.Message("本场比赛以平局结束"));
        }
        BeginTransition(session, result.SeriesFinished ? MatchTransitionKind.Close : MatchTransitionKind.ChangeMap);
        LogStage("map_end", matchId, started, always: true);
        return HookResult.Continue;
    }

    private void BeginTransition(MatchSession owner, MatchTransitionKind kind)
    {
        transitionTimer?.Kill();
        var countdown = new MatchTransition(kind);
        var mapNumber = owner.MapNumber;
        Server.PrintToChatAll(ArenaChat.Message(countdown.Message));
        transitionTimer = AddTimer(MatchTransition.ReminderSeconds, () =>
        {
            if (unloaded || session != owner || owner.MapNumber != mapNumber ||
                owner.Phase != (kind == MatchTransitionKind.Close ? MatchPhase.SeriesEnd : MatchPhase.MapEnd)) return;
            if (!countdown.Tick())
            {
                Server.PrintToChatAll(ArenaChat.Message(countdown.Message));
                return;
            }
            transitionTimer?.Kill();
            transitionTimer = null;
            if (kind == MatchTransitionKind.Close)
            {
                KickAllPlayers();
                ClearMatch(forceResult: false);
            }
            else ChangeToNextMap(owner);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static void KickAllPlayers()
    {
        foreach (var player in Utilities.GetPlayers())
            if (player.IsValid && !player.IsHLTV)
                player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
    }

    private void ChangeToNextMap(MatchSession owner)
    {
        if (session != owner || !owner.AdvanceMap()) return;
        var nextMap = owner.CurrentMap;
        var mapNumber = owner.MapNumber;
        if (!Server.IsMapValid(nextMap))
        {
            Console.WriteLine($"[ArenaMatch] next map unavailable: {nextMap}");
            ClearMatch(forceResult: true);
            return;
        }
        try
        {
            Server.ExecuteCommand($"map {nextMap}");
            AddTimer(30f, () =>
            {
                if (!unloaded && session == owner && owner.MapNumber == mapNumber && owner.Phase == MatchPhase.MapChanging)
                {
                    Console.WriteLine($"[ArenaMatch] next map change timed out: {nextMap}");
                    ClearMatch(forceResult: true);
                }
            });
        }
        catch (Exception ex) { Console.WriteLine($"[ArenaMatch] next map change failed: {ex.Message}"); ClearMatch(true); }
    }

    private HookResult OnChat(EventPlayerChat @event, GameEventInfo _)
    {
        var text = MatchChatCommands.Normalize(@event.Text);
        if (!MatchChatCommands.IsKnown(text) &&
            !int.TryParse(text, out var _selected)) return HookResult.Continue;
        var player = Utilities.GetPlayerFromUserid(@event.Userid);
        if (player != null && player.IsValid)
        {
            if (text is ".ready" or ".unready") SetReady(player, text == ".ready");
            else if (text == ".start") StartByAdmin(player, "chat .start");
            else if (text is ".stay" or ".switch" or ".swap") ChooseKnife(player, text is ".switch" or ".swap");
            else if (text is ".p" or ".pause") RequestTacticalPause(player);
            else if (text == ".tech") RequestTechnicalPause(player);
            else if (text is ".un" or ".unpause" or ".resume") VoteResumePause(player);
            else if (text == ".forceun") ForceUnpause(player);
            else duel?.OnChat(player, text);
        }
        return HookResult.Continue;
    }

    [ConsoleCommand("css_ready", "Mark yourself ready for this ArenaMatch")]
    public void OnReady(CCSPlayerController? player, CommandInfo _) { if (player != null) SetReady(player, true); }

    [ConsoleCommand("css_unready", "Remove your ArenaMatch ready state")]
    public void OnUnready(CCSPlayerController? player, CommandInfo _) { if (player != null) SetReady(player, false); }

    private void SetReady(CCSPlayerController player, bool value)
    {
        if (session == null || !session.SetReady(player.SteamID.ToString(), value))
        {
            player.PrintToChat(ArenaChat.Message("当前不能更改 ready 状态"));
            return;
        }
        player.PrintToChat(ArenaChat.ReadyResult(value));
        AnnounceReadyProgress();
        if (session.PrepareStart(ConnectedHumans()))
            StartCurrentMap();
    }

    private void AnnounceReadyProgress()
    {
        var active = session;
        if (active?.Phase != MatchPhase.Warmup)
        {
            lastReadyProgress = null;
            return;
        }

        var progress = active.GetReadyProgress(ConnectedHumans());
        var messages = new List<string>();
        if (progress.AllReady)
        {
            messages.Add(ArenaChat.Message($"{ChatColors.Green}所有参赛玩家已 ready{ChatColors.Default}，比赛即将开始"));
        }
        else
        {
            AddTeamProgress(messages, active.Contract.Team1Name, progress.Team1);
            AddTeamProgress(messages, active.Contract.Team2Name, progress.Team2);
            messages.Add(ArenaChat.Message("请输入 .ready 命令进行准备!"));
        }

        var signature = string.Join('\n', messages);
        if (signature == lastReadyProgress) return;
        lastReadyProgress = signature;
        foreach (var message in messages) Server.PrintToChatAll(message);
    }

    private static void AddTeamProgress(List<string> messages, string label, TeamReadyProgress progress)
    {
        if (progress.Total == 0)
        {
            messages.Add(ArenaChat.TeamNeutral(label, "无真人名单（人机/空队）"));
            return;
        }

        messages.Add(ArenaChat.TeamReady(label, progress.ReadyCount, progress.Total));
        if (progress.NotConnected.Count > 0)
            messages.Add(ArenaChat.TeamMissing(label, "未连接", FormatNames(progress.NotConnected)));
        if (progress.NotReady.Count > 0)
            messages.Add(ArenaChat.TeamMissing(label, "未准备", FormatNames(progress.NotReady)));
    }

    private static string FormatNames(IReadOnlyList<string> names)
    {
        const int maxNames = 5;
        var shown = string.Join("、", names.Take(maxNames));
        return names.Count > maxNames ? $"{shown} 等 {names.Count - maxNames} 人" : shown;
    }

    private static IEnumerable<string> ConnectedHumans() => Utilities.GetPlayers()
        .Where(p => p != null && p.IsValid && !p.IsBot && !p.IsHLTV && p.SteamID != 0)
        .Select(p => p.SteamID.ToString());

    [ConsoleCommand("css_start", "Manually start an acknowledged ArenaMatch")]
    public void OnStart(CCSPlayerController? player, CommandInfo _) => StartByAdmin(player, "css_start");

    [ConsoleCommand("arena_match_status", "Inspect match phase, warmup, admin permission and pawn positions")]
    public void OnStatus(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null) return;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        Console.WriteLine($"[ArenaMatch] status match={session?.Contract.MatchId} phase={session?.Phase} pause_requested={matchPause.Requested} engine_pause={rules?.MatchWaitingForResume} freeze={rules?.FreezePeriod} engine_warmup={rules?.WarmupPeriod} paused={ConVar.Find("mp_warmup_pausetimer")?.StringValue}");
        if (ulong.TryParse(command.GetArg(1), out var id))
            Console.WriteLine($"[ArenaMatch] admin steamid={id} generic={AdminManager.PlayerHasPermissions(new SteamID(id), "@css/generic")}");
        foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsHLTV))
        {
            var origin = p.PlayerPawn.Value?.AbsOrigin;
            Console.WriteLine($"[ArenaMatch] pawn steamid={p.SteamID} bot={p.IsBot} team={p.Team} origin={origin}");
        }
    }

    private void StartByAdmin(CCSPlayerController? player, string source)
    {
        if (player != null && !AdminManager.PlayerHasPermissions(player, "@css/generic"))
        {
            player.PrintToChat(ArenaChat.Message("只有服务器管理员可以强制开始比赛"));
            Console.WriteLine($"[ArenaMatch] start denied: source={source} steamid={player.SteamID} permission=@css/generic");
            return;
        }
        if (session == null || !session.ForcePrepareStart())
        {
            player?.PrintToChat(ArenaChat.Message("当前比赛尚未完成后端确认或已开始"));
            Console.WriteLine($"[ArenaMatch] start rejected: source={source} no acknowledged warmup");
            return;
        }
        var actor = player == null ? "server-console" : $"{player.PlayerName} steamid={player.SteamID}";
        Console.WriteLine($"[ArenaMatch] start forced: source={source} actor={actor} match={session.Contract.MatchId}");
        Server.PrintToChatAll(ArenaChat.Message(player == null
            ? "管理员从服务端控制台强制开始比赛"
            : $"管理员 {player.PlayerName} 强制开始比赛"));
        StartCurrentMap();
    }

    [ConsoleCommand("css_pause", "Request a 30-second tactical pause; server console requests a technical pause")]
    public void OnPause(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null) RequestTechnicalPause(null);
        else RequestTacticalPause(player);
    }

    [ConsoleCommand("css_tech", "Request an unlimited technical pause")]
    public void OnTechPause(CCSPlayerController? player, CommandInfo _) => RequestTechnicalPause(player);

    [ConsoleCommand("css_unpause", "Vote to resume ArenaMatch; server console resumes directly")]
    public void OnUnpause(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null) ForceUnpause(null);
        else VoteResumePause(player);
    }

    [ConsoleCommand("css_forceun", "Force-resume an ArenaMatch pause (admin only)")]
    public void OnForceUnpause(CCSPlayerController? player, CommandInfo _) => ForceUnpause(player);

    private static int TeamNumber(RosterRole role) =>
        role == RosterRole.Team1 ? 1 : role == RosterRole.Team2 ? 2 : 0;

    private string TeamLabel(int team) => team == 1 ? session?.Contract.Team1Name ?? "Team A"
        : team == 2 ? session?.Contract.Team2Name ?? "Team B" : "管理员";

    private static int RegulationRoundLimit()
    {
        var cvar = ConVar.Find("mp_maxrounds");
        var configured = cvar?.GetPrimitiveValue<int>() ?? 0;
        return configured > 0 ? configured : 24;
    }

    private void RequestTacticalPause(CCSPlayerController? player)
    {
        if (session is not { Phase: MatchPhase.Live } active)
        {
            player?.PrintToChat(ArenaChat.Message("正式比赛阶段才能申请暂停"));
            return;
        }
        if (player == null)
        {
            RequestTechnicalPause(null);
            return;
        }

        var team = TeamNumber(active.RoleOf(player.SteamID.ToString()));
        if (player.IsBot || player.IsHLTV || team == 0 ||
            player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist))
        {
            player.PrintToChat(ArenaChat.Message("只有名单内且处于 CT/T 的参赛玩家可以申请战术暂停"));
            return;
        }

        var nextRound = active.RoundNumber + 1;
        var regulationRounds = RegulationRoundLimit();
        var result = matchPause.RequestTactical(team, active.MapNumber, nextRound, regulationRounds);
        if (result is TacticalPauseRequestResult.AcceptedRegular or TacticalPauseRequestResult.AcceptedOvertime)
        {
            pauseRevision++;
            tacticalPauseTimerRevision = -1;
            Server.ExecuteCommand("mp_pause_match");
            var allowance = matchPause.GetAllowance(team, active.MapNumber, nextRound, regulationRounds);
            tacticalPauseSecondsRemaining = 0;
            var remaining = allowance.IsOvertime
                ? $"本轮加时剩余暂停 {allowance.Remaining} 次"
                : $"本图常规阶段剩余暂停 {allowance.Remaining} 次";
            Server.PrintToChatAll(ArenaChat.Message(
                $"{ChatColors.Green}{TeamLabel(team)}{ChatColors.Default} 申请了战术暂停，将在冻结时间生效；{ChatColors.Green}{TeamLabel(team)}{ChatColors.Default} {remaining}。双方输入 .un 解除暂停"));
            Console.WriteLine($"[ArenaMatch] tactical pause requested match={active.Contract.MatchId} " +
                $"team={team} map={active.MapNumber} next_round={nextRound} overtime={allowance.IsOvertime} " +
                $"overtime_block={allowance.OvertimeBlock} remaining={allowance.Remaining}");
            return;
        }

        var error = result switch
        {
            TacticalPauseRequestResult.AlreadyRequested => matchPause.Kind == MatchPauseKind.Technical
                ? "技术暂停已生效或等待生效；输入 .un 恢复"
                : "战术暂停已申请",
            TacticalPauseRequestResult.RegularLimitReached => "本队本图常规战术暂停次数已用完",
            TacticalPauseRequestResult.OvertimeLimitReached => "本队本轮六局加时的战术暂停已用完",
            _ => "无法申请战术暂停",
        };
        player.PrintToChat(ArenaChat.Message(error));
    }

    private void RequestTechnicalPause(CCSPlayerController? player)
    {
        if (session is not { Phase: MatchPhase.Live } active)
        {
            player?.PrintToChat(ArenaChat.Message("正式比赛阶段才能申请技术暂停"));
            return;
        }

        var admin = player == null || AdminManager.PlayerHasPermissions(player, "@css/generic");
        var team = player == null ? 0 : TeamNumber(active.RoleOf(player.SteamID.ToString()));
        if (player != null && (player.IsBot || player.IsHLTV || team == 0 ||
            player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist)))
        {
            player.PrintToChat(ArenaChat.Message("只有名单内参赛玩家可以申请技术暂停"));
            return;
        }

        if (matchPause.Requested)
        {
            player?.PrintToChat(ArenaChat.Message(matchPause.Kind == MatchPauseKind.Technical
                ? "技术暂停已申请；输入 .un 恢复"
                : "战术暂停已申请，不能改成技术暂停；可恢复后重新申请 .tech"));
            return;
        }

        matchPause.RequestTechnical();
        pauseRevision++;
        tacticalPauseTimerRevision = -1;
        Server.ExecuteCommand("mp_pause_match");
        Server.PrintToChatAll(ArenaChat.Message(
            $"{ChatColors.Green}{TeamLabel(team)}{ChatColors.Default} 申请了技术暂停；双方输入 .un 解除暂停"));
        Console.WriteLine($"[ArenaMatch] technical pause requested match={active.Contract.MatchId} team={team} admin={admin}");
    }

    private void VoteResumePause(CCSPlayerController? player)
    {
        if (session is not { Phase: MatchPhase.Live } active)
        {
            player?.PrintToChat(ArenaChat.Message("当前没有可恢复的正式比赛"));
            return;
        }
        if (player == null)
        {
            ForceUnpause(null);
            return;
        }

        var team = TeamNumber(active.RoleOf(player.SteamID.ToString()));
        if (player.IsBot || player.IsHLTV || team == 0 ||
            player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist))
        {
            player.PrintToChat(ArenaChat.Message("只有名单内且处于 CT/T 的参赛玩家可以用 .un 投票"));
            return;
        }
        var opposingTeamEmpty = team == 1
            ? active.Contract.Team2Players.Count == 0
            : active.Contract.Team1Players.Count == 0;
        var vote = matchPause.VoteResume(team, opposingTeamEmpty);
        switch (vote)
        {
            case PauseResumeVoteResult.NoPause:
                player.PrintToChat(ArenaChat.Message("当前没有暂停"));
                break;
            case PauseResumeVoteResult.InvalidTeam:
                player.PrintToChat(ArenaChat.Message("只有参赛队伍可以投票恢复"));
                break;
            case PauseResumeVoteResult.DuplicateVote:
                player.PrintToChat(ArenaChat.Message("本队已投票，等待另一队同意"));
                break;
            case PauseResumeVoteResult.VoteRecorded:
                Server.PrintToChatAll(ArenaChat.Message(
                    $"{ChatColors.Green}{TeamLabel(team)}{ChatColors.Default} 已同意恢复，等待 {ChatColors.Green}{TeamLabel(team == 1 ? 2 : 1)}{ChatColors.Default}"));
                break;
            case PauseResumeVoteResult.Resumed:
                pauseRevision++;
                tacticalPauseTimerRevision = -1;
                tacticalPauseSecondsRemaining = 0;
                Server.ExecuteCommand("mp_unpause_match");
                Server.PrintToChatAll(ArenaChat.Message("双方同意恢复，比赛继续"));
                Console.WriteLine($"[ArenaMatch] pause resumed by vote match={active.Contract.MatchId}");
                break;
        }
    }

    private void ForceUnpause(CCSPlayerController? player)
    {
        if (player != null && (player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist) ||
            session == null || TeamNumber(session.RoleOf(player.SteamID.ToString())) == 0 ||
            !AdminManager.PlayerHasPermissions(player, "@css/generic")))
        {
            player.PrintToChat(ArenaChat.Message("只有服务器管理员可以使用 .forceun"));
            return;
        }
        if (session?.Phase != MatchPhase.Live || !matchPause.Requested)
        {
            player?.PrintToChat(ArenaChat.Message("当前没有暂停"));
            return;
        }
        var matchId = session.Contract.MatchId;
        matchPause.ForceResume();
        pauseRevision++;
        tacticalPauseTimerRevision = -1;
        tacticalPauseSecondsRemaining = 0;
        Server.ExecuteCommand("mp_unpause_match");
        var actor = player?.PlayerName ?? "服务端控制台";
        Server.PrintToChatAll(ArenaChat.Message($"管理员 {actor} 强制取消暂停，比赛继续"));
        Console.WriteLine($"[ArenaMatch] pause force-resumed match={matchId} actor={actor}");
    }

    private void MaintainTacticalPause()
    {
        if (unloaded || session is not { Phase: MatchPhase.Live } active ||
            matchPause.Kind != MatchPauseKind.Tactical)
        {
            tacticalPauseSecondsRemaining = 0;
            return;
        }
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
        // MatchWaitingForResume 在申请时就置位，只有冻结阶段才表示暂停真正生效。
        var pauseActive = rules?.MatchWaitingForResume == true;
        if (tacticalPauseTimerRevision == pauseRevision && !pauseActive)
        {
            matchPause.ForceResume();
            pauseRevision++;
            tacticalPauseTimerRevision = -1;
            tacticalPauseSecondsRemaining = 0;
            return;
        }
        if (tacticalPauseTimerRevision != pauseRevision)
        {
            if (!MatchPauseState.CanStartTacticalTimer(pauseActive, rules?.FreezePeriod == true)) return;
            tacticalPauseTimerRevision = pauseRevision;
            tacticalPauseSecondsRemaining = MatchPauseState.RegularPauseSeconds;
            ShowTacticalPauseHud(tacticalPauseSecondsRemaining);
            return;
        }

        if (tacticalPauseSecondsRemaining <= 1)
        {
            Server.ExecuteCommand("mp_unpause_match");
            ShowTacticalPauseEndedHud();
            matchPause.ForceResume();
            pauseRevision++;
            tacticalPauseTimerRevision = -1;
            tacticalPauseSecondsRemaining = 0;
            Server.PrintToChatAll(ArenaChat.Message("战术暂停已满 30 秒，比赛自动恢复"));
            Console.WriteLine($"[ArenaMatch] tactical pause timeout match={active.Contract.MatchId} engine_pause=true");
            return;
        }

        tacticalPauseSecondsRemaining--;
        ShowTacticalPauseHud(tacticalPauseSecondsRemaining);
    }

    private void MaintainDuelHud() => duel?.MaintainHud();

    // PrintToCenter has no hide operation. Stop refreshing on resume and let the last native message expire;
    // sending an empty string leaves a brief HUD marker on the client.
    private void ShowTacticalPauseHud(int secondsRemaining)
    {
        if (matchPause.Kind != MatchPauseKind.Tactical || session == null) return;
        var message = MatchPauseState.FormatTacticalHud(TeamLabel(matchPause.TacticalTeam),
            matchPause.TacticalPauseNumber, matchPause.TacticalPauseLimit, secondsRemaining);
        foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot && !p.IsHLTV))
            player.PrintToCenter(message);
    }

    private void ShowTacticalPauseEndedHud()
    {
        if (matchPause.Kind != MatchPauseKind.Tactical || session == null) return;
        tacticalPauseEndHudHoldUntilUtc = DateTime.UtcNow.AddSeconds(4);
        var message = MatchPauseState.FormatTacticalEndedHud(TeamLabel(matchPause.TacticalTeam),
            matchPause.TacticalPauseNumber, matchPause.TacticalPauseLimit);
        foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot && !p.IsHLTV))
            player.PrintToCenter(message);
    }

    private void ResetMatchPause()
    {
        knifeChoiceReminder?.Kill();
        knifeChoiceReminder = null;
        if (matchPause.Requested) Server.ExecuteCommand("mp_unpause_match");
        matchPause.Reset();
        pauseRevision++;
        tacticalPauseTimerRevision = -1;
        tacticalPauseSecondsRemaining = 0;
        tacticalPauseEndHudHoldUntilUtc = DateTime.MinValue;
    }

    [ConsoleCommand("css_endmatch", "End the active ArenaMatch")]
    public void OnEndMatch(CCSPlayerController? player, CommandInfo _)
    {
        if (player != null) return;
        ClearMatch(forceResult: true, adminEnd: true);
    }

    private void ClearMatch(bool forceResult, bool adminEnd = false)
    {
        var hadMatch = session != null || loadingMatchId != 0 || pendingMap != null;
        transitionTimer?.Kill();
        transitionTimer = null;
        if (naturalSeriesEndPending && session != null)
            QueueEvent(MatchEvents.SeriesEnd(session, session.SeriesWinner));
        naturalSeriesEndPending = false;
        ResetMatchPause();
        demo?.StopMap();
        RemoveMatchBots();
        if (forceResult && session is { Phase: not (MatchPhase.SeriesEnd or MatchPhase.WaitingForAck) } active)
            QueueEvent(MatchEvents.SeriesEnd(active, active.ForceEnd(), forced: true));
        generation++;
        loadingMatchId = 0;
        pendingMap = null;
        lastReadyProgress = null;
        session = null;
        duel = null;
        demo = null;
        RestoreKnifeCvars();
        RestoreCvars();
        if (hadMatch)
        {
            idleWarmup = true;
            if (adminEnd)
            {
                Server.PrintToChatAll(ArenaChat.Message("管理员已终止本场比赛"));
                Console.WriteLine("[ArenaMatch] 管理员已终止本场比赛");
            }
            // 清空插件赛事状态后也要重置引擎，停止正式计分并留在无限热身。
            Server.ExecuteCommand("mp_warmup_pausetimer 1");
            Server.ExecuteCommand("mp_warmup_start");
            Server.ExecuteCommand("mp_restartgame 1");
        }
        Console.WriteLine("[ArenaMatch] match state cleared");
    }

    [ConsoleCommand("matchzy_addplayer", "Add a spectator to the active ArenaMatch")]
    public void OnAddSpectator(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null) return;
        var id = command.GetArg(1);
        var role = command.GetArg(2);
        var name = command.GetArg(3);
        if (session == null || role != "spec" || id.Length != 17 || !id.StartsWith("7656119", StringComparison.Ordinal) ||
            !ulong.TryParse(id, out _) || string.IsNullOrWhiteSpace(name) || name.Contains('\n') || name.Contains('\r') ||
            !session.AddSpectator(id, name))
        {
            Console.WriteLine("[ArenaMatch] matchzy_addplayer rejected");
            return;
        }
        Console.WriteLine($"[ArenaMatch] spectator added: {id}");
        var joined = Utilities.GetPlayers().FirstOrDefault(p => p != null && p.IsValid && p.SteamID.ToString() == id);
        if (joined != null) EnforcePlayer(joined);
    }

    [ConsoleCommand("arena_match_probe", "Check ArenaMatch bridge delivery for a match ID")]
    public void OnProbeMatch(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null) return;
        if (!long.TryParse(command.GetArg(1), out var matchId) || matchId <= 0)
        {
            Console.WriteLine("[ArenaMatch] usage: arena_match_probe <matchId>");
            return;
        }
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                var loaded = await BridgeIpcClient.LoadAsync(SocketPath(), matchId, lifetime.Token);
                message = $"[ArenaMatch] probe ok: match={matchId}, sha256={loaded.Sha256}, " +
                    $"maps={loaded.Contract.Maps.Count}, team1={loaded.Contract.Team1Players.Count}, " +
                    $"team2={loaded.Contract.Team2Players.Count}, spectators={loaded.Contract.Spectators.Count}";
            }
            catch (Exception ex) { message = $"[ArenaMatch] probe failed: match={matchId}, {ex.GetType().Name}: {ex.Message}"; }
            Server.NextFrame(() => { if (!unloaded) Console.WriteLine(message); });
        });
    }
}
