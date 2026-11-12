#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

// Run with --mp-ending-test --mp-sandbox-test and CT_ENDING_TEST_SAVE set to
// an isolated fixture folder. Reuses the existing in-memory PlayerPrefs patches.
internal static class SandboxModeTests {
    internal static bool Enabled => Environment.GetCommandLineArgs().Contains("--mp-sandbox-test") &&
        Environment.GetCommandLineArgs().Contains("--mp-ending-test") &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CT_ENDING_TEST_SAVE"));
    static int stage;
    static float next, deadline;
    static string Marker => Path.Combine(SavesystemPatch.mpsavefolder, "sandbox-gate-blocked");

    static void Check(bool condition, string message) {
        if (!condition) throw new Exception("[sandbox-test] FAIL " + message);
        log.l("[sandbox-test] PASS " + message);
    }

    internal static void Tick() {
        if (!Enabled || stage == 99) return;
        try {
            SteamManager.disableAchievements = true;
            if (deadline == 0f) deadline = Time.realtimeSinceStartup + 120f;
            if (Time.realtimeSinceStartup > deadline) throw new Exception("[sandbox-test] FAIL timeout stage=" + stage);
            if (!Net.IsRunning) return;
            if (stage == 0 && !Util.IsInWorld()) {
                if (Net.IsServer && CharacterReadyGate.Blocked && PreRunScript.instance != null) {
                    var announcement = ServerMain.LastStartGameAnnouncementPacket;
                    SandboxMode.Start();
                    Check(!Util.IsInWorld() && !ScavGameStartPatch.starting_game && !WorldMP.IsMenuLoadPending,
                        "unconfirmed client blocks world entry and releases loading latch");
                    // A second blocked attempt also checks that rejection releases the load latch.
                    SandboxMode.Start();
                    Check(CharacterReadyGate.Blocked, "blocked request can be retried without a loading exception");
                    Check(ServerMain.LastStartGameAnnouncementPacket.Equals(announcement), "blocked requests do not announce a start");
                    Directory.CreateDirectory(SavesystemPatch.mpsavefolder);
                    File.WriteAllText(Marker, "PASS");
                    stage = 1;
                } else if (Net.IsClient && CharacterLobby.ShouldShowPanel && File.Exists(Marker)) {
                    SandboxMode.Start(); // Must reject locally, without sending a start request.
                    Check(!Util.IsInWorld(), "client start rejected");
                    stage = 1; next = Time.realtimeSinceStartup + 3f;
                }
            } else if (stage == 1) {
                if (Net.IsClient && Time.realtimeSinceStartup >= next) {
                    Check(CharacterLobby.ConfirmPanel(), "client confirmed through lobby panel handler");
                    stage = 2;
                } else if (Net.IsServer && !CharacterReadyGate.Blocked && ScavPlayer.ClientIdToPlayerDict.Count >= 2) {
                    SandboxMode.Start();
                    Check(WorldMP.IsMenuLoadPending, "confirmed request starts loading");
                    SandboxMode.Start(); // Rapid double-click must show unavailable, not throw.
                    stage = 2;
                }
            } else if (stage == 2 && Util.IsWorldGenerated() && ScavPlayer.LocalPlayer?.body != null &&
                TutorialHandler.main?.activeCourse is SandboxCourse) {
                Check(Util.IsTutorialWorld(), "tutorial world with SandboxCourse");
                Check(!EndingFlow.Active, "ending flow inactive in sandbox");
                Check(!SaveCurrentLayerExit.Request(), "sandbox save-exit rejected");
                stage = 3;
            } else if (stage == 3) {
                var terminal = UnityEngine.Object.FindObjectOfType<TerminalScript>();
                if (terminal == null || ConsoleScript.instance == null) return;
                terminal.OnUse();
                Check(ConsoleScript.instance.active, "terminal opens console");
                Check(Con.CanCheat(), "sandbox permits console commands with release rules");
                Con.ConFailIfCheatsDisabled();
                ConsoleScript.instance.ToggleActiveState();
                Check(ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null) >= 2, "both player bodies present");
                log.l("[sandbox-test] complete side=" + (Net.IsServer ? "host" : "client"));
                stage = 99;
            }
        } catch (Exception ex) { stage = 99; log.error(ex); }
    }
}

[HarmonyPatch(typeof(Plugin), "Update")]
internal static class SandboxModeTests_TickPatch { static void Postfix() => SandboxModeTests.Tick(); }
[HarmonyPatch(typeof(EndingFlowTests), nameof(EndingFlowTests.Tick))]
internal static class SandboxModeTests_SkipEndingFixture { static bool Prefix() => !SandboxModeTests.Enabled; }
#endif
