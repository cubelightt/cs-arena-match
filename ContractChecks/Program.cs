// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net.Sockets;
using ArenaMatch;

const string sample = """
{
  "matchid": 117,
  "team1": {"id":"team_a","name":"TEAM A","players":{"76561198000000001":"Alice"}},
  "team2": {"id":"team_b","name":"TEAM B","players":{"76561198000000002":"Bob"}},
  "spectators": {"players":{"76561198000000003":"Viewer"}},
  "maplist": ["de_mirage"], "num_maps": 1, "map_sides": ["knife"],
  "players_per_team": 1, "min_players_to_ready": 1, "min_spectators_to_ready": 0,
  "skip_veto": true, "clinch_series": true, "wingman": false,
  "cvars": {"mp_friendlyfire":"1","arena_duel_roundswap":"0"}
}
""";

var checks = 0;
void Check(bool valid, string message)
{
    checks++;
    if (!valid) throw new Exception(message);
}
void Reject(Action<JsonNode> change, string label)
{
    var node = JsonNode.Parse(sample)!;
    change(node);
    try
    {
        MatchContract.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), 117);
        throw new Exception($"accepted {label}");
    }
    catch (FormatException) { checks++; }
}

var basic = MatchContract.Parse(Encoding.UTF8.GetBytes(sample), 117);
Check(basic.RecordDemo && basic.MatchId == 117 && basic.MapSides[0] == "knife", "default demo or basic parsing");
var disabled = JsonNode.Parse(sample)!;
disabled["record_demo"] = false;
Check(!MatchContract.Parse(Encoding.UTF8.GetBytes(disabled.ToJsonString()), 117).RecordDemo, "demo switch");
Reject(n => n["matchid"] = 118, "match ID mismatch");
Reject(n => n["map_sides"] = new JsonArray(), "map side count mismatch");
Reject(n => n["team2"]!["players"]!["76561198000000001"] = "Alice again", "duplicate Steam64");
Reject(n => n["cvars"]!["matchzy_remote_log_header_value"] = "secret", "credential leak");
Reject(n => n["record_demo"] = "false", "nonboolean demo flag");
Reject(n => n["skip_veto"] = false, "plugin veto");
Reject(n => n["cvars"]!["arena_duel_roundswap"] = "1", "duel missing preset");
Reject(n => n["cvars"]!["arena_duel_preset"] = "unknown", "duel unknown preset");
try
{
    MatchContract.Parse(new byte[MatchContract.MaxJsonBytes + 1], 117);
    throw new Exception("accepted oversized JSON");
}
catch (FormatException) { checks++; }
async Task<LoadedMatch> LoadFromFakeBridgeAsync(bool badDigest)
{
    var directory = Directory.CreateTempSubdirectory("arena-ipc-");
    var socketPath = Path.Combine(directory.FullName, "bridge.sock");
    try
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptAsync();
            using var stream = new NetworkStream(accepted, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var request = JsonNode.Parse((await reader.ReadLineAsync())!)!;
            Check((string?)request["op"] == "load_match" && (long?)request["matchId"] == 117,
                "IPC request shape");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sample))).ToLowerInvariant();
            if (badDigest) digest = new string('0', 64);
            var reply = JsonSerializer.Serialize(new { version = 1, ok = true, matchId = 117, sha256 = digest,
                jsonBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sample)) });
            await stream.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"));
        });
        try { return await BridgeIpcClient.LoadAsync(socketPath, 117); }
        finally { await server; }
    }
    finally { directory.Delete(recursive: true); }
}
var loaded = await LoadFromFakeBridgeAsync(false);
Check(loaded.Contract.Team1Players.Count == 1 && loaded.Sha256.Length == 64, "IPC load and digest");
try
{
    await LoadFromFakeBridgeAsync(true);
    throw new Exception("accepted bad digest");
}
catch (FormatException) { checks++; }
async Task CheckRequestAsync(string expectedOp, Func<string, Task> send)
{
    var directory = Directory.CreateTempSubdirectory("arena-ipc-");
    var socketPath = Path.Combine(directory.FullName, "bridge.sock");
    try
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptAsync();
            using var stream = new NetworkStream(accepted, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var request = JsonNode.Parse((await reader.ReadLineAsync())!)!;
            Check((string?)request["op"] == expectedOp && (long?)request["matchId"] == 117,
                $"{expectedOp} IPC request shape");
            if (expectedOp == "event")
                Check((string?)request["event"]?["event"] == "series_start", "event payload");
            if (expectedOp == "demo_ready")
                Check((int?)request["mapNumber"] == 0 && (int?)request["roundNumber"] == 16 &&
                    (string?)request["path"] == "demos/117_0.dem", "demo metadata");
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"ok\":true,\"matchId\":117}\n"));
        });
        try { await send(socketPath); }
        finally { await server; }
    }
    finally { directory.Delete(recursive: true); }
}
using var eventDocument = JsonDocument.Parse("{\"event\":\"series_start\",\"matchid\":117}");
await CheckRequestAsync("event", path => BridgeIpcClient.SendEventAsync(path, 117, eventDocument.RootElement));
await CheckRequestAsync("demo_ready", path => BridgeIpcClient.QueueDemoAsync(path, 117, 0, 16, "demos/117_0.dem"));
var session = new MatchSession(basic, new string('a', 64));
Check(session.RoleOf("76561198000000001") == RosterRole.Team1 &&
    session.RoleOf("76561198000000002") == RosterRole.Team2 &&
    session.RoleOf("76561198000000003") == RosterRole.Spectator, "roster roles");
Check(!session.SetReady("76561198000000001", true), "ready before backend ack");
session.Acknowledge();
Check(session.SetReady("76561198000000001", true) &&
    !session.PrepareStart(["76561198000000001", "76561198000000002"]), "partial ready");
var partialProgress = session.GetReadyProgress(["76561198000000001", "76561198000000002"]);
Check(partialProgress.Team1 is { ReadyCount: 1, Total: 1, NotConnected.Count: 0, NotReady.Count: 0 } &&
    partialProgress.Team2 is { ReadyCount: 0, Total: 1, NotConnected.Count: 0, NotReady.Count: 1 } &&
    partialProgress.Team2.NotReady[0] == "Bob", "ready progress separates connected unready players");
Check(!session.SetReady("76561198000000003", true), "spectator ready rejected");
Check(session.SetReady("76561198000000002", true) &&
    session.PrepareStart(["76561198000000001", "76561198000000002"]) &&
    session.Phase == MatchPhase.ReadyToStart, "both roster members ready");
session.Disconnect("76561198000000002");
Check(session.Phase == MatchPhase.Warmup && !session.AllReady(["76561198000000001"]), "disconnect clears ready");
var disconnectedProgress = session.GetReadyProgress(["76561198000000001"]);
Check(disconnectedProgress.Team2 is { ReadyCount: 0, NotConnected.Count: 1, NotReady.Count: 0 } &&
    disconnectedProgress.Team2.NotConnected[0] == "Bob", "ready progress identifies disconnected players");
Check(session.AddSpectator("76561198000000004", "Late viewer") &&
    session.RoleOf("76561198000000004") == RosterRole.Spectator &&
    !session.AddSpectator("76561198000000001", "Wrong role"), "spectator append only");
var botRoom = JsonNode.Parse(sample)!;
botRoom["team2"]!["players"] = new JsonObject();
var botContract = MatchContract.Parse(Encoding.UTF8.GetBytes(botRoom.ToJsonString()), 117);
var botSession = new MatchSession(botContract, new string('a', 64));
botSession.Acknowledge();
Check(botSession.SetReady("76561198000000001", true) &&
    botSession.PrepareStart(["76561198000000001"]), "empty bot roster counts ready");
var botProgress = botSession.GetReadyProgress(["76561198000000001"]);
Check(botProgress.AllReady && botProgress.Team2 is { ReadyCount: 0, Total: 0,
    NotConnected.Count: 0, NotReady.Count: 0 }, "empty bot roster has no missing ready players");
var forcedStart = new MatchSession(basic, new string('a', 64));
Check(!forcedStart.ForcePrepareStart(), "force start before backend ack rejected");
forcedStart.Acknowledge();
Check(forcedStart.ForcePrepareStart() && forcedStart.Phase == MatchPhase.ReadyToStart,
    "force start may bypass ready after backend ack");
async Task<LoadStatus> StatusFromFakeBridgeAsync()
{
    var directory = Directory.CreateTempSubdirectory("arena-ipc-");
    var socketPath = Path.Combine(directory.FullName, "bridge.sock");
    try
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptAsync();
            using var stream = new NetworkStream(accepted, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var request = JsonNode.Parse((await reader.ReadLineAsync())!)!;
            Check((string?)request["op"] == "load_status" && (string?)request["sha256"] == new string('a', 64),
                "load status IPC request");
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"ok\":true,\"matchId\":117,\"status\":\"loaded\",\"acked\":true}\n"));
        });
        try { return await BridgeIpcClient.GetLoadStatusAsync(socketPath, 117, new string('a', 64)); }
        finally { await server; }
    }
    finally { directory.Delete(recursive: true); }
}
var status = await StatusFromFakeBridgeAsync();
Check(status.Acked && status.Status == "loaded", "backend ack status");
var knife = new MatchSession(basic, new string('a', 64));
knife.Acknowledge();
knife.SetReady("76561198000000001", true);
knife.SetReady("76561198000000002", true);
Check(knife.PrepareStart(["76561198000000001", "76561198000000002"]) && knife.BeginMap() &&
    knife.Phase == MatchPhase.Knife, "knife map begins after ready");
knife.RoundStarted();
Check(knife.RoundEnded(3) == null && knife.Phase == MatchPhase.KnifeChoice &&
    knife.KnifeWinner == RosterRole.Team1, "knife winner mapped to team1");
Check(!knife.ChooseKnife("76561198000000002", true) &&
    knife.ChooseKnife("76561198000000001", true) && !knife.Team1IsCT, "winner-only knife side choice");
Check(MatchEvents.SidePicked(knife).GetProperty("side").GetString() == "t" &&
    MatchEvents.SidePicked(knife).GetProperty("team").GetString() == "team1", "knife side event");
Check(knife.RoundStarted() && !knife.RoundStarted(), "going_live once per map");
var round = knife.RoundEnded(2);
Check(round is { Winner: RosterRole.Team1, RoundNumber: 1, Team1Score: 1 } &&
    knife.Team1TScore == 1 && knife.Team1CTScore == 0 && knife.RoundEnded(2) == null,
    "round attribution and duplicate end guard");
var roundEvent = MatchEvents.RoundEnd(knife, round!, 7, 90);
Check(roundEvent.GetProperty("map_number").GetInt32() == 0 &&
    roundEvent.GetProperty("round_number").GetInt32() == 1 &&
    roundEvent.GetProperty("winner").GetProperty("team").GetString() == "team1" &&
    roundEvent.GetProperty("team1").GetProperty("score_t").GetInt32() == 1,
    "website round event fields");
var withStats = MatchEvents.RoundEnd(knife, round!, 7, 90,
    new Dictionary<string, MatchPlayerStats> { ["76561198000000001"] = new(2, 1, 0, 150, 1, 20) });
Check(withStats.GetProperty("team1").GetProperty("players")[0].GetProperty("kills").GetInt32() == 2 &&
    withStats.GetProperty("team1").GetProperty("players")[0].GetProperty("headshot_kills").GetInt32() == 1,
    "player match statistics in event");
var mapOutcome = knife.FinishMap();
Check(mapOutcome is { SeriesFinished: true, Team1SeriesScore: 1 } &&
    MatchEvents.MapResult(knife, mapOutcome).GetProperty("map_number").GetInt32() == 0 &&
    MatchEvents.SeriesEnd(knife, RosterRole.Team1).GetProperty("team1_series_score").GetInt32() == 1,
    "BO1 map and series results");
var bo3Node = JsonNode.Parse(sample)!;
bo3Node["maplist"] = new JsonArray("de_mirage", "de_nuke", "de_ancient");
bo3Node["map_sides"] = new JsonArray("team1_ct", "team2_ct", "knife");
bo3Node["num_maps"] = 3;
var bo3 = new MatchSession(MatchContract.Parse(Encoding.UTF8.GetBytes(bo3Node.ToJsonString()), 117), new string('a', 64));
bo3.Acknowledge();
bo3.ForcePrepareStart();
bo3.BeginMap();
bo3.RoundStarted();
bo3.RoundEnded(3);
Check(bo3.FinishMap() is { SeriesFinished: false } && bo3.AdvanceMap() &&
    bo3.MapNumber == 1 && bo3.MapLoaded() && bo3.ForcePrepareStart() && bo3.BeginMap() && !bo3.Team1IsCT, "BO3 next-map side mapping");
bo3.RoundStarted();
bo3.RoundEnded(2);
Check(bo3.FinishMap() is { SeriesFinished: true, Team1SeriesScore: 2 } &&
    bo3.MapNumber == 1, "BO3 clinch after two maps");
var interrupted = new MatchSession(basic, new string('a', 64));
interrupted.Acknowledge(); interrupted.ForcePrepareStart(); interrupted.BeginMap();
Check(interrupted.ForceEnd() == RosterRole.None &&
    MatchEvents.SeriesEnd(interrupted, RosterRole.None, forced: true).GetProperty("forced").GetBoolean(),
    "forced end has no invented winner");
var botKnife = new MatchSession(botContract, new string('a', 64));
botKnife.Acknowledge(); botKnife.ForcePrepareStart(); botKnife.BeginMap(); botKnife.RoundStarted();
botKnife.RoundEnded(2);
Check(botKnife.KnifeWinner == RosterRole.Team2 && botKnife.ChooseKnifeForEmptyRoster() &&
    botKnife.Phase == MatchPhase.Live, "empty bot team knife choice fallback");
var order = new List<string>();
var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var outbox = new MatchEventOutbox((_, payload, _) =>
{
    var name = payload.GetProperty("event").GetString()!;
    order.Add(name);
    if (name == "series_start" && order.Count == 1) throw new IOException("temporary event failure");
    if (name == "going_live") delivered.TrySetResult();
    return Task.CompletedTask;
}, _ => { }, TimeSpan.FromMilliseconds(10)))
{
    Check(outbox.Enqueue(117, MatchEvents.SeriesStart(knife)) &&
        outbox.Enqueue(117, MatchEvents.GoingLive(knife)), "event enqueue");
    await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
}
Check(order.SequenceEqual(["series_start", "series_start", "going_live"]), "event retry preserves order");
var eventJournalDir = Directory.CreateTempSubdirectory("arena-event-notice-");
try
{
    var journal = new EventNoticeJournal(eventJournalDir.FullName);
    var first = journal.Append(117, MatchEvents.SeriesStart(knife));
    var second = journal.Append(117, MatchEvents.GoingLive(knife));
    journal.Complete(first);
    var reopened = new EventNoticeJournal(eventJournalDir.FullName);
    Check(reopened.RecoverPending.Count == 1 && reopened.RecoverPending[0] == second,
        "event journal recovers only pending event");
    var replayOrder = new List<string>();
    var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var outbox = new MatchEventOutbox((_, payload, _) =>
    {
        replayOrder.Add(payload.GetProperty("event").GetString()!);
        if (replayOrder.Count == 2) replayed.TrySetResult();
        return Task.CompletedTask;
    }, _ => { }, TimeSpan.FromMilliseconds(10), eventJournalDir.FullName))
    {
        Check(outbox.Enqueue(117, MatchEvents.MapResult(knife, new MapOutcome(0, RosterRole.Team1, 1, 0, 1, 0, true),
            new Dictionary<string, MatchPlayerStats>())), "event journal accepts new event after recovery");
        await replayed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(SpinWait.SpinUntil(() => new EventNoticeJournal(eventJournalDir.FullName).RecoverPending.Count == 0,
            TimeSpan.FromSeconds(2)), "event completion is durable");
    }
    Check(replayOrder.SequenceEqual(["going_live", "map_result"]), "event replay precedes new event");
}
finally { eventJournalDir.Delete(recursive: true); }
var ordinaryRules = DuelRules.Parse(basic.Cvars);
Check(!ordinaryRules.Enabled && !ordinaryRules.RotateSpawns(1), "ordinary room leaves duel mode off");
foreach (var preset in new[] { "rifle", "pistol", "sniper" })
{
    var rules = DuelRules.Parse(new Dictionary<string, string> { ["arena_duel_roundswap"] = "1",
        ["arena_duel_preset"] = preset });
    Check(rules.Enabled && rules.Category(0) == preset && rules.Category(50) == preset, $"{preset} preset");
}
var soloRules = DuelRules.Parse(new Dictionary<string, string> { ["arena_duel_roundswap"] = "1",
    ["arena_duel_preset"] = "solo", ["arena_duel_phase_pistol"] = "10",
    ["arena_duel_phase_rifle"] = "28", ["arena_duel_phase_sniper"] = "13" });
Check(soloRules.Category(0) == "pistol" && soloRules.Category(9) == "pistol" &&
    soloRules.Category(10) == "rifle" && soloRules.Category(37) == "rifle" &&
    soloRules.Category(38) == "sniper", "solo phase boundaries");
var rifleHudRules = DuelRules.Parse(new Dictionary<string, string> { ["arena_duel_roundswap"] = "1",
    ["arena_duel_preset"] = "rifle" });
Check(rifleHudRules.HudLines(15, 31) == ("长枪对决", "15/31"), "single-preset duel HUD title and progress");
Check(rifleHudRules.HudLines(15, 19) == ("长枪对决", "15/19"), "single-preset duel HUD uses configured max rounds");
var soloHud = soloRules.HudLines(22, 51);
Check(soloHud == ("当前为 长枪对决", "手枪 （10/10）长枪（12/28） 狙击（0/13）"),
    "solo duel HUD shows current phase and each phase progress");
Check(soloRules.HudLines(51, 51).Progress == "手枪 （10/10）长枪（28/28） 狙击（13/13）",
    "solo duel HUD caps completed phase progress");
Check(!soloRules.RotateSpawns(0) && soloRules.RotateSpawns(1) && !soloRules.RotateSpawns(2),
    "spawn rotation only on even numbered live rounds");
try { DuelRules.Parse(new Dictionary<string, string> { ["arena_duel_roundswap"] = "1" }); throw new Exception("accepted missing preset"); }
catch (FormatException) { checks++; }
try { DuelRules.Parse(new Dictionary<string, string> { ["arena_duel_roundswap"] = "1", ["arena_duel_preset"] = "bad" }); throw new Exception("accepted bad preset"); }
catch (FormatException) { checks++; }
try { DuelRules.Parse(new Dictionary<string, string> { ["arena_duel_roundswap"] = "1", ["arena_duel_preset"] = "solo",
    ["arena_duel_phase_pistol"] = "0" }); throw new Exception("accepted zero phase"); }
catch (FormatException) { checks++; }
var journalDir = Directory.CreateTempSubdirectory("arena-demo-notice-");
try
{
    var notice = DemoNotice.Create(117, 1, 19);
    DemoNoticeJournal.Write(journalDir.FullName, notice);
    var file = Path.Combine(journalDir.FullName, DemoNoticeJournal.FileName(117, 1));
    Check(DemoNoticeJournal.Read(file) == notice && DemoNoticeJournal.PendingFiles(journalDir.FullName).Count() == 1,
        "demo notice survives restart with map and round identity");
    DemoNoticeJournal.Write(journalDir.FullName, notice with { Done = true });
    Check(DemoNoticeJournal.Read(file).Done, "demo notice completion is durable");
    try { DemoNoticeJournal.Write(journalDir.FullName, notice with { Path = "../escape.dem" });
        throw new Exception("accepted invalid demo notice path"); }
    catch (FormatException) { checks++; }
}
finally { journalDir.Delete(recursive: true); }
Check(MatchChatCommands.Normalize("  。P ") == ".p" && MatchChatCommands.IsKnown(".p"),
    "chat commands normalize fullwidth dot and uppercase");
Check(MatchChatCommands.Normalize("。TeCh") == ".tech" && MatchChatCommands.IsKnown(".tech"),
    "technical pause accepts fullwidth dot and mixed case");
Check(MatchChatCommands.Normalize("。FoRcEuN") == ".forceun" && MatchChatCommands.IsKnown(".forceun"),
    "force-unpause accepts fullwidth dot and mixed case");
Check(MatchChatCommands.IsKnown(MatchChatCommands.Normalize(".UN")),
    "short unpause alias is recognized");

Check(!MatchPauseState.CanStartTacticalTimer(true, false),
    "mid-round pending pause does not start its countdown");
Check(!MatchPauseState.CanStartTacticalTimer(false, true),
    "ordinary freeze time does not start a pause countdown");
Check(MatchPauseState.CanStartTacticalTimer(true, true),
    "pending tactical pause starts counting in the next freeze time");
Check(KnifeRules.IsTimeout(12) && KnifeRules.IsTimeout(13) && !KnifeRules.IsTimeout(8),
    "knife timeout distinguishes time expiry from elimination");
Check(KnifeRules.ResolveTimeout([1, 1], [100], false) == 3 &&
    KnifeRules.ResolveTimeout([100], [1, 1], true) == 2,
    "knife survivors take priority over remaining health");
Check(KnifeRules.ResolveTimeout([30, 50], [10, 60], false) == 3 &&
    KnifeRules.ResolveTimeout([10], [20], true) == 2,
    "equal knife survivor counts use total remaining health");
Check(KnifeRules.ResolveTimeout([50], [50], true) == 3 &&
    KnifeRules.ResolveTimeout([50], [50], false) == 2,
    "equal count and health support both randomized winners");
var pendingKnife = new MatchSession(basic, new string('a', 64));
pendingKnife.Acknowledge(); pendingKnife.ForcePrepareStart(); pendingKnife.BeginMap(); pendingKnife.RoundStarted();
pendingKnife.RoundEnded(3);
Check(!pendingKnife.RoundStarted() && pendingKnife.RoundEnded(2) == null &&
    pendingKnife.Phase == MatchPhase.KnifeChoice && pendingKnife.RoundNumber == 0,
    "warmup during knife choice neither starts live nor scores rounds");
Check(pendingKnife.ChooseKnife("76561198000000001", false) && pendingKnife.Team1IsCT &&
    pendingKnife.RoundStarted(), "stay preserves actual team side before starting live");

var namedNode = JsonNode.Parse(sample)!;
namedNode["team1"]!["name"] = "红队";
namedNode["team2"]!["name"] = "蓝队";
var named = new MatchSession(MatchContract.Parse(Encoding.UTF8.GetBytes(namedNode.ToJsonString()), 117), new string('a', 64));
Check(named.Contract.Team1Name == "红队" && named.Contract.Team2Name == "蓝队" &&
    named.CTTeamName == "红队" && named.TTeamName == "蓝队", "custom team names retained and mapped to initial sides");
named.Acknowledge(); named.ForcePrepareStart(); named.BeginMap(); named.RoundStarted(); named.RoundEnded(3);
named.ChooseKnife("76561198000000001", true);
Check(named.CTTeamName == "蓝队" && named.TTeamName == "红队", "knife switch updates scoreboard team names");
named.SyncTeam1Side(true);
Check(named.CTTeamName == "红队" && named.TTeamName == "蓝队", "halftime switches names with roster sides");
MatchSession TiedRegulation(bool overtime)
{
    var node = JsonNode.Parse(sample)!;
    node["map_sides"] = new JsonArray("team1_ct");
    node["cvars"]!["mp_overtime_enable"] = overtime ? "1" : "0";
    var match = new MatchSession(MatchContract.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), 117), new string('a', 64));
    match.Acknowledge(); match.ForcePrepareStart(); match.BeginMap();
    for (var i = 0; i < 24; i++) { match.RoundStarted(); match.RoundEnded(i % 2 == 0 ? 3 : 2); }
    return match;
}
var overtimeMatch = TiedRegulation(true);
Check(overtimeMatch.FinishMap() == null && overtimeMatch.Phase == MatchPhase.Live &&
    overtimeMatch.Team1Score == 12 && overtimeMatch.Team2Score == 12, "12:12 with overtime stays live");
for (var i = 0; i < 6; i++) { overtimeMatch.RoundStarted(); overtimeMatch.RoundEnded(i < 4 ? 3 : 2); }
Check(overtimeMatch.FinishMap() is { Team1Score: 16, Team2Score: 14, Winner: RosterRole.Team1, SeriesFinished: true },
    "BO1 completes after a 16:14 overtime win");
var drawMatch = TiedRegulation(false);
var drawOutcome = drawMatch.FinishMap();
Check(drawOutcome is { Winner: RosterRole.None, SeriesFinished: true, Team1SeriesScore: 0, Team2SeriesScore: 0 } &&
    drawMatch.SeriesWinner == RosterRole.None, "disabled overtime allows natural 12:12 draw with no invented winner");
Check(MatchEvents.MapResult(drawMatch, drawOutcome!).GetProperty("winner").GetProperty("side").GetString() == "" &&
    MatchEvents.SeriesEnd(drawMatch, drawMatch.SeriesWinner).GetProperty("winner").GetProperty("team").GetString() == "",
    "draw event winner and side are empty for backend nullable winner");

var transitioning = new MatchSession(MatchContract.Parse(Encoding.UTF8.GetBytes(bo3Node.ToJsonString()), 117), new string('a', 64));
transitioning.Acknowledge(); transitioning.SetReady("76561198000000001", true); transitioning.SetReady("76561198000000002", true);
transitioning.PrepareStart(["76561198000000001", "76561198000000002"]); transitioning.BeginMap();
transitioning.RoundStarted(); transitioning.RoundEnded(3); transitioning.FinishMap();
Check(transitioning.AdvanceMap() && transitioning.Phase == MatchPhase.MapChanging && !transitioning.BeginMap() &&
    transitioning.ReadyPlayers.Count == 0 && transitioning.Team1SeriesScore == 1,
    "next map preserves series score but clears ready and refuses start while loading");
Check(transitioning.MapLoaded() && transitioning.Phase == MatchPhase.Warmup && !transitioning.BeginMap(),
    "map load enters warmup rather than automatic live");
transitioning.SetReady("76561198000000001", true);
Check(!transitioning.PrepareStart(["76561198000000001", "76561198000000002"]), "every map requires both rosters ready");
transitioning.SetReady("76561198000000002", true);
Check(!transitioning.PrepareStart(["76561198000000001"]) &&
    transitioning.PrepareStart(["76561198000000001", "76561198000000002"]) && transitioning.BeginMap(),
    "next map cannot start with disconnected ready players");
var closure = new MatchTransition(MatchTransitionKind.Close);
Check(closure.Message == "服务器将在1分钟后关闭", "close begins with one-minute message");
for (var left = 50; left >= 10; left -= 10)
    Check(!closure.Tick() && closure.RemainingSeconds == left && closure.Message == $"服务器将在{left}秒后关闭",
        "close reminder has exact ten-second spacing");
Check(closure.Tick() && closure.Completed, "closure completes at 60 seconds");
var mapCountdown = new MatchTransition(MatchTransitionKind.ChangeMap);
Check(mapCountdown.Message == "服务器将在1分钟后切换地图，请不要退出" && !mapCountdown.Tick() &&
    mapCountdown.Message == "服务器将在50秒后切换地图，请不要退出", "map change countdown keeps the stay-connected message");
var invalidBo3 = JsonNode.Parse(bo3Node.ToJsonString())!;
invalidBo3["cvars"]!["mp_overtime_enable"] = "0";
try { MatchContract.Parse(Encoding.UTF8.GetBytes(invalidBo3.ToJsonString()), 117); throw new Exception("BO3 accepted overtime off"); }
catch (FormatException) { checks++; }

var pauseState = new MatchPauseState();
Check(pauseState.VoteResume(1, false) == PauseResumeVoteResult.NoPause, "cannot resume without pause");
Check(pauseState.RequestTactical(1, 0, 1, 24) == TacticalPauseRequestResult.AcceptedRegular &&
    pauseState.Kind == MatchPauseKind.Tactical && pauseState.TacticalTeam == 1 &&
    pauseState.TacticalPauseNumber == 1 && pauseState.TacticalPauseLimit == 3,
    "tactical pause captures its team and display ordinal");
Check(MatchPauseState.FormatTacticalHud("TeamA", pauseState.TacticalPauseNumber,
    pauseState.TacticalPauseLimit, 30) == "TeamA （1/3）\n战术暂停 剩余 30 秒",
    "tactical pause HUD formats the requested two centered lines");
Check(MatchPauseState.FormatTacticalEndedHud("TeamA", pauseState.TacticalPauseNumber,
    pauseState.TacticalPauseLimit) == "TeamA （1/3）\n暂停结束 比赛继续",
    "tactical pause timeout HUD shows its team and completed message");
Check(pauseState.RequestTactical(2, 0, 1, 24) == TacticalPauseRequestResult.AlreadyRequested,
    "only one pause request can be active");
Check(pauseState.VoteResume(0, false) == PauseResumeVoteResult.InvalidTeam && pauseState.Requested,
    "spectators cannot vote to resume");
Check(pauseState.VoteResume(1, false) == PauseResumeVoteResult.VoteRecorded &&
    pauseState.VoteResume(1, false) == PauseResumeVoteResult.DuplicateVote,
    "one team vote is recorded once");
Check(pauseState.VoteResume(2, false) == PauseResumeVoteResult.Resumed && !pauseState.Requested,
    "both teams resume a tactical pause");
Check(pauseState.TacticalTeam == 0 && pauseState.TacticalPauseNumber == 0 &&
    pauseState.TacticalPauseLimit == 0, "tactical HUD identity clears when resumed");
Check(pauseState.RequestTactical(1, 0, 2, 24) == TacticalPauseRequestResult.AcceptedRegular &&
    pauseState.ForceResume(), "second regulation timeout can be force-resumed");
Check(pauseState.RequestTactical(1, 0, 3, 24) == TacticalPauseRequestResult.AcceptedRegular &&
    pauseState.ForceResume(), "third regulation timeout is available");
Check(pauseState.RequestTactical(1, 0, 4, 24) == TacticalPauseRequestResult.RegularLimitReached,
    "fourth regulation timeout is denied to the same team");
Check(pauseState.RequestTactical(2, 0, 4, 24) == TacticalPauseRequestResult.AcceptedRegular &&
    pauseState.ForceResume(), "regulation timeout budget is independent per team");
Check(pauseState.GetAllowance(2, 0, 4, 24).Remaining == 2,
    "remaining regulation timeout count is accurate");
Check(pauseState.RequestTactical(1, 0, 25, 24) == TacticalPauseRequestResult.AcceptedOvertime &&
    pauseState.ForceResume(), "one timeout is available in the first overtime block");
Check(pauseState.RequestTactical(1, 0, 30, 24) == TacticalPauseRequestResult.OvertimeLimitReached,
    "overtime timeout cannot be reused within six rounds");
Check(pauseState.RequestTactical(1, 0, 31, 24) == TacticalPauseRequestResult.AcceptedOvertime &&
    pauseState.ForceResume(), "overtime timeout resets for the next six rounds");
Check(pauseState.RequestTechnical() && pauseState.Kind == MatchPauseKind.Technical &&
    !pauseState.RequestTechnical(), "technical pause is unlimited and cannot be requested twice");
Check(pauseState.VoteResume(1, false) == PauseResumeVoteResult.VoteRecorded &&
    pauseState.VoteResume(1, false) == PauseResumeVoteResult.DuplicateVote,
    "technical pause still requires a valid resume vote");
Check(pauseState.VoteResume(2, false) == PauseResumeVoteResult.Resumed,
    "technical pause can be resumed by both teams");
Check(pauseState.RequestTactical(2, 0, 5, 24) == TacticalPauseRequestResult.AcceptedRegular &&
    pauseState.RequestTechnical() == false && pauseState.Kind == MatchPauseKind.Tactical,
    "technical pause cannot extend an already active tactical pause");
Check(pauseState.ForceResume(), "force-unpause clears the active pause");
Check(pauseState.RequestTactical(1, 1, 1, 24) == TacticalPauseRequestResult.AcceptedRegular,
    "tactical pause allowance resets on the next map");
pauseState.Reset();
Check(!pauseState.Requested && pauseState.RequestTactical(1, 0, 1, 24) ==
    TacticalPauseRequestResult.AcceptedRegular, "pause state and budgets reset at match cleanup");

Console.WriteLine($"ArenaMatch contract checks: {checks} passed");
