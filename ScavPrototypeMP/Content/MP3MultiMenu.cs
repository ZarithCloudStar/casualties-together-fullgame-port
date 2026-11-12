using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together;

[HarmonyPatch(typeof(MP3Menu), "Start")]
internal static class MP3MultiMenuStart {
    static void Postfix(MP3Menu __instance) {
        if (MP3MultiLibrary.Enabled && __instance.dropdown.enabled && __instance.GetComponent<MP3MultiMenu>() == null && __instance.gameObject != MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance)
            __instance.gameObject.AddComponent<MP3MultiMenu>();
    }
}

// The original dropdown owns the visible options; its static list always remains the HOST library.
public sealed class MP3MultiMenu : MonoBehaviour {
    MP3Menu menu;
    GameObject panel;
    RectTransform content;
    TextMeshProUGUI status;
    knetid selectedOwner;
    int revision = -1, hostCount = -1;
    int lastRow = -1;
    float lastClick = -1;
    List<MP3Track> displayed = new();
    bool hostReady;
    bool restoring;

    void Start() {
        menu = GetComponent<MP3Menu>();
        Build(); Refresh();
    }
    void Update() {
        if (!MP3MultiLibrary.Enabled) {
            if (MP3MultiLibrary.SyncEnabled) { RestoreHostNow(); return; }
            if (!restoring) { restoring = true; StartCoroutine(RestoreVanilla()); }
            return;
        }
        int count = MP3Menu.dropdownList?.Count ?? 0;
        bool ready = selectedOwner.id != 0 || (Net.IsServer ? !MP3PlayerServerAudioStreamer.unprivated_MP3Menu_loadingMusic : MP3PlayerServerAudioStreamer.client_host_list_ready);
        if (revision != MP3MultiLibrary.Revision || hostCount != count || hostReady != ready) Refresh();
    }
    internal void RestoreHostNow() {
        menu ??= GetComponent<MP3Menu>();
        restoring = true;
        selectedOwner = (ushort)0; displayed.Clear(); lastRow = -1;
        if (panel != null) panel.SetActive(false);
        menu.dropdown.Hide(); menu.dropdown.ClearOptions();
        menu.UpdateList();
        menu.dropdown.AddOptions(MP3Menu.dropdownList ?? new List<TMP_Dropdown.OptionData>());
        menu.dropdown.SetValueWithoutNotify(0); menu.dropdown.RefreshShownValue();
        menu.dropdown.interactable = true;
        Destroy(this);
    }
    IEnumerator RestoreVanilla() {
        // Give the world/rule lifecycle a frame to cancel network loaders first.
        yield return null;
        yield return null;
        if (MP3MultiLibrary.Enabled) { restoring = false; yield break; }
        if (menu != null) {
            menu.dropdown.Hide();
            menu.dropdown.ClearOptions();
            menu.UpdateList();
            menu.dropdown.AddOptions(MP3Menu.dropdownList ?? new List<TMP_Dropdown.OptionData>());
            menu.dropdown.interactable = true;
        }
        Destroy(this);
    }
    RectTransform Rect(string name, Transform parent, Vector2 size) {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false); rect.sizeDelta = size;
        return rect;
    }
    TextMeshProUGUI Label(Transform parent, string text, Vector2 size) {
        var rt = Rect("Label", parent, size);
        var label = rt.gameObject.AddComponent<TextMeshProUGUI>();
        label.font = menu.dropdown.captionText.font;
        label.fontSize = Mathf.Clamp(menu.dropdown.captionText.fontSize, 14, 24);
        label.color = Color.white; label.richText = false;
        label.alignment = TextAlignmentOptions.Center; label.text = text;
        label.enableWordWrapping = true; label.raycastTarget = false;
        return label;
    }
    Button Button(Transform parent, string text, Vector2 size, Action clicked) {
        var rt = Rect("MP3 source", parent, size);
        var image = rt.gameObject.AddComponent<Image>(); image.color = Color.black;
        var border = rt.gameObject.AddComponent<Outline>(); border.effectColor = Color.white; border.effectDistance = Vector2.one;
        var button = rt.gameObject.AddComponent<Button>();
        var colors = button.colors; colors.highlightedColor = Color.gray; colors.pressedColor = Color.gray;
        colors.disabledColor = new Color(0.25f, 0.25f, 0.25f); button.colors = colors;
        var label = Label(rt, text, size - new Vector2(12, 2));
        label.enableAutoSizing = true; label.fontSizeMin = 10; label.fontSizeMax = label.fontSize;
        label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.sizeDelta = new Vector2(-12, -2);
        button.onClick.AddListener(() => clicked()); return button;
    }
    void Build() {
        var d = (RectTransform)menu.dropdown.transform;
        var rt = Rect("MP3 Players", menu.transform, new Vector2(220, 360));
        panel = rt.gameObject;
        // Align beside the outer menu frame where possible, otherwise beside the dropdown.
        RectTransform edge = d;
        var canvas = menu.GetComponentInParent<Canvas>();
        var canvasRect = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
        // TMP places its fullscreen blocker at 29999. Keep source buttons clickable
        // while the song list is open so switching players takes a single click.
        var playerCanvas = panel.AddComponent<Canvas>();
        playerCanvas.overrideSorting = true; playerCanvas.sortingOrder = 30001;
        if (canvas != null) playerCanvas.sortingLayerID = canvas.rootCanvas.sortingLayerID;
        panel.AddComponent<GraphicRaycaster>();
        for (var ancestor = d.parent; ancestor != null; ancestor = ancestor.parent) {
            var image = ancestor.GetComponent<Image>();
            if (image != null && image.rectTransform.rect.width >= d.rect.width &&
                (canvasRect == null || image.rectTransform.rect.width < canvasRect.rect.width * 0.9f)) {
                edge = image.rectTransform; break;
            }
            if (ancestor == menu.transform) break;
        }
        var corners = new Vector3[4]; edge.GetWorldCorners(corners);
        var local = menu.transform.InverseTransformPoint(corners[2]);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0, 1);
        rt.localPosition = new Vector3(local.x + 16, local.y, 0);
        rt.gameObject.AddComponent<Image>().color = Color.black;
        var outline = rt.gameObject.AddComponent<Outline>(); outline.effectColor = Color.white; outline.effectDistance = Vector2.one;
        var header = Label(rt, Lang.Get("mp3_players_header"), new Vector2(208, 32));
        header.rectTransform.anchoredPosition = new Vector2(110, -20);
        header.rectTransform.anchorMin = header.rectTransform.anchorMax = new Vector2(0, 1);
        status = Label(rt, "", new Vector2(208, 46));
        status.fontSize = 14;
        status.rectTransform.anchorMin = status.rectTransform.anchorMax = new Vector2(0, 1);
        status.rectTransform.anchoredPosition = new Vector2(110, -292);
        var stop = Button(rt, Lang.Get("mp3_stop"), new Vector2(196, 30), () => MP3MultiLibrary.Request(MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player, (ushort)0, "", -1));
        var stopRect = (RectTransform)stop.transform; stopRect.anchorMin = stopRect.anchorMax = new Vector2(0, 1); stopRect.anchoredPosition = new Vector2(110, -334);
        var viewport = Rect("Player viewport", rt, new Vector2(208, 226));
        viewport.anchorMin = viewport.anchorMax = new Vector2(0, 1); viewport.pivot = new Vector2(0.5f, 1); viewport.anchoredPosition = new Vector2(110, -42);
        viewport.gameObject.AddComponent<RectMask2D>();
        var scroll = viewport.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
        content = Rect("Players", viewport, new Vector2(200, 226));
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1); content.sizeDelta = new Vector2(0, 226);
        scroll.viewport = viewport; scroll.content = content;
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>(); layout.spacing = 5; layout.padding = new RectOffset(5, 5, 3, 3); layout.childControlHeight = false; layout.childControlWidth = true; layout.childForceExpandHeight = false;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }
    void Refresh() {
        lastRow = -1;
        revision = MP3MultiLibrary.Revision; hostCount = MP3Menu.dropdownList?.Count ?? 0;
        if (selectedOwner.id != 0 && !MP3MultiLibrary.Sources.ContainsKey(selectedOwner)) selectedOwner = (ushort)0;
        foreach (Transform child in content) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        var sources = MP3MultiLibrary.Sources.Values.OrderBy(s => s.Owner.id).ToList();
        if (!sources.Any(s => s.Owner.id == 0)) sources.Insert(0, new MP3LibrarySource { Owner = (ushort)0, Name = Lang.Get("mp3_host") });
        foreach (var source in sources) {
            knetid id = source.Owner;
            string name = id.id == 0 ? Lang.Get("mp3_host") + " · " + source.Name : source.Name;
            string text = string.Format(Lang.Get("mp3_source_format"), name, source.Tracks.Count);
            if (selectedOwner.Equals(id)) text = "> " + text;
            var b = Button(content, text, new Vector2(196, 36), () => { selectedOwner = id; lastRow = -1; Refresh(); });
            b.interactable = id.id == 0 || source.Tracks.Count > 0;
            if (!b.interactable) b.GetComponentInChildren<TextMeshProUGUI>().color = Color.gray;
        }
        menu.dropdown.Hide();
        int oldValue = menu.dropdown.value;
        displayed = selectedOwner.id == 0 || !MP3MultiLibrary.Sources.TryGetValue(selectedOwner, out var s) ? new List<MP3Track>() : s.Tracks.ToList();
        var options = selectedOwner.id == 0 ? MP3Menu.dropdownList ?? new List<TMP_Dropdown.OptionData>() : displayed.Select(t => new TMP_Dropdown.OptionData(t.Name + " [" + t.Hash.Substring(0, 6) + "]")).ToList();
        hostReady = selectedOwner.id != 0 || (Net.IsServer ? !MP3PlayerServerAudioStreamer.unprivated_MP3Menu_loadingMusic : MP3PlayerServerAudioStreamer.client_host_list_ready);
        menu.dropdown.ClearOptions();
        if (options.Count == 0) menu.dropdown.AddOptions(new List<string> { hostReady ? Lang.Get("mp3_no_tracks") : Lang.Get("mp3_loading") });
        else menu.dropdown.AddOptions(options);
        menu.dropdown.SetValueWithoutNotify(Mathf.Clamp(oldValue, 0, Math.Max(0, options.Count - 1)));
        menu.dropdown.RefreshShownValue(); menu.dropdown.interactable = options.Count > 0 && hostReady;
        status.text = options.Count == 0 ? (hostReady ? Lang.Get("mp3_no_tracks") : Lang.Get("mp3_loading")) : Lang.Get("mp3_play_hint");
    }
    internal void PlaySelected() {
        if (!MP3MultiLibrary.Enabled || !hostReady || !menu.dropdown.interactable) return;
        int index = menu.dropdown.value;
        if (selectedOwner.id == 0) MP3MultiLibrary.Request(MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player, (ushort)0, "", index);
        else if (index >= 0 && index < displayed.Count) MP3MultiLibrary.Request(MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player, selectedOwner, displayed[index].Id, 0);
    }
    internal void Click(int row, PointerEventData e) {
        if (e.button != PointerEventData.InputButton.Left && e.button != PointerEventData.InputButton.Right) return;
        bool play = e.button == PointerEventData.InputButton.Right;
        if (e.button == PointerEventData.InputButton.Left) {
            play = lastRow == row && Time.unscaledTime - lastClick < 0.35f;
            lastRow = row; lastClick = Time.unscaledTime;
        }
        menu.dropdown.value = row;
        foreach (var item in menu.dropdown.GetComponentsInChildren<MP3TrackPointer>(false))
            item.GetComponent<Toggle>().SetIsOnWithoutNotify(item.Row == row);
        if (play) { PlaySelected(); menu.dropdown.Hide(); lastRow = -1; }
    }
    void OnDestroy() { if (panel != null) Destroy(panel); }
}

// TMP normally hides the list on its first left click, making a double click on a row
// impossible. Keep this menu's list open; outside clicks and the dropdown still close it.
[HarmonyPatch(typeof(TMP_Dropdown), "OnSelectItem")]
internal static class MP3DropdownSelect {
    static bool Prefix(TMP_Dropdown __instance, Toggle toggle) {
        var click = toggle.GetComponent<MP3TrackPointer>();
        if (!MP3MultiLibrary.Enabled || click == null || click.Menu == null) return true;
        toggle.SetIsOnWithoutNotify(true);
        __instance.value = click.Row;
        return false;
    }
}
public sealed class MP3TrackPointer : MonoBehaviour, IPointerClickHandler {
    internal MP3MultiMenu Menu;
    internal int Row;
    public void OnPointerClick(PointerEventData e) { if (Menu != null) Menu.Click(Row, e); }
}
[HarmonyPatch(typeof(TMP_Dropdown), nameof(TMP_Dropdown.Show))]
internal static class MP3DropdownShow {
    static void Postfix(TMP_Dropdown __instance) {
        var menu = __instance.GetComponentInParent<MP3MultiMenu>();
        if (menu == null || !MP3MultiLibrary.Enabled) return;
        int row = 0;
        foreach (var toggle in __instance.GetComponentsInChildren<Toggle>(false)) {
            if (toggle.transform.IsChildOf(__instance.template)) continue;
            var click = toggle.gameObject.GetComponent<MP3TrackPointer>() ?? toggle.gameObject.AddComponent<MP3TrackPointer>();
            click.Menu = menu; click.Row = row++;
        }
    }
}
