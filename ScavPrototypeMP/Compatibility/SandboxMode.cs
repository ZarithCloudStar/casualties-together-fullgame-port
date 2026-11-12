#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;
using System.Collections;

namespace Together;

internal static class SandboxMode {
    static SandboxCourse placedCourse;
    static Vector2 spawnLocation;
    internal static bool SpawnReady => placedCourse != null && TutorialHandler.main != null &&
        TutorialHandler.main.activeCourse == placedCourse;
    internal static Vector2 SpawnLocation => spawnLocation;
    internal static void BeginPlacement() { placedCourse = null; }
    internal static void PublishPlacement(SandboxCourse course) {
        if (!Net.IsServer || !Util.IsTutorialWorld() || ScavPlayer.LocalPlayer?.body == null) return;
        spawnLocation = ScavPlayer.LocalPlayer.pos;
        placedCourse = course;
        Body_PlaceBody_MultiplayerPatch.SpawnLocation = spawnLocation;
        Body_PlaceBody_MultiplayerPatch.has_spawn_location = true;
        log.l($"[sandbox] host room placed course={course.GetInstanceID()} authoritative={spawnLocation:F3}");
        foreach (var player in ScavPlayer.BodyToPlayerDict.Values) {
            if (player.IsLocal || player.body == null) continue;
            player.Server_TeleportCharacter(spawnLocation);
            player.body.SetVelocity(Vector2.zero);
            log.l($"[sandbox] broadcast room player={player.playerName} at={spawnLocation:F3}");
        }
    }
    static string Text(string key, string fallback) => Lang.Get(key, true) ?? fallback;

    internal static void DrawButton(float height) {
        if (!Util.GameIsFull() || !Net.IsRunning || !Net.IsServer || Util.IsInWorld()) return;
        if (GUILayout.Button(new GUIContent(Text("sandbox_button", "Sandbox mode"),
            Text("sandbox_hint", "Enter the sandbox with all players after everyone confirms their character. A terminal provides console access.")),
            GUILayout.Height(height))) Start();
    }

    internal static void Start() {
        if (!Net.IsRunning || !Net.IsServer) {
            Multiplayer.DoMultiplayerStatusMessageLog(Text("sandbox_host_only", "Only the host can start sandbox mode."));
            return;
        }
        if (!Util.GameIsFull() || Util.IsInWorld() || PreRunScript.instance == null || WorldMP.IsMenuLoadPending) {
            Multiplayer.DoMultiplayerStatusMessageLog(Text("sandbox_unavailable", "Return to the lobby and wait for the menu to finish loading."));
            return;
        }
        log.l("[sandbox] host requested sandbox");
        // WaitLoad owns the character-ready gate and the existing start announcement.
        // The network tutorial patch selects SandboxCourse on both peers.
        PreRunScript.instance.StartTutorial();
    }
}

[HarmonyPatch(typeof(SandboxCourse), nameof(SandboxCourse.Sequence))]
internal static class SandboxMode_PlacementPatch {
    static void Postfix(SandboxCourse __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) return;
        if (Net.IsServer) {
            SandboxMode.BeginPlacement();
            __result = HostSequence(__instance, __result);
        } else __result = ClientSequence();
    }
    static IEnumerator ClientSequence() {
        TutorialHandler.main.checkForEscape = false;
        TutorialHandler.main.forcePlayerAlive = false;
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        if (ScavPlayer.LocalPlayer?.body != null) ScavPlayer.LocalPlayer.body.forceWalk = false;
        log.l("[sandbox] client course awaiting authoritative room; no local placement");
    }
    static IEnumerator HostSequence(SandboxCourse course, IEnumerator sequence) {
        int step = 0;
        while (sequence.MoveNext()) {
            // 1.0.1: first yield waits for the fade; the following MoveNext places
            // the host in the room and then waits for the terminal. Publish here,
            // before that unrelated wait, so WorldPlacePlayer cannot deadlock.
            if (++step == 2) SandboxMode.PublishPlacement(course);
            yield return sequence.Current;
            if (step == 1) {
                // A slow world generator can outlast the fade. Do not let its
                // later Body.PlaceBody overwrite the room placement we publish.
                yield return new WaitUntil(() => !Net.IsServer || course == null || Util.IsWorldGenerated());
                if (!Net.IsServer || course == null) yield break;
            }
        }
    }
}

// Release defaults disable cheat commands. Sandbox console access lasts only
// while the tutorial world exists; the lobby's persisted server rules stay intact.
[HarmonyPatch(typeof(Con), nameof(Con.CanCheat))]
internal static class SandboxMode_ConsolePermissionPatch {
    static void Postfix(ref bool __result) {
        if (Util.GameIsFull() && Net.IsRunning && Util.IsTutorialWorld()) __result = true;
    }
}
#endif
