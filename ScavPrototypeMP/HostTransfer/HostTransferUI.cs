using System;
using System.Linq;
using CasualtiesTogetherUtils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

// Only presentation and button lifetime live here. Consent, sender authority,
// save policy and reconnection stay in LobbyHostTransfer.
internal sealed class HostTransferUI : CasualtiesTogetherSingleton {
    private enum View { Closed, Players, Offer, Progress, Notice }
    private static HostTransferUI instance;
    private View view;
    private bool atExit, copySave;
    private int sendSaveSlot;
    private string sendSaveName;
    private int page;
    private string offerToken, offerHost, error;
    private string noticeText;
    private LobbyHostTransfer.TransferMode offerMode;
    private GameObject canvas;
    private RectTransform panel;
    private TextMeshProUGUI statusLabel;
    private Button cancelButton, exitButton;
    private WorldGeneration buttonWorld;
    private double closeAt;
    private double nextButtonSearch;
    private int buttonSearches;
#if FULLGAME
    private bool waitForCharacterNotice;
    private bool sawCharacterPanel;
    private string deferredNoticeText;
    private float deferredNoticeStarted;
#endif
    internal static bool SelectingAtExit => instance != null && instance.view == View.Players && instance.atExit;
    internal static bool IsModalOpen => instance != null && instance.canvas != null && instance.canvas.activeInHierarchy;
    private static string T(string key, string fallback) => Lang.Get("host_transfer_" + key, true) ?? fallback;

    private void Awake() { instance = this; }
    private void OnDestroy() { if (canvas != null) Destroy(canvas); if (instance == this) instance = null; }
    private void OnGUI() {
        if (canvas != null && canvas.activeInHierarchy) UIBullshit.CheckCursorOverlap(new Rect(0, 0, Screen.width, Screen.height));
    }

    internal static void DrawLobbyAction() {
        if (!Net.IsRunningSteam || !Net.IsHost || !Util.IsInMainMenu()) return;
        bool oldEnabled = GUI.enabled;
        GUI.enabled &= !LobbyHostTransfer.Busy && ScavPlayer.AllPlayers.Count(p => p != null && !p.IsLocal) > 0;
        if (GUILayout.Button(LobbyHostTransfer.HasRequest ? T("button_status", "Host transfer status") : T("button", "Transfer host..."))) Open(false);
        GUI.enabled = oldEnabled;
    }

    internal static void Open(bool layerExit) {
        if (instance == null) return;
        instance.atExit = layerExit; instance.page = 0; instance.error = null;
        instance.sendSaveSlot = 0; instance.sendSaveName = null;
        instance.view = LobbyHostTransfer.HasRequest ? View.Progress : View.Players;
        instance.copySave = layerExit;
        instance.closeAt = Time.realtimeSinceStartupAsDouble + 60;
        instance.Render();
    }

    internal static void ShowOffer(string token, string host, LobbyHostTransfer.TransferMode mode) {
        if (instance == null) return;
        instance.offerToken = token; instance.offerHost = host; instance.offerMode = mode;
        instance.view = View.Offer; instance.error = null; instance.Render();
    }

    // The leave-room flow starts a LobbyOnlyAuto transfer without a player
    // picker. Show the existing progress view so the host knows to wait for
    // the handoff before the scene reload.
    internal static void OpenAutoProgress() {
        if (instance == null) return;
        instance.atExit = false;
        instance.error = null;
        instance.view = View.Progress;
        instance.Render();
    }

    internal static void CloseForLeave() {
        if (instance == null) return;
        instance.Close();
    }

    internal static void NotifyComplete(string message) {
        if (instance == null) return;
#if FULLGAME
        instance.waitForCharacterNotice = false;
        instance.sawCharacterPanel = false;
        instance.deferredNoticeText = null;
#endif
        instance.noticeText = message;
        instance.view = View.Notice; instance.Render();
        instance.closeAt = Time.realtimeSinceStartupAsDouble + 8;
    }

    internal static void NotifyCompleteAfterCharacter(string message) {
        if (instance == null) return;
#if FULLGAME
        instance.deferredNoticeText = message;
        instance.waitForCharacterNotice = true;
        instance.sawCharacterPanel = false;
        instance.deferredNoticeStarted = Time.unscaledTime;
        instance.Close();
#else
        NotifyComplete(message);
#endif
    }

    private void Update() {
#if FULLGAME
        if (waitForCharacterNotice) {
            if (CharacterLobby.ShouldShowPanel) sawCharacterPanel = true;
            else if (sawCharacterPanel || Time.unscaledTime - deferredNoticeStarted > 2f) {
                string message = deferredNoticeText;
                waitForCharacterNotice = false;
                deferredNoticeText = null;
                NotifyComplete(message);
            }
        }
#endif
        if (view == View.Offer && !LobbyHostTransfer.IsCurrentOffer(offerToken)) Close();
        if (view == View.Players && ((!Net.IsRunningSteam || !Net.IsHost) || Time.realtimeSinceStartupAsDouble > closeAt ||
            (atExit ? !Util.IsInWorld() : !Util.IsInMainMenu()))) Close();
        if (view == View.Progress) {
            if (statusLabel != null) statusLabel.text = LobbyHostTransfer.Status;
            if (cancelButton != null) cancelButton.interactable = LobbyHostTransfer.CanCancel;
            if (!LobbyHostTransfer.HasRequest && !LobbyHostTransfer.Busy) Close();
        }
        if (view == View.Notice && Time.realtimeSinceStartupAsDouble > closeAt) CloseNotice();
        EnsureExitButton();
        if (exitButton != null) {
            bool supported = Net.IsRunningSteam && Net.IsHost && !SavesystemPatch.PreserveSave && buttonWorld != null && !buttonWorld.doPod && buttonWorld.biomeDepth != 10;
            // Keep the third slot stable after adding it; unsupported contexts
            // disable the button rather than moving the native controls around.
            exitButton.interactable = supported && !LobbyHostTransfer.Busy && ScavPlayer.AllPlayers.Count(p => p != null && !p.IsLocal) > 0;
        }
    }

    private void Close() { view = View.Closed; if (canvas != null) canvas.SetActive(false); }
    private void CloseNotice() {
        Close();
#if FULLGAME
        if (Net.IsClient) CharacterLobby.ReopenPanel();
#endif
    }
    private void Try(Action action) {
        try { action(); } catch (Exception ex) { error = ex.Message; Render(); }
    }

    private void Render() {
        if (UIManager.template_button == null || UIManager.template_label == null) return;
        if (canvas != null) Destroy(canvas);
        canvas = new GameObject("Together Host Transfer", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvas.transform.SetParent(transform, false);
        var uiCanvas = canvas.GetComponent<Canvas>(); uiCanvas.renderMode = RenderMode.ScreenSpaceOverlay; uiCanvas.sortingOrder = 32000;
        var scaler = canvas.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
        var shade = new GameObject("Input shield", typeof(RectTransform), typeof(Image)); shade.transform.SetParent(canvas.transform, false);
        var shadeRect = (RectTransform)shade.transform; shadeRect.anchorMin = Vector2.zero; shadeRect.anchorMax = Vector2.one; shadeRect.offsetMin = shadeRect.offsetMax = Vector2.zero;
        var shadeImage = shade.GetComponent<Image>();
        shadeImage.color = new Color(0, 0, 0, 0.86f);
        shadeImage.raycastTarget = true;
        var block = UIManager.CreateBlock(canvas.transform, "Transfer host dialog", new Vector2(760, view == View.Offer ? 790 : 710));
        panel = (RectTransform)block.transform; panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f); panel.anchoredPosition = Vector2.zero;
        string title = view == View.Offer ? T("title_request", "Host transfer request") : view == View.Players ? (atExit ? T("title_continue", "Transfer host and continue") : T("title_lobby", "Transfer lobby host")) : T("title", "Host transfer");
        Label(title, 22, 52, 30);
        if (view == View.Players) RenderPlayers();
        else if (view == View.Offer) {
            Label(string.Format(T("offer", "{0} would like you to become the host."), offerHost), 87, 74, 25);
            Label(LobbyHostTransfer.OfferWarning(offerMode), 166, 350, 20);
            AddButton(offerMode == LobbyHostTransfer.TransferMode.LobbyOnly ? T("accept", "Accept hosting") : T("accept_save", "Accept hosting and replace active save"), 533, () => Try(AcceptOffer));
            AddButton(T("decline", "Decline"), 599, () => Try(() => { LobbyHostTransfer.AnswerOffer(offerToken, false); Close(); }));
        }
        else if (view == View.Progress) {
            statusLabel = Label(LobbyHostTransfer.Status, 100, 180, 27);
            Label(T("keep_open", "Keep your game open until the transfer completes. Every player must reconnect before the expedition can resume."), 302, 120, 23);
            AddButton(T("close", "Close this window"), 509, Close);
        }
        else {
            Label(noticeText, 125, 300, 28);
            AddButton(T("ok", "OK"), 507, CloseNotice);
        }
        if (!string.IsNullOrEmpty(error)) Label(error, view == View.Offer ? 675 : 628, 58, 21);
    }

    private void RenderPlayers() {
        if (atExit) Label(T("choose_next", "Choose the next host. Once they accept, this layer will continue and the expedition will resume automatically."), 80, 90, 23);
        else {
            bool canSend = false;
#if FULLGAME
            canSend = SaveSlots.Enabled && SaveSlots.HasSave;
#endif
            AddButton((copySave ? "[X] " : "[ ] ") + T("send_save", "Send your MP save file to them?"), 82, () => {
                if (!copySave) { copySave = true; sendSaveSlot = 0; sendSaveName = null; OpenSendSlotSelector(); }
                else { copySave = false; sendSaveSlot = 0; sendSaveName = null; Render(); }
            }).interactable = canSend;
            Label(copySave ? sendSaveSlot == 0 ? T("save_choose", "Choose a save slot before selecting the new host.") :
                string.Format(T("save_selected", "Selected save: {0}"), sendSaveName ?? ("Slot " + sendSaveSlot))
                : T("no_save", "Transfer hosting only. The new host keeps their own saved expedition."), 150, 70, 22);
        }
        var players = ScavPlayer.AllPlayers.Where(p => p != null && !p.IsLocal && p.SteamId != 0).OrderBy(p => p.playerName).ToArray();
        int pages = Math.Max(1, (players.Length + 3) / 4); page = Math.Min(page, pages - 1);
        for (int i = 0; i < 4 && page * 4 + i < players.Length; i++) {
            ulong steam = players[page * 4 + i].SteamId;
            string name = players[page * 4 + i].playerName;
            AddButton(string.Format(T("to", "Transfer to {0}"), name), 232 + i * 61, () => Try(() => {
                var player = ScavPlayer.AllPlayers.FirstOrDefault(p => p != null && p.SteamId == steam);
                var mode = atExit ? LobbyHostTransfer.TransferMode.NextLayer : copySave ? LobbyHostTransfer.TransferMode.LobbyWithSave : LobbyHostTransfer.TransferMode.LobbyOnly;
                if (mode == LobbyHostTransfer.TransferMode.LobbyWithSave && sendSaveSlot == 0) { OpenSendSlotSelector(); return; }
                LobbyHostTransfer.Request(player, mode, sendSaveSlot); view = View.Progress; Render();
            }));
        }
        if (players.Length == 0) Label(T("no_players", "No other players are connected."), 250, 80, 25);
        if (pages > 1) AddButton(string.Format(T("next_players", "Next players ({0}/{1})"), page + 1, pages), 480, () => { page = (page + 1) % pages; Render(); });
        AddButton(T("back", "Back"), 548, Close);
    }

    private void OpenSendSlotSelector() {
#if FULLGAME
        SaveSlotMenu.OpenTransferSource((slot, name) => {
            sendSaveSlot = slot; sendSaveName = name; copySave = true; Render();
        });
#else
        error = "Save transfer is available in the full game only.";
        Render();
#endif
    }

    private void AcceptOffer() {
        if (offerMode == LobbyHostTransfer.TransferMode.LobbyOnly) {
            LobbyHostTransfer.AnswerOffer(offerToken, true); Close(); return;
        }
        string token = offerToken;
#if FULLGAME
        SaveSlotMenu.OpenTransferReceive((slot, name) => LobbyHostTransfer.AnswerOffer(token, true, slot));
#else
        LobbyHostTransfer.AnswerOffer(token, true);
#endif
    }

    private TextMeshProUGUI Label(string text, float top, float height, float size) {
        var label = UIManager.CreateLabel(panel, text); label.richText = false; label.fontSize = size;
        label.enableAutoSizing = false; label.enableWordWrapping = true; label.alignment = TextAlignmentOptions.TopLeft;
        Place(label.rectTransform, top, height); return label;
    }
    private Button AddButton(string text, float top, Action action) {
        var button = UIManager.CreateButton(panel, text, null);
        button.onClick = new Button.ButtonClickedEvent(); button.onClick.AddListener(() => action());
        var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) { label.text = text; label.richText = false; label.fontSize = 23; label.enableAutoSizing = false; }
        foreach (var localizer in button.GetComponentsInChildren<UILocalizer>(true)) Destroy(localizer);
        Place((RectTransform)button.transform, top, 51); return button;
    }
    private static void Place(RectTransform rect, float top, float height) {
        rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one; rect.pivot = new Vector2(0.5f, 1);
        rect.sizeDelta = new Vector2(-48, height); rect.anchoredPosition = new Vector2(0, -top);
    }

    private void EnsureExitButton() {
        var world = WorldGeneration.world;
        if (world == null) { buttonWorld = null; exitButton = null; buttonSearches = 0; return; }
        if (buttonWorld != world) { buttonWorld = world; exitButton = null; buttonSearches = 0; nextButtonSearch = 0; }
        if (buttonWorld == world && exitButton != null) return;
        if (world.savePanel == null || !Net.IsHost || !Net.IsRunningSteam) return;
        if (Time.realtimeSinceStartupAsDouble < nextButtonSearch || buttonSearches >= 20) return;
        nextButtonSearch = Time.realtimeSinceStartupAsDouble + 0.5; buttonSearches++;
        exitButton = AttachExitButton(world.savePanel);
        if (exitButton == null && buttonSearches == 20) Plugin.Logger.LogWarning("[LobbyTransfer] Could not locate native save/continue buttons; layer-exit host transfer is unavailable.");
    }

    internal static Button AttachExitButton(GameObject savePanel) {
        var existing = savePanel.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "Together_TransferHostAtExit");
        if (existing != null) return existing;
        var buttons = savePanel.GetComponentsInChildren<Button>(true);
        Button continuation = buttons.FirstOrDefault(b => HasAction(b, "ContinueRun"));
        Button save = buttons.FirstOrDefault(b => HasAction(b, "SaveAndExit"));
        if (continuation == null || save == null) {
            // Some game builds attach the native listeners at runtime, so
            // Unity does not expose ContinueRun/SaveAndExit as persistent
            // method names. Keep the transfer action available by placing a
            // standalone button in the same save panel.
            var fallback = UIManager.CreateButton(savePanel.transform, T("button", "Transfer host..."), null);
            fallback.name = "Together_TransferHostAtExit";
            fallback.onClick = new Button.ButtonClickedEvent(); fallback.onClick.AddListener(() => Open(true));
            var fallbackRect = (RectTransform)fallback.transform;
            fallbackRect.anchorMin = new Vector2(.5f, 0); fallbackRect.anchorMax = new Vector2(.5f, 0); fallbackRect.pivot = new Vector2(.5f, 0);
            fallbackRect.sizeDelta = new Vector2(420, 56); fallbackRect.anchoredPosition = new Vector2(0, 28);
            Plugin.Logger?.LogInfo("[LobbyTransfer] Added fallback bottom-layer transfer button.");
            return fallback;
        }
        var added = UIManager.CreateButton(continuation.transform.parent, T("button", "Transfer host..."), null);
        added.name = "Together_TransferHostAtExit";
        added.onClick = new Button.ButtonClickedEvent(); added.onClick.AddListener(() => Open(true));
        var rt = (RectTransform)added.transform;
        var native = (RectTransform)continuation.transform;
        rt.anchorMin = native.anchorMin; rt.anchorMax = native.anchorMax; rt.pivot = native.pivot; rt.sizeDelta = native.sizeDelta;
        rt.anchoredPosition = native.anchoredPosition; rt.localScale = native.localScale;
        // Native save/continue controls share a panel. Respect a layout group if
        // present; otherwise split their existing two slots into three equal slots.
        if (continuation.transform.parent == save.transform.parent && continuation.transform.parent.GetComponent<LayoutGroup>() == null)
            ArrangeExitButtons((RectTransform)save.transform, native, rt);
        return added;
    }

    private static bool HasAction(Button button, string method) {
        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            if (button.onClick.GetPersistentMethodName(i) == method) return true;
        return false;
    }

    internal static void ArrangeExitButtons(RectTransform save, RectTransform continuation, RectTransform transfer) {
        bool horizontal = Mathf.Abs(save.anchoredPosition.x - continuation.anchoredPosition.x) >= Mathf.Abs(save.anchoredPosition.y - continuation.anchoredPosition.y);
        var ordered = new[] { save, continuation }.OrderBy(r => horizontal ? r.anchoredPosition.x : -r.anchoredPosition.y).ToArray();
        float extent = horizontal ? Mathf.Abs(ordered[1].anchoredPosition.x - ordered[0].anchoredPosition.x) + ordered[0].rect.width
            : Mathf.Abs(ordered[1].anchoredPosition.y - ordered[0].anchoredPosition.y) + ordered[0].rect.height;
        float size = (extent - 16) / 3f;
        float start = horizontal ? ordered[0].anchoredPosition.x - ordered[0].rect.width * ordered[0].pivot.x
            : ordered[0].anchoredPosition.y + ordered[0].rect.height * (1 - ordered[0].pivot.y);
        var all = new[] { ordered[0], ordered[1], transfer };
        for (int i = 0; i < all.Length; i++) {
            all[i].SetSizeWithCurrentAnchors(horizontal ? RectTransform.Axis.Horizontal : RectTransform.Axis.Vertical, size);
            var pos = all[i].anchoredPosition;
            if (horizontal) pos.x = start + i * (size + 8) + size * all[i].pivot.x;
            else pos.y = start - i * (size + 8) - size * (1 - all[i].pivot.y);
            all[i].anchoredPosition = pos;
        }
    }
}
