#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

// Client-side adapter that reuses the stock PreRunScript character page.
// It deliberately owns no admission/selection state: CharacterLobby remains the
// source of truth, and the stock Start button is treated as Ready.
internal static class StockCharacterSelectUI {
    static bool shown;
    static bool openedSettings;
    static PreRunScript prs;
    static Transform charSelect;
    static readonly List<GameObject> hidden = new();
    static GameObject chipBar;
    static Button chipChipped, chipUnchipped;
    static TextMeshProUGUI chipChippedLabel, chipUnchippedLabel;
    static Button startButton, backButton;
    static Button.ButtonClickedEvent stockBack;
    static TextMeshProUGUI[] startLabels;
    static string[] startOriginalTexts;
    static TextMeshProUGUI waitingLabel;
    static PreRunScript observedMenu;
    static Transform observedPage;

    internal static bool IsVisible => Net.IsRunning && observedPage != null && observedPage.gameObject.activeInHierarchy;

    static string ConfirmText => CharacterSelection.Text("character_confirm", "Confirm");

    internal static void Tick() {
        if (!Util.GameIsFull()) return;
        var menu = PreRunScript.instance;
        if (!ReferenceEquals(menu, observedMenu) || (menu != null && observedPage == null)) {
            observedMenu = menu;
            observedPage = FindCharSelect(menu);
        }
        if (!Net.IsRunning) { Hide(); return; }
        TickLobby();
    }

    static void TickLobby() {
        if (!Util.IsInMainMenu() || Util.IsInWorld() || !CharacterLobby.ShouldShowPanel) {
            if (shown) Hide();
            return;
        }
        PreRunScript p = PreRunScript.instance;
        if (p == null) return;
        if (!shown) Show(p);
        if (!shown) return;
        if (Input.GetKeyDown(KeyCode.Escape)) {
            DismissAndReturnToRoom();
            return;
        }
        RefreshPreview(p);
        UpdateChips();
        SetStartText(ConfirmText);
        if (Net.IsServer) UpdateWaitingLabel(p);
    }

    internal static void Show() {
        if (!Util.GameIsFull() || !CharacterLobby.ShouldShowPanel) return;
        PreRunScript p = PreRunScript.instance;
        if (p != null) Show(p);
    }

    static void Show(PreRunScript p) {
        prs = p;
        Transform root = FindCharSelect(p);
        if (root == null) return;
        charSelect = root;
        if (UIMainMenu.IsOpen()) UIMainMenu.SetOpen(false);

        if (p.runSettingsScreen != null && !p.runSettingsScreen.activeSelf) {
            p.runSettingsScreen.SetActive(true);
            openedSettings = true;
        }
        HideRunRuleSiblings(p);
        root.gameObject.SetActive(true);
        Transform bottom = FindDeepChild(root, "Bottom");
        if (bottom != null) bottom.gameObject.SetActive(true);

        HookButtons(root);
        CaptureStartText();
        BuildChips(root);
        shown = true;
        RefreshPreview(p, true);
        SetStartText(ConfirmText);
        UpdateChips();
    }

    internal static void Hide() {
        if (!shown) return;
        RestoreStartText();
        if (chipBar != null) {
            Object.Destroy(chipBar);
            chipBar = null;
            chipChipped = chipUnchipped = null;
            chipChippedLabel = chipUnchippedLabel = null;
        }
        if (backButton != null && stockBack != null) backButton.onClick = stockBack;
        startButton = backButton = null;
        stockBack = null;
        if (charSelect != null) charSelect.gameObject.SetActive(false);
        if (openedSettings && prs != null && prs.runSettingsScreen != null)
            prs.runSettingsScreen.SetActive(false);
        openedSettings = false;
        RestoreHidden();
        charSelect = null;
        prs = null;
        shown = false;
    }

    internal static void HideForReset() => Hide();

    static void HookButtons(Transform root) {
        startButton = FindChildButton(root, "Bottom/Start") ?? FindChildButton(root, "Start");
        backButton = FindChildButton(root, "Bottom/Back") ?? FindChildButton(root, "Back");
        if (backButton == null) return;
        stockBack = backButton.onClick;
        backButton.onClick = new Button.ButtonClickedEvent();
        backButton.onClick.AddListener(DismissAndReturnToRoom);
    }

    // Back/ESC from the character page returns to the room menu, not the
    // vanilla main menu.
    static void DismissAndReturnToRoom() {
        CharacterLobby.DismissPanel();
        Hide();
        LobbyNavigation.ReturnToRoom();
    }

    static void RefreshPreview(PreRunScript p, bool force = false) {
        int draft = CharacterLobby.SafeDraftChoice();
        if (!force && PreRunScript.characterSelected == draft) return;
        PreRunScript.characterSelected = draft;
        p.UpdateCharacterText();
    }

    static void UpdateWaitingLabel(PreRunScript p) {
        int waiting = CharacterReadyGate.WaitingCount + CharacterReadyGate.LobbyMissing;
        if (waitingLabel == null && waiting > 0) {
            var go = new GameObject("CT_CharacterWaiting", typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = (RectTransform)go.transform;
            rect.SetParent(charSelect, false);
            rect.anchorMin = new Vector2(.015f, .065f);
            rect.anchorMax = new Vector2(.405f, .135f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            waitingLabel = go.GetComponent<TextMeshProUGUI>();
            waitingLabel.font = p.charSelectNotes.font;
            waitingLabel.fontSize = p.charSelectNotes.fontSize * .8f;
            waitingLabel.alignment = TextAlignmentOptions.MidlineLeft;
            waitingLabel.raycastTarget = false;
        }
        if (waitingLabel == null) return;
        waitingLabel.gameObject.SetActive(waiting > 0);
        if (waiting > 0) {
            int missing = CharacterReadyGate.LobbyMissing;
            waitingLabel.text = missing > 0 ? CharacterReadyGate.ReadyText(missing) : CharacterReadyGate.WaitingText(waiting);
        }
    }

    static void BuildChips(Transform root) {
        if (chipBar != null || startButton == null || backButton == null) return;
        chipBar = new GameObject("CT_StockCharacterChips", typeof(RectTransform));
        chipBar.transform.SetParent(root, false);
        chipChipped = MakeChip(chipBar.transform, false, out chipChippedLabel);
        chipUnchipped = MakeChip(chipBar.transform, true, out chipUnchippedLabel);
        chipBar.SetActive(ChipState.IndividualChipChoiceAllowed);
        Canvas.ForceUpdateCanvases();
        FitChipRow();
    }

    static void FitChipRow() {
        var root = (RectTransform)charSelect;
        var back = (RectTransform)backButton.transform;
        var start = (RectTransform)startButton.transform;
        var row = (RectTransform)chipBar.transform;
        Vector2 bottomLeft = root.InverseTransformPoint(back.TransformPoint(back.rect.min));
        Vector2 topRight = root.InverseTransformPoint(start.TransformPoint(start.rect.max));
        var footerMin = (bottomLeft - root.rect.min) / root.rect.size;
        var footerMax = (topRight - root.rect.min) / root.rect.size;
        row.anchorMin = new Vector2(footerMin.x, footerMax.y);
        row.anchorMax = new Vector2(footerMax.x, 2f * footerMax.y - footerMin.y);
        row.offsetMin = row.offsetMax = Vector2.zero;
    }

    static Button MakeChip(Transform parent, bool unchipped, out TextMeshProUGUI label) {
        var button = Object.Instantiate(startButton, parent, false);
        button.name = unchipped ? "Chip_Unchipped" : "Chip_Chipped";
        var rect = (RectTransform)button.transform;
        rect.anchorMin = new Vector2(unchipped ? .5f : 0f, 0f);
        rect.anchorMax = new Vector2(unchipped ? 1f : .5f, 1f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        label.text = ChipState.Label(unchipped);
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => {
            CharacterLobby.PreviewChip(unchipped);
            // Once the client has pressed Ready, a further change is sent
            // immediately; before that it stays a local preview.
            if (CharacterLobby.LocalChosen) CharacterLobby.CommitDraft();
            UpdateChips();
        });
        return button;
    }

    static void UpdateChips() {
        if (chipBar != null) chipBar.SetActive(ChipState.IndividualChipChoiceAllowed);
        if (chipChipped == null || chipUnchipped == null) return;
        FitChipRow();
        bool unchipped = CharacterLobby.DraftUnchipped;
        chipChippedLabel.text = ChipState.Label(false);
        chipUnchippedLabel.text = ChipState.Label(true);
        chipChipped.targetGraphic.color = unchipped ? Color.white : new Color(.35f, .85f, .55f, 1f);
        chipUnchipped.targetGraphic.color = unchipped ? new Color(.35f, .85f, .55f, 1f) : Color.white;
    }

    static void SetStartText(string text) {
        if (charSelect == null) return;
        Button button = startButton ?? (startButton = FindChildButton(charSelect, "Bottom/Start") ?? FindChildButton(charSelect, "Start"));
        if (button == null) return;
        button.interactable = true;
        var labels = button.GetComponentsInChildren<TextMeshProUGUI>(true);
        for (int i = 0; i < labels.Length; i++) {
            if (labels[i] == null) continue;
            labels[i].text = text;
            labels[i].raycastTarget = false;
        }
    }

    // The single-player flow uses the same stock page and must keep its
    // "开始冒险"/Start label. Remember the prefab's text before the lobby
    // page changes it and put it back when the lobby page closes.
    static void CaptureStartText() {
        RestoreStartText();
        if (startButton == null) return;
        startLabels = startButton.GetComponentsInChildren<TextMeshProUGUI>(true);
        startOriginalTexts = new string[startLabels.Length];
        for (int i = 0; i < startLabels.Length; i++) {
            startOriginalTexts[i] = startLabels[i] != null ? startLabels[i].text : null;
        }
    }

    static void RestoreStartText() {
        if (startLabels != null && startOriginalTexts != null) {
            int count = Mathf.Min(startLabels.Length, startOriginalTexts.Length);
            for (int i = 0; i < count; i++) {
                if (startLabels[i] != null && startOriginalTexts[i] != null) startLabels[i].text = startOriginalTexts[i];
            }
        }
        startLabels = null;
        startOriginalTexts = null;
    }

    static void HideRunRuleSiblings(PreRunScript p) {
        if (p.runSettingsScreen == null) return;
        Transform root = p.runSettingsScreen.transform;
        for (int i = 0; i < root.childCount; i++) {
            Transform child = root.GetChild(i);
            if (child == null || child == charSelect) continue;
            if (child.name != "Presets" && child.name != "CustomSettings" && child.name != "SelectOutline") continue;
            if (!child.gameObject.activeSelf) continue;
            child.gameObject.SetActive(false);
            hidden.Add(child.gameObject);
        }
    }

    static void RestoreHidden() {
        for (int i = 0; i < hidden.Count; i++)
            if (hidden[i] != null) hidden[i].SetActive(true);
        hidden.Clear();
    }

    static Button FindChildButton(Transform root, string path) {
        Transform t = root;
        string[] parts = path.Split('/');
        for (int i = 0; i < parts.Length; i++) {
            t = FindDeepChild(t, parts[i]);
            if (t == null) return null;
        }
        return t.GetComponent<Button>();
    }

    static Transform FindCharSelect(PreRunScript p) {
        if (p == null) return null;
        if (p.runSettingsScreen != null) {
            Transform found = FindDeepChild(p.runSettingsScreen.transform, "CharSelect");
            if (found != null) return found;
        }
        if (p.charImages != null) {
            for (int i = 0; i < p.charImages.Count; i++) {
                if (p.charImages[i] == null) continue;
                Transform t = p.charImages[i].transform;
                while (t != null) {
                    if (t.name == "CharSelect") return t;
                    t = t.parent;
                }
            }
        }
        return null;
    }

    static Transform FindDeepChild(Transform parent, string name) {
        if (parent == null) return null;
        if (parent.name == name) return parent;
        for (int i = 0; i < parent.childCount; i++) {
            Transform result = FindDeepChild(parent.GetChild(i), name);
            if (result != null) return result;
        }
        return null;
    }
}

// While the lobby's character page is open, the stock Start button confirms
// the pick instead of starting the run. This applies to the host too, so the
// actual start always goes through the room's gated Start adventure button.
[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
internal static class PreRunScript_StartRun_StockCharacterSelectPatch {
    // Run before the lobby gate so confirming the page is never reported as an
    // attempted (blocked) start.
    [HarmonyPriority(900)]
    static bool Prefix() {
        if (!Util.GameIsFull() || !CharacterLobby.ShouldShowPanel) return true;
        CharacterLobby.NoteStartClick();
        CharacterLobby.ConfirmPanel();
        return false;
    }
}
#endif
