#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Together;

// Isolated real Unity/transport fixture. Cursor and R sampling alone are injected;
// collider configuration, target selection, health sync and panel rendering are production.
internal static class MedicalSandboxRegression {
    internal static bool Enabled => EndingTestPreferences.Enabled && Environment.GetCommandLineArgs().Contains("--mp-medical-sandbox-test");
    static bool starting, running;
    internal static bool Probe;
    static Vector2 cursor;
    static bool Baseline => Environment.GetCommandLineArgs().Contains("--mp-diagnosis");
    static string Folder => SavesystemPatch.mpsavefolder;
    static void Signal(string name) => File.WriteAllText(Path.Combine(Folder, name), "ready");
    static void Check(bool ok, string message) {
        log.l($"[medical-sandbox-test] {(ok ? "PASS" : "FAIL")} {message}");
        if (!ok) throw new Exception(message);
    }
    static IEnumerator Wait(string name) {
        float end = Time.realtimeSinceStartup + 60;
        while (!File.Exists(Path.Combine(Folder, name)) && Time.realtimeSinceStartup < end) yield return null;
        Check(File.Exists(Path.Combine(Folder, name)), "signal " + name);
    }
    internal static void Tick() {
        if (!Enabled || !Net.IsRunning) return;
        SteamManager.disableAchievements = true;
        if (Net.IsClient && CharacterLobby.ShouldShowPanel) CharacterLobby.ConfirmPanel();
        if (Net.IsServer && !starting && PreRunScript.instance != null && !CharacterReadyGate.Blocked && ScavPlayer.ClientIdToPlayerDict.Count >= 2) {
            starting = true; SandboxMode.Start();
        }
        if (running || !Util.IsWorldGenerated() || ScavPlayer.LocalPlayer?.body == null) return;
        running = true; Util.StartCoroutine(Safe(Run()));
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
    static IEnumerator Run() {
        yield return new WaitForSecondsRealtime(15);
        var local = ScavPlayer.LocalPlayer;
        var remote = ScavPlayer.ClientIdToPlayerDict.Values.Single(p => !p.IsLocal);
        float delta = Vector2.Distance(local.pos, remote.pos);
        log.l($"[medical-sandbox-test] SPAWN side={(Net.IsServer ? "host" : "client")} local={local.pos:F3} remote={remote.pos:F3} delta={delta:F3} shared={Body_PlaceBody_MultiplayerPatch.SpawnLocation:F3}");
        Check(TutorialHandler.main?.activeCourse is SandboxCourse, "sandbox course active");
        if (!Baseline) Check(delta <= 2, "sandbox bodies within two tiles on this peer");
        Signal(Net.IsServer ? "host-spawn" : "client-spawn");
        yield return Wait(Net.IsServer ? "client-spawn" : "host-spawn");
        // Constrain only after observing unmodified sandbox placement. Keep all
        // colliders and rb.simulated unchanged so picking cannot be falsified.
        foreach (var b in ScavPlayer.BodyToPlayerDict.Keys)
            foreach (var rb in b.GetComponentsInChildren<Rigidbody2D>(true)) rb.constraints = RigidbodyConstraints2D.FreezeAll;
        if (Net.IsServer) {
            local.Server_TeleportCharacter(new Vector2(0, -106));
            remote.Server_TeleportCharacter(new Vector2(2, -106));
            local.body.limbs[0].skinHealth = 37; remote.body.limbs[0].skinHealth = 73;
            local.body.hunger = 57; remote.body.hunger = 83;
            MedicalSync.Server_SendCharacterHealth(local.netBody);
            MedicalSync.Server_SendCharacterHealth(remote.netBody);
            Signal("health-sent");
        }
        yield return Wait("health-sent");
        yield return new WaitForSecondsRealtime(3);
        Check(Mathf.Abs(remote.body.limbs[0].skinHealth - (Net.IsServer ? 73 : 37)) < 1, "distinct peer wound state arrived through health sync");
        TestPanel(remote.body, "remote-root", remote.body.transform.position);
        yield return null;
        TestPanel(remote.body, "remote-torso", remote.body.GetUpperTorso().transform.position);
        yield return null;
        foreach (var limb in remote.body.limbs) {
            if (limb == null || limb.dismembered) continue;
            TestPanel(remote.body, "remote-limb-" + limb.name, limb.transform.position);
            yield return null;
        }
        TestPanel(local.body, "self", local.body.transform.position);
        yield return null;
        TestPanel(local.body, "empty-cursor", local.pos + Vector2.up * 20);
        yield return null;
        if (!Baseline) {
            Check(PlayerCamera_ToggleWoundView_MultiplayerPatch.FindCursorTarget(remote.pos + Vector2.up * 20, null) == null, "pose fallback rejects distant cursor");
            var far = remote.pos + Vector2.right * (SharedMain.max_player_interaction_distance + 20);
            var previous = remote.body.transform.position;
            try {
                // No frame advances while checking the pure selection guard.
                remote.body.transform.position = far;
                Check(PlayerCamera_ToggleWoundView_MultiplayerPatch.FindCursorTarget(far, null) == null, "pose fallback rejects player outside interaction distance");
            } finally { remote.body.transform.position = previous; }
            PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(PlayerCamera.main);
            cursor = local.pos; Probe = true;
            try { PlayerCamera_ToggleWoundView_MultiplayerPatch.OpenSpecificBody(remote.body, false); }
            finally { Probe = false; }
            Check(WoundView.view.body == remote.body, "explicit interaction target wins over cursor");
        }
        yield return new WaitForEndOfFrame();
        PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(PlayerCamera.main);
        Signal(Net.IsServer ? "host.done" : "client.done");
    }
    static void TestPanel(Body expected, string label, Vector2 point) {
        PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(PlayerCamera.main);
        cursor = point; Probe = true;
        try { PlayerCamera.main.ToggleWoundView(false); }
        finally { Probe = false; }
        var view = WoundView.view;
        view.limbLookingAt = 0; view.UpdateView();
        float food = view.foodFill.GetComponent<FilledImagePP>().fillAmount;
        log.l($"[medical-sandbox-test] PANEL {label} expected={PlayerCamera_ToggleWoundView_MultiplayerPatch.DescribeBody(expected)} actual={PlayerCamera_ToggleWoundView_MultiplayerPatch.DescribeBody(view.body)} name={view.nameText.text} selectedLimbBody={PlayerCamera_ToggleWoundView_MultiplayerPatch.DescribeBody(PlayerCamera.main.selectedLimb?.body)} skinUI={view.skinText.text} expectedSkin={Mathf.RoundToInt(expected.limbs[0].skinHealth)} foodUI={food:F3} expectedFood={expected.hunger * .01f:F3} open={view.gameObject.activeSelf}");
        if (!Baseline) {
            Check(view.gameObject.activeSelf && view.body == expected && PlayerCamera.main.selectedLimb == expected.limbs[0], label + " body and selected limb");
            Check(view.skinText.text == Mathf.RoundToInt(expected.limbs[0].skinHealth).ToString(), label + " wound data");
            Check(Mathf.Abs(food - expected.hunger * .01f) < .001f, label + " body health data");
            if (!expected.IsBodyLocal()) Check(view.nameText.text == expected.GetComponent<NetBody>().bodyName, label + " player name");
        }
        if (label == "remote-torso") ScreenCapture.CaptureScreenshot(Path.Combine(Folder, Net.IsServer ? "host-panel.png" : "client-panel.png"));
    }
    internal static bool Down(KeyCode key) => Probe ? key == KeyBinds.GetBind(CoopKeybinds.woundview) : Input.GetKeyDown(key);
    internal static Vector2 Cursor() => Probe ? cursor : Util.GetCursorWorldPos();
}
[HarmonyPatch(typeof(Plugin), "Update")]
internal static class MedicalSandboxRegressionTick { static void Postfix() => MedicalSandboxRegression.Tick(); }
[HarmonyPatch(typeof(EndingFlowTests), nameof(EndingFlowTests.Tick))]
internal static class MedicalSandboxRegressionSkipEnding { static bool Prefix() => !MedicalSandboxRegression.Enabled; }
[HarmonyPatch(typeof(WorldMP), nameof(WorldMP.Patched_WorldPlacePlayer))]
internal static class MedicalSandboxRegressionSlowGeneration {
    static void Postfix(ref IEnumerator __result) {
        if (MedicalSandboxRegression.Enabled && Net.IsServer && Environment.GetCommandLineArgs().Contains("--mp-delay-sandbox-worldplace"))
            __result = Delay(__result);
    }
    static IEnumerator Delay(IEnumerator routine) {
        log.l("[medical-sandbox-test] delaying host WorldPlacePlayer by 3s to outlast course fade");
        yield return new WaitForSecondsRealtime(3);
        while (routine.MoveNext()) yield return routine.Current;
    }
}
[HarmonyPatch(typeof(PlayerCamera_ToggleWoundView_MultiplayerPatch), nameof(PlayerCamera_ToggleWoundView_MultiplayerPatch.Prefix))]
internal static class MedicalSandboxRegressionInput {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        foreach (var c in instructions) {
            if (c.operand is MethodInfo m) {
                if (m == AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) })) c.operand = AccessTools.Method(typeof(MedicalSandboxRegression), "Down");
                if (m == AccessTools.Method(typeof(Util), nameof(Util.GetCursorWorldPos))) c.operand = AccessTools.Method(typeof(MedicalSandboxRegression), "Cursor");
            }
            yield return c;
        }
    }
}
#endif
