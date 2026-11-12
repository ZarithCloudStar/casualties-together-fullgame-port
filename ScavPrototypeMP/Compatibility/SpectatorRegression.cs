#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Together;

// Opt-in, real A/B transport fixture. Only input sampling and subject placement
// are synthetic; camera, ownership, death, GUI selection and respawn are production.
internal static class SpectatorRegression {
    internal static bool Enabled => EndingTestPreferences.Enabled && Environment.GetCommandLineArgs().Contains("--mp-spectator-test");
    static bool starting, running;
    static string Folder => SavesystemPatch.mpsavefolder;
    static bool Baseline => Environment.GetCommandLineArgs().Contains("--mp-spectator-baseline");
    internal static string InputBind;
    internal static int OriginalInputCalls, OriginalActionCalls;
    internal static bool ProbingActions;
    static string Id(Body b) => b == null ? "null" : b.GetComponent<NetBody>()?.bodyName + "#" + b.GetInstanceID();
    static void Check(bool ok, string message) {
        log.l("[spectator-test] " + (ok ? "PASS " : "FAIL ") + message);
        if (!ok) { File.WriteAllText(Path.Combine(Folder, "failed"), message); throw new Exception(message); }
    }
    static void Signal(string name) => File.WriteAllText(Path.Combine(Folder, name), "ready");
    static IEnumerator Wait(string name) {
        float until = Time.realtimeSinceStartup + 60;
        while (!File.Exists(Path.Combine(Folder, name)) && Time.realtimeSinceStartup < until) yield return null;
        Check(File.Exists(Path.Combine(Folder, name)), "signal " + name);
    }
    internal static void Tick() {
        if (!Enabled || !Net.IsRunning) return;
        SteamManager.disableAchievements = true;
        if (Net.IsClient && CharacterLobby.ShouldShowPanel) CharacterLobby.ConfirmPanel();
        if (Net.IsServer && !starting && PreRunScript.instance != null && !CharacterReadyGate.Blocked && ScavPlayer.ClientIdToPlayerDict.Count >= 2) {
            starting = true; PreRunScript.instance.StartRun();
        }
        if (!Util.IsWorldGenerated() || ScavPlayer.LocalPlayer?.body == null) return;
        foreach (var p in ScavPlayer.ClientIdToPlayerDict.Values.Where(p => p.body != null)) {
            p.body.rb.simulated = false;
            foreach (var l in p.body.limbs) if (l?.rb != null) l.rb.simulated = false;
            if (p.body.alive) p.body.ResetEntropy();
        }
        if (running) return;
        running = true; Util.StartCoroutine(RunSafely(Net.IsServer ? Server() : Client()));
    }
    static IEnumerator RunSafely(IEnumerator routine) {
        while (true) {
            bool next; object current;
            try { next = routine.MoveNext(); current = routine.Current; }
            catch (Exception e) { File.WriteAllText(Path.Combine(Folder, "failed"), e.ToString()); log.error(e); yield break; }
            if (!next) yield break;
            yield return current;
        }
    }
    static void Sample(string stage) {
        var cam = PlayerCamera.main;
        var target = Spectator.GetCameraFollow();
        var b = target == null ? null : target.GetComponent<Body>();
        var index = Traverse.Create(typeof(UIInGame)).Field("curspectatefolowing").GetValue<int>();
        var name = Traverse.Create(typeof(UIInGame)).Field("curspectatefolowingname").GetValue<string>();
        log.l($"[spectator-test] SAMPLE {stage} active={UIInGame.SpectatorModeActive} index={index} name={name} cameraBody={Id(cam.body)} target={Id(b)} owned={Id(ScavPlayer.LocalPlayer.body)} remoteIsLocal={b?.IsBodyLocal()} camera={cam.transform.position:F3} bodyPos={b?.transform.position.ToString("F3")} delta={(b == null ? -1 : Vector2.Distance(cam.transform.position, b.transform.position)):F3}");
        log.l("[spectator-test] PLAYERS " + string.Join("; ", ScavPlayer.BodyToPlayerDict.Values.Select(p => p.playerName + " alive=" + p.body?.alive + " local=" + p.IsLocal)));
    }
    static IEnumerator Server() {
        yield return new WaitUntil(() => ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p.body != null && p.IsAlive()) == 2);
        yield return new WaitForSecondsRealtime(5);
        var host = ScavPlayer.LocalPlayer;
        var guest = ScavPlayer.ClientIdToPlayerDict.Values.Single(p => !p.IsLocal);
        host.Server_TeleportCharacter(new Vector2(0, WorldGeneration.world.halfHeight - 30));
        guest.Server_TeleportCharacter(host.pos + Vector2.right * 20);
        Signal("placed"); yield return Wait("client-ready");
        // Real health/death synchronization, followed by the production forced-spectator receiver.
        guest.body.brainHealth = 0; guest.body.heartRate = 0; guest.body.bloodOxygen = 0; guest.body.bloodPressure = 0;
        yield return new WaitUntil(() => !guest.body.alive);
        MedicalSync.Server_SendCharacterHealth(guest.netBody);
        Multiplayer.Server_SendSimpleMessageToOneClient(NetmsgId.CLIENT_forcespectator, guest.playerId);
        yield return Wait("target-selected");
        var before = host.pos;
        for (int i = 1; i <= 60; i++) {
            host.Server_TeleportCharacter(before + Vector2.right * (i * .5f));
            yield return new WaitForSecondsRealtime(.05f);
        }
        File.WriteAllText(Path.Combine(Folder, "host-position"), host.pos.x + "," + host.pos.y);
        Signal("host-moved"); yield return Wait("client-movement-checked");
        guest.Server_RespawnCharacter(host.pos + Vector2.right * 20);
        yield return Wait("client.done");
        Signal("host.done"); log.l("[spectator-test] complete host");
    }
    static void InputProbe(PlayerCamera cam, string bind) {
        InputBind = bind;
        try { cam.HandleInput(); }
        finally { InputBind = null; }
    }
    static IEnumerator Client() {
        yield return Wait("placed");
        yield return new WaitForSecondsRealtime(2); Signal("client-ready");
        yield return new WaitUntil(() => UIInGame.SpectatorModeActive);
        var cam = PlayerCamera.main;
        var local = ScavPlayer.LocalPlayer.body;
        var host = ScavPlayer.ClientIdToPlayerDict.Values.Single(p => !p.IsLocal);
        Sample("death-entry");
        Check(!local.alive, "guest died through health sync");
        if (!Baseline) Check(cam.body == host.body, "death defaults to living HOST");
        // Same NetBody argument as the < > selector.
        UIInGame.Spectator_SetTarget(host.netBody);
        Sample("switch-host");
        Check(host.netBody.GetComponent<Body>() == host.body, "NetBody and Body share GO");
        var before = host.pos;
        Signal("target-selected");
        float maxError = 0, until = Time.realtimeSinceStartup + 30;
        int frames = 0, wrongBody = 0;
        while (!File.Exists(Path.Combine(Folder, "host-moved")) && Time.realtimeSinceStartup < until) {
            yield return new WaitForEndOfFrame();
            frames++;
            if (cam.body != host.body) wrongBody++;
            maxError = Mathf.Max(maxError, Vector2.Distance(cam.transform.position, host.pos));
        }
        Check(File.Exists(Path.Combine(Folder, "host-moved")), "HOST finished authoritative movement");
        log.l($"[spectator-test] FRAMES sampled={frames} wrongBody={wrongBody} maxCameraDelta={maxError:F6}");
        if (!Baseline) Check(frames > 30 && wrongBody == 0 && maxError < .1f, "every rendered frame follows selected HOST");
        yield return new WaitForSecondsRealtime(2);
        Sample("host-moved");
        float moved = Vector2.Distance(before, host.pos);
        log.l($"[spectator-test] OBSERVED host replica movement={moved:F3} authoritative={File.ReadAllText(Path.Combine(Folder, "host-position"))}");
        int calls = OriginalInputCalls;
        Vector3 look = host.body.targetLookPos;
        host.body.moveDir = new Vector2(.25f, .5f);
        foreach (string bind in new[] { "right", "left", "up", "down", "jump", "switchhands", "iteminteract", "attack" }) InputProbe(cam, bind);
        Sample("input-probes");
        log.l($"[spectator-test] OBSERVED originalInputCalls={OriginalInputCalls - calls} moveDir={host.body.moveDir:F3} lookChanged={look != host.body.targetLookPos}");
        if (!Baseline) {
            Check(!host.body.IsBodyLocal() && Util.GetLocalBody() == local && NetBody.GetLocalNetBodyNullable() == local.GetComponent<NetBody>(), "camera target does not acquire local authority");
            Check(moved > 25, "remote replica keeps receiving HOST motion while watched");
            Check(Vector2.Distance(cam.transform.position, host.pos) < .1f, "camera tracks moving target");
            Check(OriginalInputCalls == calls && host.body.moveDir == new Vector2(.25f, .5f) && host.body.targetLookPos == look, "WASD jump interact attack leave watched body untouched");
            int actions = OriginalActionCalls;
            ProbingActions = true;
            try {
                AccessTools.Method(typeof(PlayerCamera), "HandleDragging").Invoke(cam, new object[] { new List<UnityEngine.EventSystems.RaycastResult>(), null });
                AccessTools.Method(typeof(PlayerCamera), "HandleAttacks").Invoke(cam, null);
                cam.SwitchHands(); cam.GunRack(); cam.GunEjectMag(); cam.GunToggleSafety();
                cam.WoundSpecialAction(); cam.ApplyWoundItem(null); cam.DoBodyWorkout(0);
            } finally { ProbingActions = false; }
            Check(OriginalActionCalls == actions, "independent drag attack hand gun and medical callbacks blocked");
            MinigameBase.main.StartMinigame(new CPRMinigame(host.body), null);
            Check(MinigameBase.main.currentMinigame == null, "spectator cannot start CPR minigame");
            ScreenCapture.CaptureScreenshot(Path.Combine(Folder, "spectating-host.png"));
        }
        host.body.moveDir = Vector2.zero;
        UIInGame.spectator_only_alive = false;
        // Exercise the actual GUI direction handler (extracted production method).
        var cycle = AccessTools.Method(typeof(UIInGame), "Spectator_CycleTarget");
        if (cycle != null) {
            cycle.Invoke(null, new object[] { 1 }); Sample("cycle-corpse");
            Check(cam.body == local && Vector2.Distance(cam.transform.position, local.transform.position) < .1f, "next target immediately follows read-only corpse");
            int corpseCalls = OriginalInputCalls;
            InputProbe(cam, "right"); InputProbe(cam, "jump");
            Check(OriginalInputCalls == corpseCalls && local.moveDir == Vector2.zero, "corpse is read-only too");
            cycle.Invoke(null, new object[] { -1 }); Sample("cycle-host");
            Check(cam.body == host.body && Vector2.Distance(cam.transform.position, host.pos) < .1f, "previous target immediately follows HOST");
        }
        UIInGame.StopSpectatorMode(); Sample("manual-stop");
        Check(!UIInGame.SpectatorModeActive && cam.body == local && Spectator.GetCameraFollow() == null, "stop restores owned body and clears target");
        UIInGame.StartSpectatorMode(); Signal("client-movement-checked");
        yield return new WaitUntil(() => local.alive);
        yield return new WaitForSecondsRealtime(2); Sample("respawn");
        if (!Baseline) Check(!UIInGame.SpectatorModeActive && cam.body == local, "network respawn restores owned body automatically");
        calls = OriginalInputCalls;
        InputProbe(cam, "right");
        Check(OriginalInputCalls == calls + 1 && local.moveDir.x == 1, "respawn restores original movement input");
        local.moveDir = Vector2.zero;
        if (!Baseline) {
            var phase = EndingFlow.State.phase;
            try {
                EndingFlow.State.phase = EndingPhase.Narrative;
                UIInGame.StartSpectatorMode();
                Check(!UIInGame.SpectatorModeActive && cam.body == local, "existing narrative settlement guard still blocks spectator entry");
                calls = OriginalInputCalls; InputProbe(cam, "right");
                Check(OriginalInputCalls == calls, "existing result input lock remains active");
            } finally { EndingFlow.State.phase = phase; }
        }
        Signal("client.done"); log.l("[spectator-test] complete client");
    }
    internal static bool Key(KeyCode key, string kind) {
        if (InputBind == null) return kind == "GetKey" ? Input.GetKey(key) : kind == "GetKeyDown" ? Input.GetKeyDown(key) : Input.GetKeyUp(key);
        return kind != "GetKeyUp" && key == KeyBinds.GetBind(InputBind);
    }
    internal static bool Held(KeyCode key) => Key(key, "GetKey");
    internal static bool Down(KeyCode key) => Key(key, "GetKeyDown");
    internal static bool Up(KeyCode key) => Key(key, "GetKeyUp");
    internal static void EnterInput() { if (InputBind != null) OriginalInputCalls++; }
    internal static void EnterAction() { if (ProbingActions) OriginalActionCalls++; }
}
[HarmonyPatch(typeof(Plugin), "Update")]
internal static class SpectatorRegressionTick { static void Postfix() => SpectatorRegression.Tick(); }
[HarmonyPatch(typeof(EndingFlowTests), nameof(EndingFlowTests.Tick))]
internal static class SpectatorRegressionSkipEnding { static bool Prefix() => !SpectatorRegression.Enabled; }
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleInput))]
internal static class SpectatorRegressionInput {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        // Does not bypass production prefixes: the counter/input sample are INSIDE the original method.
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpectatorRegression), "EnterInput"));
        foreach (var c in instructions) {
            if (c.operand is MethodInfo m && m.DeclaringType == typeof(Input) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(KeyCode)) {
                string name = m.Name == "GetKey" ? "Held" : m.Name == "GetKeyDown" ? "Down" : m.Name == "GetKeyUp" ? "Up" : null;
                if (name != null) c.operand = AccessTools.Method(typeof(SpectatorRegression), name);
            }
            yield return c;
        }
    }
}
[HarmonyPatch(typeof(UIInGame), nameof(UIInGame.Spectator_SetTarget))]
internal static class SpectatorRegressionTargetTrace {
    static void Prefix(Component obj) {
        if (SpectatorRegression.Enabled) log.l("[spectator-test] TARGET " + obj + " via " + new System.Diagnostics.StackTrace(1));
    }
}
[HarmonyPatch]
internal static class SpectatorRegressionActions {
    static IEnumerable<MethodBase> TargetMethods() {
        foreach (string name in new[] { "HandleDragging", "HandleAttacks", "SwitchHands", "GunRack", "GunEjectMag", "GunToggleSafety", "WoundSpecialAction", "ApplyWoundItem", "DoBodyWorkout" })
            yield return AccessTools.Method(typeof(PlayerCamera), name);
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpectatorRegression), "EnterAction"));
        foreach (var c in instructions) yield return c;
    }
}
#endif
