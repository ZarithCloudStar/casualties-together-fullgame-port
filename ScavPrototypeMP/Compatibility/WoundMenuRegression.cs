#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

// Exercise the real IMGUI interaction-menu callback and keep observing after
// Update/OnGUI/LateUpdate. Only the menu button's click result is injected.
internal static class WoundMenuRegression {
    internal static bool Enabled => EndingTestPreferences.Enabled && Environment.GetCommandLineArgs().Contains("--mp-wound-menu-test");
    static bool Diagnosis => Environment.GetCommandLineArgs().Contains("--mp-diagnosis");
    static bool starting, running, holdBodies;
    internal static bool ClickPending;
    internal static int Clicks;
    static string Folder => SavesystemPatch.mpsavefolder;
    static void Signal(string name) => File.WriteAllText(Path.Combine(Folder, name), "ready");
    static void Check(bool ok, string message) {
        log.l($"[wound-menu-test] {(ok ? "PASS" : "FAIL")} {message}");
        if (!ok) { File.WriteAllText(Path.Combine(Folder, "failed"), message); throw new Exception(message); }
    }
    static IEnumerator Wait(string name) {
        float end = Time.realtimeSinceStartup + 60;
        while (!File.Exists(Path.Combine(Folder, name)) && Time.realtimeSinceStartup < end) yield return null;
        Check(File.Exists(Path.Combine(Folder, name)), "signal " + name);
    }
    internal static void Tick() {
        if (!Enabled || !Net.IsRunning) return;
        SteamManager.disableAchievements = true;
        if (holdBodies) foreach (var b in ScavPlayer.BodyToPlayerDict.Keys)
            foreach (var rb in b.GetComponentsInChildren<Rigidbody2D>(true)) rb.constraints = RigidbodyConstraints2D.FreezeAll;
        if (Net.IsClient && CharacterLobby.ShouldShowPanel) CharacterLobby.ConfirmPanel();
        if (Net.IsServer && !starting && PreRunScript.instance != null && !CharacterReadyGate.Blocked && ScavPlayer.ClientIdToPlayerDict.Count >= 2) {
            starting = true; PreRunScript.instance.StartRun();
        }
        if (running || !Util.IsWorldGenerated() || ScavPlayer.LocalPlayer?.body == null ||
            ScavPlayer.BodyToPlayerDict.Count < 2) return;
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
        yield return new WaitForSecondsRealtime(5);
        var local = ScavPlayer.LocalPlayer;
        var remote = ScavPlayer.ClientIdToPlayerDict.Values.Single(p => !p.IsLocal);
        holdBodies = true;
        foreach (var b in ScavPlayer.BodyToPlayerDict.Keys)
            foreach (var rb in b.GetComponentsInChildren<Rigidbody2D>(true)) rb.constraints = RigidbodyConstraints2D.FreezeAll;
        Signal(Net.IsServer ? "host-ready" : "client-ready");
        yield return Wait(Net.IsServer ? "client-ready" : "host-ready");
        if (Net.IsServer) {
            var at = new Vector2(0, WorldGeneration.world.halfHeight - 20);
            WorldGeneration.world.GenerateBlockCircle(at, 14, 0, 1, 0, autoUpdateChunk: true);
            WorldGeneration.world.UpdateWorld();
            local.Server_TeleportCharacter(at);
            remote.Server_TeleportCharacter(at + Vector2.right * 3);
            local.body.limbs[0].skinHealth = 37; remote.body.limbs[0].skinHealth = 73;
            local.body.hunger = 57; remote.body.hunger = 83;
            MedicalSync.Server_SendCharacterHealth(local.netBody);
            MedicalSync.Server_SendCharacterHealth(remote.netBody);
            Signal("placed");
        }
        yield return Wait("placed");
        yield return new WaitForSecondsRealtime(3);
        Check(Mathf.Abs(remote.body.limbs[0].skinHealth - (Net.IsServer ? 73 : 37)) < 1, "peer wound received via CharacterHealth");
        PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(PlayerCamera.main);
        log.l($"[wound-menu-test] MENU guards local={PlayerCamera_ToggleWoundView_MultiplayerPatch.DescribeBody(local.body)} remote={PlayerCamera_ToggleWoundView_MultiplayerPatch.DescribeBody(remote.body)} conscious={local.body.conscious} alive={local.body.alive} spectator={UIInGame.SpectatorModeActive} clear={Util.AccurateRaycastInteractionCheckObstruction(local.body, remote.body, false)}");
        WoundView.view.limbLookingAt = 0;
        UIInGame.StartPlayerInteractionMenu(remote.netBody);
        Check(UIInGame.interaction_menu_target_body == remote.netBody, "right-click menu targets remote player");
        ClickPending = true;
        float until = Time.realtimeSinceStartup + 5;
        while (ClickPending && Time.realtimeSinceStartup < until) yield return null;
        Check(!ClickPending && Clicks == 1, "inspect-wounds click consumed by real IMGUI menu");
        Check(PlayerCamera_ToggleWoundView_MultiplayerPatch.IsOpen(), "menu opened wound panel");
        int frames = 0, wrongBody = 0, wrongLimb = 0, wrongSkin = 0, wrongFood = 0, wrongName = 0;
        until = Time.realtimeSinceStartup + 2;
        while (Time.realtimeSinceStartup < until) {
            yield return new WaitForEndOfFrame();
            var view = WoundView.view;
            frames++;
            if (view.body != remote.body) wrongBody++;
            if (view.nameText.text != remote.netBody.bodyName) wrongName++;
            if (PlayerCamera.main.selectedLimb?.body != remote.body) wrongLimb++;
            if (view.skinText.text != Mathf.RoundToInt(remote.body.limbs[view.limbLookingAt].skinHealth).ToString()) wrongSkin++;
            if (Mathf.Abs(view.foodFill.GetComponent<FilledImagePP>().fillAmount - remote.body.hunger * .01f) > .005f) wrongFood++;
        }
        log.l($"[wound-menu-test] FRAMES side={(Net.IsServer ? "host" : "client")} count={frames} wrongBody={wrongBody} wrongLimb={wrongLimb} wrongSkin={wrongSkin} wrongFood={wrongFood} wrongName={wrongName} view={PlayerCamera_ToggleWoundView_MultiplayerPatch.DescribeBody(WoundView.view.body)} name={WoundView.view.nameText.text} skin={WoundView.view.skinText.text} peerSkin={remote.body.limbs[0].skinHealth:F1}");
        if (!Diagnosis) Check(frames > 30 && wrongBody == 0 && wrongLimb == 0 && wrongSkin == 0 && wrongFood == 0 && wrongName == 0, "remote name body limbs wounds and health persist across rendered frames");
        else Check(wrongBody > 0 && wrongLimb > 0, "baseline reproduced delayed reset to owned body");
        ScreenCapture.CaptureScreenshot(Path.Combine(Folder, Net.IsServer ? "host-menu-panel.png" : "client-menu-panel.png"));
        yield return null;
        PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(PlayerCamera.main);
        PlayerCamera.main.ToggleWoundView(false);
        yield return new WaitForEndOfFrame();
        Check(WoundView.view.body == local.body && PlayerCamera.main.selectedLimb?.body == local.body, "own panel still uses owned body");
        PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(PlayerCamera.main);
        if (!Diagnosis) {
            UIInGame.StartPlayerInteractionMenu(remote.netBody);
            Check(UIInGame.interaction_menu_target_body == remote.netBody, "reopened right-click menu after own panel");
            ClickPending = true; until = Time.realtimeSinceStartup + 5;
            while (ClickPending && Time.realtimeSinceStartup < until) yield return null;
            Check(!ClickPending && Clicks == 2 && PlayerCamera_ToggleWoundView_MultiplayerPatch.IsOpen(), "second inspect click opens peer panel");
            for (int i = 0; i < 30; i++) {
                yield return new WaitForEndOfFrame();
                Check(WoundView.view.body == remote.body && WoundView.view.nameText.text == remote.netBody.bodyName &&
                    PlayerCamera.main.selectedLimb?.body == remote.body, "reopened peer panel keeps target frame=" + i);
            }
            PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(PlayerCamera.main);
        }
        Signal(Net.IsServer ? "host.done" : "client.done");
    }
}
[HarmonyPatch(typeof(Plugin), "Update")]
internal static class WoundMenuRegressionTick { static void Postfix() => WoundMenuRegression.Tick(); }
[HarmonyPatch(typeof(EndingFlowTests), nameof(EndingFlowTests.Tick))]
internal static class WoundMenuRegressionSkipEnding { static bool Prefix() => !WoundMenuRegression.Enabled; }
[HarmonyPatch(typeof(UIInGame), nameof(UIInGame.DoPlayerInteractionMenuButton))]
internal static class WoundMenuRegressionClick {
    static bool Prefix(bool enabled, in string text, ref bool focused, ref bool __result) {
        if (!WoundMenuRegression.Enabled || !WoundMenuRegression.ClickPending || !enabled || text != Lang.Get("plrint_woundview")) return true;
        WoundMenuRegression.ClickPending = false; WoundMenuRegression.Clicks++;
        focused = true; __result = true;
        log.l($"[wound-menu-test] GUI inspect click target={PlayerCamera_ToggleWoundView_MultiplayerPatch.DescribeBody(UIInGame.interaction_menu_target_body?.body)} event={Event.current?.type}");
        return false;
    }
}
#endif
