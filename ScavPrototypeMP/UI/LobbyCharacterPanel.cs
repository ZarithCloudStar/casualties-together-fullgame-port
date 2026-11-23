#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together;

// A client-only, persistent UGUI overlay. It never gates world loading or body
// creation; closing it accepts the remembered choice without room confirmation.
internal sealed class LobbyCharacterPanel : MonoBehaviour {
    static LobbyCharacterPanel instance;
    const float CloseDelay = 0.2f;
    const float ClickGuardDuration = 0.3f;
    static GameObject clickGuard;
    static float guardUntil;
    static int guardFrame;
    GameObject overlay;
    CanvasGroup overlayGroup;
    readonly Button[] choices = new Button[3];
    readonly TextMeshProUGUI[] choiceLabels = new TextMeshProUGUI[3];
    readonly Image[] choiceImages = new Image[3];
    TextMeshProUGUI title, subtitle, hint, confirmLabel, closeLabel, chipHint;
    readonly Button[] chipButtons = new Button[2];
    readonly TextMeshProUGUI[] chipLabels = new TextMeshProUGUI[2];
    bool displayedUnchipped;
    bool displayedChipAllowed;
    int displayedChoice = -1;
    string displayedLanguage;
    bool displayedBody, shown, cursorVisible;
    CursorLockMode cursorLock;
    float waitSince;
    bool waitLogged;
    bool _hiding, actionPending;
    bool closing, hideScheduled;
    float closeStarted;
    int closeFrame;

    // AdaptiveButton polls raw Input instead of the EventSystem. A Graphic
    // raycaster alone cannot prevent its Quit action while our modal is open.
    internal static bool BlocksVanillaMenuInput => Util.GameIsFull() &&
        (clickGuard != null || (instance != null && (instance.shown || instance.closing)));

    internal static void Ensure() {
        if (!CharacterLobby.ShouldShowPanel) return;
        if (instance == null) {
            var controller = new GameObject("CT_LobbyCharacterController");
            DontDestroyOnLoad(controller);
            instance = controller.AddComponent<LobbyCharacterPanel>();
            instance.waitSince = Time.unscaledTime;
        }
        // Show from Update, never while the menu's button event is dispatching.
    }

    internal static void HideForReset() {
        if (instance != null) CharacterSelection.RunUiAction("panel reset hide", instance.Hide);
    }

    internal static void DrawModMenuButton(float height) {
        if (!Util.GameIsFull()) return;
        bool previousEnabled = GUI.enabled;
        bool canChoose = CharacterLobby.InLobby && Net.IsConnected && (Net.IsClient || Net.IsServer);
        GUI.enabled = previousEnabled && canChoose;
        try {
            string text = CharacterSelection.Text("character_open_button", "Choose character");
            string tooltip = canChoose ? "" : CharacterSelection.Text("character_need_connection", "Connect to a server first.");
            if (GUILayout.Button(new GUIContent(text, tooltip), GUILayout.Height(height))) {
                CharacterSelection.DeferUiAction("mod menu reopen", () => {
                    if (CharacterLobby.ReopenPanel()) {
                        // The IMGUI menu is also hidden outside its click event,
                        // with a shield covering the transition to UGUI.
                        ExtendClickGuard();
                        UIMainMenu.SetOpen(false);
                    }
                });
            }
        } finally { GUI.enabled = previousEnabled; }
    }

    static bool LocalHasBody() {
        var player = ScavPlayer.LocalPlayer;
        return player != null && player.body != null;
    }

    internal static string LocalName() {
        var player = ScavPlayer.LocalPlayer;
        return player != null ? player.playerName : UIMainMenu.USERINPUT_NAME;
    }

    void Update() => CharacterSelection.RunUiAction("panel update", UpdatePanel);

    void UpdatePanel() {
        if (_hiding || actionPending || closing) return;
        if (!CharacterLobby.ShouldShowPanel) {
            Hide();
            return;
        }
        if (shown && overlay != null && !overlay.activeSelf) {
            QueueAction("external close", () => { CharacterLobby.DismissPanel(); Hide(); }, true);
            return;
        }
        ShowIfReady();
        if (!shown) return;
        if (Input.GetKeyDown(KeyCode.Escape)) QueueAction("escape", () => { CharacterLobby.DismissPanel(); Hide(); }, true);
        else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) QueueAction("confirm key", Confirm, true);
        if (displayedChoice != CharacterLobby.SafeDraftChoice() || displayedLanguage != Locale.currentLangName || displayedBody != LocalHasBody() || displayedUnchipped != CharacterLobby.DraftUnchipped || displayedChipAllowed != ChipState.IndividualChipChoiceAllowed) Refresh();
        if (!CharacterLobby.ShouldShowPanel) Hide();
    }

    void LateUpdate() {
        if (shown && CharacterLobby.ShouldShowPanel) {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void ShowIfReady() {
        if (!CharacterLobby.ShouldShowPanel || shown || closing || hideScheduled) return;
        if (UIManager.template_button == null || UIManager.template_label == null) {
            if (!waitLogged && Time.unscaledTime - waitSince > 3f) {
                waitLogged = true;
                log.warn("CHARACTER: lobby panel waiting for the existing UI button/label templates.");
            }
            return;
        }
        if (overlay == null) BuildView();
        cursorVisible = Cursor.visible;
        cursorLock = Cursor.lockState;
        overlay.SetActive(true);
        overlayGroup.blocksRaycasts = true;
        overlayGroup.interactable = true;
        shown = true;
        Refresh();
        int chr = CharacterLobby.SafeDraftChoice();
        var events = EventSystem.current;
        if (events != null && choices != null && chr < choices.Length && choices[chr] != null)
            events.SetSelectedGameObject(choices[chr].gameObject);
        log.l($"CHARACTER: lobby panel opened player={LocalName()}");
    }

    void BeginClose() {
        if (closing) return;
        // This runs immediately in the click callback. The independent Canvas
        // remains a raycast target even though the panel no longer is one.
        ExtendClickGuard();
        closing = true;
        closeStarted = Time.unscaledTime;
        closeFrame = Time.frameCount;
        if (overlayGroup != null) {
            overlayGroup.blocksRaycasts = false;
            overlayGroup.interactable = false;
        }
        ClearFocus();
        log.l("CHARACTER: panel close deferred hideAfter=0.2s clickGuard=0.3s");
    }

    void Hide() {
        if (!shown || _hiding || hideScheduled) return;
        BeginClose();
        hideScheduled = true;
        if (!CharacterSelection.RunUiAction("panel close schedule", () => Util.StartCoroutine(DeactivateAfterDelay()))) {
            hideScheduled = false;
            CancelClose();
        }
    }

    IEnumerator DeactivateAfterDelay() {
        // A global coroutine runner survives controller disable/destruction.
        // Use unscaled time so a paused lobby still completes the transition.
        do { yield return null; }
        while (Time.frameCount <= closeFrame || Time.unscaledTime - closeStarted < CloseDelay);
        CharacterSelection.RunUiAction("panel delayed deactivate", Deactivate);
    }

    static void ClearFocus() => CharacterSelection.RunUiAction("panel clear focus", () => {
        var events = EventSystem.current;
        if (events != null) events.SetSelectedGameObject(null);
    });

    void Deactivate() {
        if (_hiding) return;
        _hiding = true;
        shown = false; // OnDisable must observe the closed state, even on reentry.
        try {
            if (overlay != null) overlay.SetActive(false);
            ClearFocus();
            log.l("CHARACTER: lobby panel hidden");
        } finally {
            Cursor.visible = cursorVisible;
            Cursor.lockState = cursorLock;
            _hiding = false;
            hideScheduled = closing = false;
        }
    }

    void CancelClose() {
        closing = false;
        if (overlayGroup != null) {
            overlayGroup.blocksRaycasts = true;
            overlayGroup.interactable = true;
        }
    }

    static void ExtendClickGuard() {
        guardUntil = Math.Max(guardUntil, Time.unscaledTime + ClickGuardDuration);
        guardFrame = Time.frameCount;
        if (clickGuard != null) return;
        clickGuard = new GameObject("CT_LobbyCharacterClickGuard", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        DontDestroyOnLoad(clickGuard);
        var canvas = clickGuard.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32760;
        var eater = new GameObject("ClickEater", typeof(RectTransform), typeof(Image));
        eater.transform.SetParent(clickGuard.transform, false);
        var rect = eater.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var image = eater.GetComponent<Image>();
        image.color = new Color(0, 0, 0, 0);
        image.raycastTarget = true;
        try { Util.StartCoroutine(ReleaseClickGuard()); }
        catch (Exception) {
            // Scheduling may fail during teardown. Leave the still-visible
            // modal in place and let the callback's common logger report it.
            Destroy(clickGuard);
            clickGuard = null;
            throw;
        }
    }

    static IEnumerator ReleaseClickGuard() {
        // Keep consuming a held mouse/submit key until it is released as well.
        do { yield return null; }
        while (Time.frameCount <= guardFrame || Time.unscaledTime < guardUntil ||
               Input.GetMouseButton(0) || Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter));
        CharacterSelection.RunUiAction("panel release click guard", () => {
            ClearFocus();
            if (clickGuard != null) Destroy(clickGuard);
            clickGuard = null;
            log.l("CHARACTER: panel click guard released");
        });
    }

    void OnDisable() {
        if (!shown || _hiding) return;
        if (closing) return;
        QueueAction("panel disabled", () => { CharacterLobby.DismissPanel(); Hide(); }, true);
    }

    void OnDestroy() {
        if (shown && !_hiding && !closing && CharacterLobby.ShouldShowPanel)
            QueueAction("panel destroyed", () => { CharacterLobby.DismissPanel(); Hide(); }, true);
        CharacterSelection.RunUiAction("panel destroy hide", Hide);
        if (instance == this) instance = null;
    }

    void Confirm() {
        if (CharacterLobby.ConfirmPanel()) Hide();
    }

    void QueueAction(string phase, Action action, bool closes = false) {
        CharacterSelection.RunUiAction(phase + " callback", () => {
            if (_hiding || actionPending || closing || !shown || !CharacterLobby.ShouldShowPanel) return;
            if (closes) BeginClose();
            actionPending = true;
            int session = CharacterLobby.SessionVersion;
            log.l($"CHARACTER: panel action queued {phase}");
            bool queued = CharacterSelection.DeferUiAction("panel " + phase, () => {
                try {
                    if (session == CharacterLobby.SessionVersion && CharacterLobby.ShouldShowPanel) action();
                } finally {
                    actionPending = false;
                    if (closes && !hideScheduled) {
                        if (!CharacterLobby.ShouldShowPanel) Hide();
                        else CancelClose(); // e.g. failed send: allow retry
                    }
                }
            });
            if (!queued) { actionPending = false; if (closes) CancelClose(); }
        });
    }

    void BuildView() {
        overlay = new GameObject("LobbyCharacterOverlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
        overlayGroup = overlay.GetComponent<CanvasGroup>();
        overlay.transform.SetParent(transform, false);
        var canvas = overlay.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20000;
        var scaler = overlay.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        backdrop.transform.SetParent(overlay.transform, false);
        var backdropRect = backdrop.GetComponent<RectTransform>();
        backdropRect.anchorMin = Vector2.zero;
        backdropRect.anchorMax = Vector2.one;
        backdropRect.offsetMin = backdropRect.offsetMax = Vector2.zero;
        backdrop.GetComponent<Image>().color = new Color(0, 0, 0, 0.75f);

        UIManager.CreateBlock(overlay.transform, "LobbyCharacterCard", out var card, out var cardRect, out var cardImage);
        card.name = "LobbyCharacterCard";
        Place(cardRect, Vector2.zero, new Vector2(1000, 860));
        cardImage.color = new Color(0.09f, 0.11f, 0.14f, 1f);
        title = Label(card.transform, "Title", "character_title", "Choose your character", new Vector2(0, 374), new Vector2(820, 54), 30);
        subtitle = Label(card.transform, "Instructions", "character_panel_instructions", "Choose a character, then confirm. Close keeps your current choice.", new Vector2(0, 315), new Vector2(900, 66), 20);

        for (int i = 0; i < 3; i++) {
            int chr = i;
            UIManager.CreateButton(card.transform, "character_title", null, null, out var row, out var text, out var rect, out var button, out var image);
            row.name = "Character_" + i;
            Place(rect, new Vector2(0, 198 - i * 118), new Vector2(900, 106));
            text.fontSize = 21;
            text.enableAutoSizing = false;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.alignment = TextAlignmentOptions.Left;
            choices[i] = button;
            choiceLabels[i] = text;
            choiceImages[i] = image;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => QueueAction("preview " + chr, () => { CharacterLobby.Preview(chr); Refresh(); }));
        }

        for (int i = 0; i < 2; i++) {
            bool unchipped = i == 1;
            UIManager.CreateButton(card.transform, unchipped ? "chip_unchipped" : "chip_chipped", null, null,
                out var row, out var text, out var rect, out var button);
            row.name = unchipped ? "Chip_Unchipped" : "Chip_Chipped";
            Place(rect, new Vector2(i == 0 ? -230 : 230, -146), new Vector2(440, 58));
            chipButtons[i] = button;
            chipLabels[i] = text;
            text.fontSize = 23;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => QueueAction("chip preview", () => { CharacterLobby.PreviewChip(unchipped); Refresh(); }));
        }
        chipHint = Label(card.transform, "ChipHint", "chip_hint", "Saved per player for this save; used by multiplayer ending rules.",
            new Vector2(0, -210), new Vector2(900, 56), 18);
        bool showChips = ChipState.IndividualChipChoiceAllowed;
        for (int i = 0; i < 2; i++) chipButtons[i].gameObject.SetActive(showChips);
        chipHint.gameObject.SetActive(showChips);
        hint = Label(card.transform, "Status", "character_saved", "Character choice saved.", new Vector2(0, -274), new Vector2(900, 58), 18);
        Button(card.transform, "Confirm", "character_confirm", "Confirm", new Vector2(0, -362), new Vector2(420, 64),
            Confirm, out confirmLabel);
        Button(card.transform, "Close", "character_close", "Close", new Vector2(450, 374), new Vector2(80, 48),
            () => { CharacterLobby.DismissPanel(); Hide(); }, out closeLabel);
        overlay.SetActive(false);
        log.l("CHARACTER: lobby panel UI created Canvas=ScreenSpaceOverlay order=20000 card=1000x700 buttons=3 confirm=1 close=1");
    }

    static void Place(RectTransform rect, Vector2 position, Vector2 size) {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        rect.localScale = Vector3.one;
    }

    static TextMeshProUGUI Label(Component parent, string name, string key, string fallback, Vector2 position, Vector2 size, float fontSize) {
        UIManager.CreateLabel(parent, key, null, null, out var go, out var text, out var rect);
        go.name = name;
        Place(rect, position, size);
        text.text = CharacterSelection.Text(key, fallback);
        text.fontSize = fontSize;
        text.enableAutoSizing = false;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
        return text;
    }

    void Button(Component parent, string name, string key, string fallback, Vector2 position, Vector2 size, Action action, out TextMeshProUGUI label) {
        UIManager.CreateButton(parent, key, null, null, out var go, out label, out var rect, out var button);
        go.name = name;
        Place(rect, position, size);
        label.text = CharacterSelection.Text(key, fallback);
        label.fontSize = 22;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => QueueAction(name, action, true));
    }

    void Refresh() {
        displayedChoice = CharacterLobby.SafeDraftChoice();
        displayedLanguage = Locale.currentLangName;
        displayedBody = LocalHasBody();
        displayedUnchipped = CharacterLobby.DraftUnchipped;
        chipHint.text = CharacterSelection.Text("chip_hint", "Saved per player for this save; used by multiplayer ending rules.");
        bool showChips = ChipState.IndividualChipChoiceAllowed;
        displayedChipAllowed = showChips;
        chipHint.gameObject.SetActive(showChips);
        for (int i = 0; i < 2; i++) {
            bool selected = displayedUnchipped == (i == 1);
            if (chipButtons[i] != null) chipButtons[i].gameObject.SetActive(showChips);
            chipLabels[i].text = (selected ? "[" + CharacterSelection.Text("character_selected", "Selected") + "] " : "") + ChipState.Label(i == 1);
            chipButtons[i].GetComponent<Image>().color = selected ? new Color(.35f, .85f, .55f, 1) : Color.white;
        }
        title.text = CharacterSelection.Text("character_title", "Choose your character");
        subtitle.text = CharacterSelection.Text("character_panel_instructions", "Choose a character, then confirm. Close keeps your current choice.");
        hint.text = CharacterSelection.CommandLineOverride.HasValue
            ? CharacterSelection.Text("character_cli_override", "Character selection is fixed by --mp-setcharacter.")
            : displayedBody ? CharacterSelection.DeferredMessage
            : CharacterSelection.Text("character_panel_ready", "Your choice will be used when your character is created.");
        confirmLabel.text = CharacterSelection.Text("character_confirm", "Confirm");
        closeLabel.text = CharacterSelection.Text("character_close", "Close");
        for (int i = 0; i < 3; i++) {
            if (choices[i] == null || choiceLabels[i] == null || choiceImages[i] == null) continue;
            string name = LobbyCharacterSelection.Name(i);
            string desc = LobbyCharacterSelection.Description(i);
            UIManager.SetUITooltip(choiceLabels[i], name, name, desc, false);
            choiceLabels[i].text = (i == displayedChoice ? "[" + CharacterSelection.Text("character_selected", "Selected") + "] " : "") + name + "\n" + desc;
            choices[i].interactable = !CharacterSelection.CommandLineOverride.HasValue || i == CharacterSelection.Local;
            choiceImages[i].color = i == displayedChoice ? new Color(0.35f, 0.85f, 0.55f, 1) : Color.white;
        }
    }
}

// The native main-menu buttons bypass EventSystem raycasts entirely. Gate only
// their click actions while our fullgame modal/shield owns the input.
[HarmonyPatch(typeof(AdaptiveButton), "Clicked")]
internal static class AdaptiveButton_Clicked_CharacterPanelPatch {
    static bool Prepare() => Util.GameIsFull();
    internal static bool Prefix() => !LobbyCharacterPanel.BlocksVanillaMenuInput;
}
#endif
