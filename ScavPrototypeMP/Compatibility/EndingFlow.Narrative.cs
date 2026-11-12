#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together;

[System.Flags]
internal enum NarrativeButtons {
    None = 0, Focused = 1, MouseHeld = 2, MouseDown = 4, MouseUp = 8,
    AttackHeld = 16, AttackDown = 32, AttackUp = 64, Fixture = 128
}

internal static partial class EndingFlow {
    static WorldGeneration narrativeWorld;
    static Coroutine narrative;
    internal static string NarrativeStep { get; private set; } = "";
    static ScrollableText narrativeText;
    static int narrativeInputFrame = -1;
    static bool narrativeInputArmed;
    static ScrollableText grantedNarrativeText;
    static int narrativeGrantFrame = -1;
    static bool narrativeGrantConsumed;
    static string narrativeInputSource = "unmarked-Skip";
    static NarrativeButtons lastNarrativeButtons;
    internal static string NarrativeInputState() {
        var bind = KeyBinds.GetBind("attack");
        return $"frame={Time.frameCount} focus={Application.isFocused} bind={bind} attack={Input.GetKey(bind)}/{Input.GetKeyDown(bind)}/{Input.GetKeyUp(bind)} mouse0={Input.GetMouseButton(0)}/{Input.GetMouseButtonDown(0)}/{Input.GetMouseButtonUp(0)} module={EventSystem.current?.currentInputModule?.GetType().Name}";
    }
    internal static bool IsNarrativeButton(Button button) => button != null &&
        Enumerable.Range(0, button.onClick.GetPersistentEventCount()).Any(i =>
            button.onClick.GetPersistentTarget(i) is ScrollableText text && OwnsNarrativeText(text));
    internal static void TraceNarrativeButton(Button button, string kind, BaseEventData data) {
        if (!IsNarrativeButton(button)) return;
        var pointer = data as PointerEventData;
        log.l($"[narrative-input] UI {kind} {NarrativeInputState()} pointer={pointer?.pointerId} button={pointer?.button} eligible={pointer?.eligibleForClick} press={pointer?.pressPosition} clickTime={pointer?.clickTime} stack={new System.Diagnostics.StackTrace(2, false)}");
    }

    internal static bool OwnsNarrativeText(ScrollableText text) => Active &&
        State.phase == EndingPhase.Narrative && text != null && text == narrativeText;

    // A Button callback is not evidence of a fresh physical press: Submit,
    // delayed pointer-up and arbitrary UnityEvent.Invoke all reach Skip too.
    // Only this local input pump can grant one scoped Skip per fresh down edge.
    internal static bool AcceptNarrativeClick(ScrollableText text) {
        if (!OwnsNarrativeText(text)) return true;
        bool granted = grantedNarrativeText == text && narrativeGrantFrame == Time.frameCount && !narrativeGrantConsumed && !text.toClose;
        log.l($"[narrative-input] Skip source={narrativeInputSource} accepted={granted} {NarrativeInputState()} stage={NarrativeStep} stack={new System.Diagnostics.StackTrace(2, false)}");
        if (!granted) return false;
        narrativeGrantConsumed = true;
        log.l($"[narrative] click source={narrativeInputSource} frame={Time.frameCount} player={Key(ScavPlayer.LocalPlayer)} stage={NarrativeStep} panel={text.posIndex + 1}/{text.positions.Count} revealed={text.progress == text.positions[text.posIndex]}");
        return true;
    }
    internal static void NarrativeTextInput(ScrollableText text) {
        if (!OwnsNarrativeText(text) || text.toClose || !TryNarrativePress(out string source)) return;
        grantedNarrativeText = text; narrativeGrantFrame = Time.frameCount; narrativeGrantConsumed = false;
        narrativeInputSource = source;
        try { text.Skip(); }
        finally { grantedNarrativeText = null; narrativeInputSource = "unmarked-Skip"; }
    }
    static NarrativeButtons ReadNarrativeButtons() {
        var bind = KeyBinds.GetBind("attack");
        return (Application.isFocused ? NarrativeButtons.Focused : 0) |
            (Input.GetMouseButton(0) ? NarrativeButtons.MouseHeld : 0) |
            (Input.GetMouseButtonDown(0) ? NarrativeButtons.MouseDown : 0) |
            (Input.GetMouseButtonUp(0) ? NarrativeButtons.MouseUp : 0) |
            (Input.GetKey(bind) ? NarrativeButtons.AttackHeld : 0) |
            (Input.GetKeyDown(bind) ? NarrativeButtons.AttackDown : 0) |
            (Input.GetKeyUp(bind) ? NarrativeButtons.AttackUp : 0);
    }
    static bool TryNarrativePress(out string source) {
        source = "";
        var buttons = ReadNarrativeButtons();
        if (buttons != lastNarrativeButtons) {
            lastNarrativeButtons = buttons;
            log.l($"[narrative-input] sample={buttons} armed={narrativeInputArmed} {NarrativeInputState()} stage={NarrativeStep}");
        }
        if ((buttons & NarrativeButtons.Focused) == 0) { narrativeInputArmed = false; return false; }
        bool held = (buttons & (NarrativeButtons.MouseHeld | NarrativeButtons.AttackHeld)) != 0;
        bool down = (buttons & (NarrativeButtons.MouseDown | NarrativeButtons.AttackDown)) != 0;
        if (!held && !down) { narrativeInputArmed = true; return false; }
        if (!down || !narrativeInputArmed || narrativeInputFrame == Time.frameCount) return false;
        narrativeInputArmed = false; narrativeInputFrame = Time.frameCount;
        source = ((buttons & NarrativeButtons.Fixture) != 0 ? "fixture-" : "physical-") +
            ((buttons & NarrativeButtons.MouseDown) != 0 ? "mouse-down" : "attack-down");
        log.l($"[narrative-input] edge source={source} frame={Time.frameCount} sample={buttons} stage={NarrativeStep}");
        return true;
    }
    static void ShowNarrativeText(string key) {
        ScrollableText.CreateText(Locale.GetOther(key), cover: true);
        narrativeText = ScrollableText.textObj.GetComponent<ScrollableText>();
        narrativeInputFrame = Time.frameCount; // don't reuse the preceding CG click
        narrativeInputArmed = false; grantedNarrativeText = null;
        Stage(key);
    }

    // Presentation finishes locally, settlement commits once every connected
    // participant (including dead players) has read the story. No host timer
    // may force-close a client's text. Disconnected players do not hold the gate.
    static void BeginNarrative(byte type, string reason, string key = "") {
        if (!Net.IsServer || SettlementLocked || State.phase == EndingPhase.Failed) return;
        State.phase = EndingPhase.Narrative; State.ending = type;
        State.narrativeKey = key; State.narrativeReady.Clear();
        Changed($"[{(type == 3 ? "merc" : "escape")}] narrative begin type={type} reason={reason}");
    }
    static void NarrativeComplete(ScavPlayer player, int type) {
        if (State.phase != EndingPhase.Narrative || type != State.ending || State.narrativeReady.Contains(Key(player))) return;
        State.narrativeReady.Add(Key(player));
        Changed($"[{(type == 3 ? "merc" : "escape")}] narrative ready player={Key(player)} count={State.narrativeReady.Count}");
    }
    static void TickNarrative() {
        if (State.phase == EndingPhase.Narrative && Players().All(p => State.narrativeReady.Contains(Key(p))))
            Finish(State.ending, "all connected players completed narrative");
    }
    static void Stage(string step) {
        NarrativeStep = step;
        log.l($"[{(State.ending == 3 ? "merc" : "escape")}] local stage={step} dark={GlobalDark.main.image.color.a:0.000}");
    }
    static void ApplyNarrative() {
        var world = WorldGeneration.world;
        if (State.phase != EndingPhase.Narrative) {
            if (!IsFinished) {
                if (narrativeWorld != null && narrative != null) narrativeWorld.StopCoroutine(narrative);
                narrativeWorld = null; narrative = null; narrativeText = null; NarrativeStep = "";
            }
            return;
        }
        if (PlayerCamera.main?.body == null || world == null) return;
        world.cutsceneActive = true;
        foreach (var p in Players()) if (p.body != null) {
            p.body.forceNoMovement = true; p.body.forceWalk = false;
            p.body.moveDir = Vector2.zero; p.body.SetVelocity(Vector2.zero);
        }
        PlayerCamera.main.blockItemUsage = true;
        if (narrativeWorld == world && narrative != null) return;
        narrativeWorld = world;
        UIInGame.StopSpectatorMode();
        PlayerCamera.main.body = ScavPlayer.LocalPlayer.body;
        PlayerCamera.main.didDeathScreen = true;
        if (PlayerCamera.main.finishCoroutine != null) {
            PlayerCamera.main.StopCoroutine(PlayerCamera.main.finishCoroutine);
            PlayerCamera.main.finishCoroutine = null;
        }
        PlayerCamera.main.endScreen.gameObject.SetActive(false);
        PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(PlayerCamera.main);
        if (EndLabScript.instance != null) EndLabScript.instance.StopAllCoroutines();
        if (MinigameBase.main?.currentMinigame != null) MinigameBase.main.EndMinigame();
        PlayerCamera.main.craftingPanel.SetActive(false); PlayerCamera.main.tradeMenu.SetActive(false); PlayerCamera.main.itemCustomizer.SetActive(false);
        narrative = world.StartCoroutine(Narrative(world, State.ending));
    }
    static bool AttackPressed() {
        return TryNarrativePress(out _);
    }
    static IEnumerator WaitForNarrativeAttack() {
        // A held click from the previous text/CG cannot advance this CG.
        narrativeInputArmed = false;
        yield return null;
        yield return new WaitUntil(AttackPressed);
        log.l($"[narrative] CG click player={Key(ScavPlayer.LocalPlayer)} stage={NarrativeStep}");
    }
    static IEnumerator Fade(string step) {
        GlobalDark.main.SetSpeedMultiplier(1f);
        GlobalDark.main.SetStayDark(false);
        GlobalDark.main.Darken(); Stage(step + "-start");
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        Stage(step + "-complete");
    }
    static IEnumerator Narrative(WorldGeneration world, byte type) {
        Time.timeScale = 1f;
        ScrollableText.ForceClose();
        GlobalDark.main.SetDarkAmount(0f);
        yield return Fade("initial-fade");
        MusicManager.main.StopSong();
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        if (type == 3) {
            ShowNarrativeText("mercnote1");
            yield return new WaitUntil(() => !ScrollableText.textExists);
            GlobalDark.main.SetSpeedMultiplier(.1f);
            world.cutsceneUnder.gameObject.SetActive(true); world.cutsceneUnder.sprite = world.cutsceneImages[9]; Stage("image9");
            yield return new WaitForSecondsRealtime(3f);
            yield return WaitForNarrativeAttack();
            yield return Fade("note2-fade");
            ShowNarrativeText("mercnote2");
            MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/endlabdrone")); MusicManager.main.specialLoop = true;
            world.cutsceneUnder.gameObject.SetActive(false); world.cutsceneOver.gameObject.SetActive(false);
            world.cutsceneUnder.sprite = world.cutsceneImages[11];
            yield return new WaitUntil(() => !ScrollableText.textExists);
            MusicManager.main.StopSong();
            Sound.Play("heartsplosion", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            GlobalDark.main.SetDarkAmount(1f); GlobalDark.main.SetStayDark(true); GlobalDark.main.Darken(); Stage("heartsplosion-black");
            yield return null;
            ScrollableText.ForceClose();
            yield return new WaitForSecondsRealtime(4f);
            GlobalDark.main.SetStayDark(false);
            ShowNarrativeText("mercnote3");
            yield return new WaitUntil(() => !ScrollableText.textExists);
            MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/biopod")); MusicManager.main.specialLoop = false;
            GlobalDark.main.SetSpeedMultiplier(.05f);
        } else if (type == 2) {
            Sound.Play("robothit", PlayerCamera.main.body.transform.position, twoDimensional: false, pitchShift: false);
            MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/freedom")); MusicManager.main.specialLoop = false;
            ShowNarrativeText("escapenote1");
            yield return new WaitUntil(() => !ScrollableText.textExists);
            GlobalDark.main.SetSpeedMultiplier(.1f);
            world.cutsceneUnder.gameObject.SetActive(true); world.cutsceneUnder.sprite = world.cutsceneImages[8];
            world.cutsceneOver.gameObject.SetActive(true); world.cutsceneOver.sprite = world.cutsceneImages[PlayerCamera.main.body.charType + 3]; Stage("escape-image8");
            yield return new WaitForSecondsRealtime(3f);
            yield return WaitForNarrativeAttack();
            yield return Fade("escape-note2-fade");
            ShowNarrativeText("escapenote2");
            world.cutsceneUnder.sprite = world.cutsceneImages[12]; world.cutsceneUnder.gameObject.SetActive(false); world.cutsceneOver.gameObject.SetActive(false);
            yield return new WaitUntil(() => !ScrollableText.textExists);
        } else {
            ShowNarrativeText(State.narrativeKey);
            yield return new WaitUntil(() => !ScrollableText.textExists);
        }
        GlobalDark.main.SetDarkAmount(1f); GlobalDark.main.SetStayDark(true); GlobalDark.main.Darken();
        Stage("await-party");
        Request(EndingRequest.NarrativeComplete, Vector2.zero, type);
    }
}

// Native ScrollableText advances one panel per real click; our previous input
// pump could grant a second Skip in the same frame and looked like auto-play.
[HarmonyPatch(typeof(ScrollableText), nameof(ScrollableText.Skip))]
internal static class Ending_NarrativeClickPatch {
    static bool Prefix() => true;
}
[HarmonyPatch(typeof(Button), nameof(Button.OnPointerClick))]
internal static class Ending_NarrativePointerTrace {
    static void Prefix(Button __instance, PointerEventData eventData) => EndingFlow.TraceNarrativeButton(__instance, "pointer-click", eventData);
}
[HarmonyPatch(typeof(Button), nameof(Button.OnSubmit))]
internal static class Ending_NarrativeSubmitTrace {
    static void Prefix(Button __instance, BaseEventData eventData) => EndingFlow.TraceNarrativeButton(__instance, "submit", eventData);
}
#endif
