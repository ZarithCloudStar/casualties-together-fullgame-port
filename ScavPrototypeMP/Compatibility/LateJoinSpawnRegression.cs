#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

// Real sequential connections: HOST+FRIEND descend; FRIEND disconnects; a new
// identity joins; test the rule off, then reconnect the saved original identity.
internal static class LateJoinSpawnRegression {
    internal static bool Enabled => EndingTestPreferences.Enabled && Environment.GetCommandLineArgs().Contains("--mp-late-spawn-test");
    static bool Diagnosis => Environment.GetCommandLineArgs().Contains("--mp-diagnosis");
    static string Phase => Environment.GetCommandLineArgs().FirstOrDefault(x => x.StartsWith("--mp-late-phase="))?.Split('=')[1] ?? "initial";
    static string Folder => SavesystemPatch.mpsavefolder;
    static bool starting, running;
    static Vector2 initialSpawn, savedFriend;
    static void Signal(string name) => File.WriteAllText(Path.Combine(Folder, name), "ready");
    static void Check(bool ok, string message) {
        log.l($"[late-spawn-test] {(ok ? "PASS" : "FAIL")} {message}");
        if (!ok) { File.WriteAllText(Path.Combine(Folder, "failed"), message); throw new Exception(message); }
    }
    static IEnumerator Wait(string marker) {
        float end = Time.realtimeSinceStartup + 90;
        while (!File.Exists(Path.Combine(Folder, marker)) && Time.realtimeSinceStartup < end) yield return null;
        Check(File.Exists(Path.Combine(Folder, marker)), "signal " + marker);
    }
    static IEnumerator Disconnected() {
        float end = Time.realtimeSinceStartup + 30;
        while (ScavPlayer.ClientIdToPlayerDict.Count > 1 && Time.realtimeSinceStartup < end) yield return null;
        Check(ScavPlayer.ClientIdToPlayerDict.Count == 1, "previous client disconnected from actual server roster");
    }
    internal static void Tick() {
        if (!Enabled || !Net.IsRunning) return;
        SteamManager.disableAchievements = true;
        // Keep transport/placement measurements independent of gravity, body
        // collisions and background keyboard input during sequential joins.
        // The fixture never assigns the observed final body positions itself.
        if (Util.IsWorldGenerated()) foreach (var b in ScavPlayer.BodyToPlayerDict.Keys) {
            if (b.alive) b.ResetEntropy();
            b.rb.simulated = false;
            foreach (var limb in b.limbs) if (limb?.rb != null) limb.rb.simulated = false;
        }
        if (Net.IsClient && CharacterLobby.ShouldShowPanel) CharacterLobby.ConfirmPanel();
        if (Net.IsServer && !starting && PreRunScript.instance != null && !CharacterReadyGate.Blocked && ScavPlayer.ClientIdToPlayerDict.Count >= 2) {
            starting = true;
            Multiplayer.rules.SpawnOnPlayer = true;
            Multiplayer.rules.LateJoinSpectate = false;
            Multiplayer.rules.SavePlayerPosition = true;
            PreRunScript.instance.StartRun();
        }
        if (running || !Util.IsWorldGenerated() || ScavPlayer.LocalPlayer?.body == null) return;
        running = true; Util.StartCoroutine(Safe(Net.IsServer ? Server() : Client()));
    }
    static IEnumerator Safe(IEnumerator routine) {
        while (true) {
            bool more; object current;
            try { more = routine.MoveNext(); current = routine.Current; }
            catch (Exception e) { File.WriteAllText(Path.Combine(Folder, "failed"), e.ToString()); log.error(e); yield break; }
            if (!more) yield break;
            yield return current;
        }
    }
    static void Room(Vector2 center) {
        var world = WorldGeneration.world;
        world.GenerateBlockCircle(center, 17, 3, 1, 0, autoUpdateChunk: true);
        world.GenerateBlockCircle(center, 11, 0, 1, 0, autoUpdateChunk: true);
        world.UpdateWorld();
    }
    static ScavPlayer Guest() => ScavPlayer.ClientIdToPlayerDict.Values.Single(p => !p.IsLocal);
    static void Sample(string side, string phase, ScavPlayer guest, ScavPlayer host) {
        log.l($"[late-spawn-test] SAMPLE {side} phase={phase} player={guest.playerName} lateJoin={guest.LateJoin} spawnOn={Multiplayer.rules.SpawnOnPlayer} guest={guest.pos:F3} host={host.pos:F3} shared={Body_PlaceBody_MultiplayerPatch.SpawnLocation:F3} distance={Vector2.Distance(guest.pos, host.pos):F3}");
    }
    static IEnumerator Server() {
        yield return new WaitForSecondsRealtime(5);
        initialSpawn = Body_PlaceBody_MultiplayerPatch.SpawnLocation;
        var host = ScavPlayer.LocalPlayer;
        var first = Guest();
        var room1 = new Vector2(0, WorldGeneration.world.halfHeight - 110);
        Room(room1);
        host.Server_TeleportCharacter(room1);
        first.Server_TeleportCharacter(room1 + Vector2.right * 3);
        yield return new WaitForSecondsRealtime(5);
        Sample("server", "initial", first, host);
        Check(Vector2.Distance(host.pos, initialSpawn) > 70, "party moved far below initial spawn");
        Signal("party-descended");
        yield return Wait("initial.observed");
        savedFriend = first.pos;
        File.WriteAllText(Path.Combine(Folder, "saved-friend.json"), JsonConvert.SerializeObject(new[] { savedFriend.x, savedFriend.y }));
        Signal("initial.leave");
        yield return Disconnected();
        Check(ServerMain.Server_LastPlayerStates.Count > 0, "original FRIEND state retained on disconnect");
        var room2 = new Vector2(40, WorldGeneration.world.halfHeight - 165);
        Room(room2); host.Server_TeleportCharacter(room2);
        yield return new WaitForSecondsRealtime(5);
        Signal("join-new");
        yield return Wait("new.observed");
        var newcomer = Guest();
        Sample("server", "new", newcomer, host);
        Check(newcomer.playerName == "FRIEND1" && newcomer.LateJoin, "new identity is a genuine late join");
        float distance = Vector2.Distance(newcomer.pos, host.pos);
        Check(Diagnosis ? distance > 50 : distance <= 2, Diagnosis ? "baseline reproduced fresh player spawning far from teammate" : "new identity spawned within two tiles of teammate on server");
        if (!Diagnosis) Check(!newcomer.ServerPlayerState.late_join_spawn_pending, "admission marker consumed after spawn");
        Signal("new.leave");
        yield return Disconnected();
        if (Diagnosis) { Signal("host.done"); yield break; }
        Multiplayer.rules.SpawnOnPlayer = false;
        Signal("join-off");
        yield return Wait("off.observed");
        var off = Guest(); Sample("server", "off", off, host);
        Check(Vector2.Distance(off.pos, initialSpawn) <= 2 && Vector2.Distance(off.pos, host.pos) > 50, "rule off retains initial spawn on server");
        Signal("off.leave");
        yield return Disconnected();
        Multiplayer.rules.SpawnOnPlayer = true;
        Signal("join-saved");
        yield return Wait("saved.observed");
        var returning = Guest(); Sample("server", "saved", returning, host);
        Check(returning.playerName == "FRIEND" && Vector2.Distance(returning.pos, savedFriend) <= 2 && Vector2.Distance(returning.pos, host.pos) > 40,
            $"saved position takes priority on server expected={savedFriend:F3} actual={returning.pos:F3}");
        Signal("saved.leave");
        yield return Disconnected();
        var state = new Server_PlayerState(host) { late_join_spawn_pending = true };
        state.OnWorldgenStart();
        Check(!state.late_join_spawn_pending, "new layer resets pending admission instead of treating it as another late join");
        Signal("host.done");
    }
    static IEnumerator Client() {
        yield return new WaitForSecondsRealtime(5);
        var local = ScavPlayer.LocalPlayer;
        var host = ScavPlayer.ClientIdToPlayerDict.Values.Single(p => p.IsHost);
        if (Phase == "initial") {
            yield return Wait("party-descended");
            yield return new WaitForSecondsRealtime(2);
            Sample("client", Phase, local, host);
            Check(Vector2.Distance(local.pos, host.pos) <= 20 && Vector2.Distance(local.pos, Body_PlaceBody_MultiplayerPatch.SpawnLocation) > 70, "initial FRIEND descended with host");
        } else {
            Sample("client", Phase, local, host);
            float distance = Vector2.Distance(local.pos, host.pos);
            if (Phase == "new") Check(Diagnosis ? distance > 50 : distance <= 2, Diagnosis ? "baseline client reproduced wrong initial spawn" : "new identity sees itself within two tiles of host");
            if (Phase == "off") Check(Vector2.Distance(local.pos, Body_PlaceBody_MultiplayerPatch.SpawnLocation) <= 2 && distance > 50, "rule off client uses initial spawn");
            if (Phase == "saved") {
                var at = JsonConvert.DeserializeObject<float[]>(File.ReadAllText(Path.Combine(Folder, "saved-friend.json")));
                Check(Vector2.Distance(local.pos, new Vector2(at[0], at[1])) <= 2 && distance > 40, "saved FRIEND client restores its own position");
            }
        }
        Signal(Phase + ".observed");
        yield return Wait(Phase + ".leave");
        Signal(Phase + ".done");
        Multiplayer.ShutdownNetwork();
    }
}
[HarmonyPatch(typeof(Plugin), "Update")]
internal static class LateJoinSpawnRegressionTick { static void Postfix() => LateJoinSpawnRegression.Tick(); }
[HarmonyPatch(typeof(EndingFlowTests), nameof(EndingFlowTests.Tick))]
internal static class LateJoinSpawnRegressionSkipEnding { static bool Prefix() => !LateJoinSpawnRegression.Enabled; }
#endif
