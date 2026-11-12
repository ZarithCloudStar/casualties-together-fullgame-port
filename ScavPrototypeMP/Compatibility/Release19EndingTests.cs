#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Together;

// Runs in real A/B game processes. Placement is fixture-controlled; text input
// invokes the prefab's serialized button callback, attack input is a one-frame
// local test edge. Neither changes production state or bypasses narrative waits.
internal static class Release19EndingTests {
    static string Scenario => Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") ?? "";
    internal static bool Active => EndingTestPreferences.Enabled && Scenario.StartsWith("r19-");
    static bool HostArrives => Scenario.EndsWith("-host");
    static string Folder => Path.GetDirectoryName(Environment.GetEnvironmentVariable("CT_ENDING_TEST_SAVE"));
    static string Side => Net.IsServer ? "host" : "client";
    static bool Seen(string file) => File.Exists(Path.Combine(Folder, file));
    static void Mark(string file) => File.WriteAllText(Path.Combine(Folder, file), "PASS");
    static void Check(bool pass, string message) {
        if (!pass) throw new Exception("[r19-test] FAIL " + message);
        log.l("[r19-test] PASS " + message);
    }
    static IEnumerator Wait(Func<bool> predicate, string message, float seconds = 120f) {
        float until = Time.realtimeSinceStartup + seconds;
        while (!predicate()) {
            if (Time.realtimeSinceStartup > until) throw new Exception("[r19-test] FAIL timeout " + message);
            yield return null;
        }
    }
    static void Screenshot(string name) => ScreenCapture.CaptureScreenshot(Path.Combine(Folder, Side + "-" + name + ".png"));
    internal static void Prepare() {
        if (!Active) return;
        // Several old/manual game windows can remain open. Bound this fixture's
        // rendering cost without altering any narrative or simulation timing.
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;
        Screen.SetResolution(800, 600, false);
        Release19FrameProbe.Install();
        pressStart = -100;
        log.l($"[r19-test] input setup autoNarrative={EndingFlowTests.AutoNarrative} queuedPress={pressStart} {EndingFlow.NarrativeInputState()}");
    }
    static void PlaceLocal(Action<Vector2?> hold, Vector2 pos) {
        hold(pos);
        var p = ScavPlayer.LocalPlayer;
        p.body.Heal(); p.body.ForceStand(); p.body.forceNoMovement = true;
        p.netBody.SetBodyPosition(pos); p.body.SetVelocity(Vector2.zero);
    }
    internal static IEnumerator ExitServer(Action<Vector2?> hold) {
        Check(!EndingFlowTests.AutoNarrative, "scene setup does not enable automatic narrative input");
        var host = ScavPlayer.LocalPlayer;
        var guest = ScavPlayer.ClientIdToPlayerDict.Values.First(p => !p.IsHost);
        PlaceLocal(hold, new Vector2(354f, 243f));
        Mark("r19.exit.setup");
        yield return Wait(() => Seen("r19.exit.client"), "client parked in corridor");
        // Reproduce the reported bug: the original carrier remains alive and
        // pinned as exitWalker while another player traverses the actual exit.
        EndingFlow.State.exitWalker = EndingFlow.State.cargoOwner;
        EndingFlow.State.exitFalling = false;
        log.l($"[r19-test] pinned exitWalker={EndingFlow.State.exitWalker} carrier={EndingFlow.State.cargoOwner} hostArrives={HostArrives}");
        Mark("r19.exit.start");
        if (HostArrives) yield return Traverse(hold);
        yield return Wait(() => EndingFlow.State.phase == EndingPhase.Narrative, "any arriving player triggers narrative");
        var arriver = HostArrives ? host : guest;
        Check(EndingFlow.State.exitWalker == arriver.GetPersistentId(), "exit records actual arriver regardless of pinned identity");
        Check(EndingFlow.State.ending == 2 && !EndingFlow.IsFinished, "true exit starts Escape narrative before settlement");
        hold(null);
    }
    internal static IEnumerator ExitClient(Action<Vector2?> hold) {
        yield return Wait(() => Seen("r19.exit.setup"), "exit setup");
        PlaceLocal(hold, new Vector2(354f, 243f));
        yield return new WaitForSecondsRealtime(.5f);
        Mark("r19.exit.client");
        yield return Wait(() => Seen("r19.exit.start"), "exit walker pinned");
        if (!HostArrives) yield return Traverse(hold);
        yield return Wait(() => EndingFlow.State.phase == EndingPhase.Narrative, "client receives narrative");
        hold(null);
    }
    static IEnumerator Traverse(Action<Vector2?> hold) {
        PlaceLocal(hold, new Vector2(367f, 243f));
        yield return new WaitForSecondsRealtime(1f);
        Check(EndingFlow.State.phase == EndingPhase.Escaping && EndingFlow.State.exitFalling, "x>366 opens trapdoor without ending");
        PlaceLocal(hold, new Vector2(367f, 269f));
        yield return new WaitForSecondsRealtime(.7f);
        Check(EndingFlow.State.phase == EndingPhase.Escaping, "y<=270 does not trigger true exit");
        PlaceLocal(hold, new Vector2(371f, 271f));
        yield return new WaitForSecondsRealtime(.7f);
        Check(EndingFlow.State.phase == EndingPhase.Escaping, "x>=370 does not trigger true exit");
        PlaceLocal(hold, new Vector2(Scenario.Contains("noncore") ? 352f : 367f, 271f));
    }

    static int pressStart = -100;
    static bool pressMouse;
    static int sampledPanel = -1;
    static bool sampledRevealed;
    static void AttackEdge(bool mouse = false) {
        pressStart = Time.frameCount + 1; pressMouse = mouse;
        sampledPanel = -1;
        log.l($"[r19-test] input queued {(mouse ? "mouse" : "attack")} neutral/down/held/up side={Side} startFrame={pressStart} stage={EndingFlow.NarrativeStep}");
    }
    internal static NarrativeButtons SampleButtons(NarrativeButtons physical) {
        int offset = Time.frameCount - pressStart;
        if (EndingFlowTests.AutoNarrative) { offset = Time.frameCount % 18; pressMouse = false; }
        else if (!Active || offset < 0 || offset > 7) return physical;
        var buttons = NarrativeButtons.Focused | NarrativeButtons.Fixture;
        if (offset >= 2 && offset <= 4) buttons |= pressMouse ? NarrativeButtons.MouseHeld : NarrativeButtons.AttackHeld;
        if (offset == 2) buttons |= pressMouse ? NarrativeButtons.MouseDown : NarrativeButtons.AttackDown;
        if (offset == 2 && Active && Text != null) {
            sampledPanel = Text.posIndex;
            sampledRevealed = Text.progress == Text.positions[sampledPanel];
            log.l($"[r19-test] actual down sample frame={Time.frameCount} panel={sampledPanel + 1} revealed={sampledRevealed}");
        }
        if (offset == 5) buttons |= pressMouse ? NarrativeButtons.MouseUp : NarrativeButtons.AttackUp;
        return buttons;
    }
    static ScrollableText Text => ScrollableText.textObj == null ? null : ScrollableText.textObj.GetComponent<ScrollableText>();
    static Button ClickButton(ScrollableText text) => text.GetComponentsInChildren<Button>(true).First(b =>
        Enumerable.Range(0, b.onClick.GetPersistentEventCount()).Any(i =>
            b.onClick.GetPersistentTarget(i) == text && b.onClick.GetPersistentMethodName(i) == nameof(ScrollableText.Skip)));
    static void Click(ScrollableText text) {
        var button = ClickButton(text);
        log.l($"[r19-test] input prefab Button.onClick -> ScrollableText.Skip side={Side} stage={EndingFlow.NarrativeStep} page={text.posIndex + 1}");
        button.onClick.Invoke();
    }
    static IEnumerator ReadText(string stage, bool first = false) {
        yield return Wait(() => EndingFlow.NarrativeStep == stage && Text != null, stage + " visible");
        var text = Text;
        Check(ClickButton(text) != null, stage + " prefab click is bound to Skip");
        if (first) {
            int panel = text.posIndex;
            yield return new WaitForSecondsRealtime(3f);
            // Reproduce all unsupported Skip sources with no input edge. None
            // may advance text, including a delayed click/Submit callback.
            Click(text); text.Skip();
            var button = ClickButton(text);
            button.OnSubmit(new BaseEventData(EventSystem.current));
            button.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, eligibleForClick = true });
            Check(text.posIndex == panel && !text.toClose, stage + " callback/direct Skip/Submit/delayed pointer-up without edge all rejected");
            yield return new WaitForSecondsRealtime(3f);
            Check(Text == text && text.posIndex == panel && !text.toClose && !EndingFlow.IsFinished,
                stage + " no input for 6s stays on same panel");
            Screenshot(stage + "-no-input");
        }
        while (text != null && ScrollableText.textExists) {
            int panel = text.posIndex;
            if (text.progress != text.positions[panel]) {
                // Exercise attack binding as well as the native click callback.
                AttackEdge();
                yield return Wait(() => Time.frameCount > pressStart + 7, stage + " attack sample sequence completed", 10f);
                // A short page may finish typing during the two neutral frames
                // before down. Assert against its state AT the sampled edge.
                Check(sampledPanel == panel, stage + " input sampled on intended panel");
                if (sampledRevealed) {
                    Check(text.posIndex == Mathf.Min(panel + 1, text.positions.Count - 1) &&
                        (panel < text.positions.Count - 1 || text.toClose), stage + " page completed before down; one edge advances once");
                    if (text.toClose) { yield return Wait(() => !ScrollableText.textExists, stage + " final confirmation closes"); break; }
                    continue;
                }
                Check(text.progress == text.positions[panel] && text.posIndex == panel, stage + " attack reveals current page only");
            }
            // A fully revealed final panel is still unread until the next click.
            yield return new WaitForSecondsRealtime(text._onLastPanel ? 6f : 1f);
            Check(Text == text && text.posIndex == panel && !text.toClose, stage + " revealed page waits for click page=" + (panel + 1));
            Screenshot(stage + "-page" + (panel + 1));
            AttackEdge(mouse: true);
            yield return Wait(() => sampledPanel >= 0 && Time.frameCount > pressStart + 2, stage + " mouse down sampled", 10f);
            Click(text);
            Click(text); // Pointer callback while held must not consume another step.
            if (panel < text.positions.Count - 1)
                Check(text.posIndex == panel + 1 && !text.toClose, stage + " one click advances exactly one page");
            int afterDown = text.posIndex;
            yield return Wait(() => Time.frameCount > pressStart + 7, stage + " mouse held/up sequence completed", 10f);
            if (text != null) Check(text.posIndex == afterDown, stage + " held and pointer-up do not advance again");
            if (text != null && text.toClose) {
                yield return Wait(() => !ScrollableText.textExists, stage + " clicked final page fades out");
                break;
            }
        }
    }
    internal static IEnumerator ReadNarrative(byte ending) {
        string note1 = ending == 3 ? "mercnote1" : "escapenote1";
        string image = ending == 3 ? "image9" : "escape-image8";
        string note2 = ending == 3 ? "mercnote2" : "escapenote2";
        yield return ReadText(note1, true);
        yield return Wait(() => EndingFlow.NarrativeStep == image, "CG visible");
        var world = WorldGeneration.world;
        Check(world.cutsceneUnder.gameObject.activeSelf && (ending == 3 || world.cutsceneOver.gameObject.activeSelf), "native CG images visible");
        yield return new WaitForSecondsRealtime(6f);
        Check(EndingFlow.NarrativeStep == image && !ScrollableText.textExists && !EndingFlow.IsFinished, "CG no input for 6s stays visible past 3s minimum");
        Screenshot("CG-no-input");
        AttackEdge();
        yield return Wait(() => EndingFlow.NarrativeStep == note2, "CG attack advances to next text");
        Check(ScrollableText.textExists, "CG click opens next text instead of settlement");
        if (Net.IsClient) {
            yield return Wait(() => Seen("r19.host.read"), "host finishes independently while client text stays open");
            yield return new WaitForSecondsRealtime(1f);
            Check(EndingFlow.NarrativeStep == note2 && ScrollableText.textExists && !EndingFlow.IsFinished, "client keeps own unread text after host finishes");
            yield return Wait(() => Seen("r19.party.checked"), "host verifies party wait");
        }
        yield return ReadText(note2);
        if (ending == 3) yield return ReadText("mercnote3");
        yield return Wait(() => EndingFlow.NarrativeStep == "await-party", "local narrative complete");
        if (Net.IsServer) {
            Mark("r19.host.read");
            yield return new WaitForSecondsRealtime(6f);
            Check(EndingFlow.State.phase == EndingPhase.Narrative && EndingFlow.State.narrativeReady.Count == 1 && !PlayerCamera.main.endScreen.gameObject.activeSelf,
                "host completed; waits 6s for client NarrativeComplete; no early settlement");
            Screenshot("await-client"); Mark("r19.party.checked");
        }
    }
}

// Diagnose long native engine frames separately from a narrative wait. Test-only.
internal static class Release19FrameProbe {
    static bool installed;
    internal static void Install() {
        if (installed) return;
        installed = true;
        var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
        Wrap(ref loop, "", 0);
        UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
    }
    static void Wrap(ref UnityEngine.LowLevel.PlayerLoopSystem loop, string parent, int depth) {
        if (loop.subSystemList == null || depth > 1) return;
        var nodes = new System.Collections.Generic.List<UnityEngine.LowLevel.PlayerLoopSystem>();
        foreach (var original in loop.subSystemList) {
            var child = original;
            string name = parent + "/" + child.type?.Name;
            Wrap(ref child, name, depth + 1);
            long start = 0;
            nodes.Add(new UnityEngine.LowLevel.PlayerLoopSystem { type = typeof(Release19FrameProbe), updateDelegate = () => start = System.Diagnostics.Stopwatch.GetTimestamp() });
            nodes.Add(child);
            nodes.Add(new UnityEngine.LowLevel.PlayerLoopSystem { type = typeof(Release19FrameProbe), updateDelegate = () => {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                if (ms > 500d && Release19EndingTests.Active) log.l($"[r19-frame] {name} ms={ms:0.0} stage={EndingFlow.NarrativeStep}");
            }});
        }
        loop.subSystemList = nodes.ToArray();
    }
}

// Inject complete input samples, never override an accepted-input predicate.
// The production edge/release/grant logic remains in the path under test.
[HarmonyPatch(typeof(EndingFlow), "ReadNarrativeButtons")]
internal static class Ending_TestNarrativeButtons {
    static void Postfix(ref NarrativeButtons __result) => __result = Release19EndingTests.SampleButtons(__result);
}
#endif
