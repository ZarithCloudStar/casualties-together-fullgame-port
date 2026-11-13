#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together;

internal static class SaveSlotMenu {
    sealed class Row {
        internal SaveSlotStore.Head head;
        internal string error;
    }
    static Row[] rows;
    static bool load, confirmed;
    static int selected;
    static string name = "", error = "";
    static Action<int, string> accept;
    static object session;
    static WorldGeneration world;
    static GameObject blocker, previousSelection;
    static Vector2 scroll;
    static Vector2 detailsScroll;
    internal static bool IsOpen => accept != null || !string.IsNullOrEmpty(error);

    internal static void Open(bool loading, Action<int, string> onAccept) {
        if (!Net.IsServer || !Net.IsRunning || IsOpen) return;
        SaveSlots.Ensure();
        rows = new Row[5];
        for (int i = 0; i < 5; i++) {
            rows[i] = new Row();
            try { rows[i].head = SaveSlots.Store.Read(i + 1); }
            catch (Exception ex) { rows[i].error = "存档损坏或不可读取"; log.error($"SLOTS: inspect slot={i + 1}: {ex}"); }
        }
        load = loading; selected = 0; confirmed = false; scroll = detailsScroll = Vector2.zero;
        name = ""; error = ""; accept = onAccept;
        session = Net.ServerEntityManager; world = WorldGeneration.world;
        BlockCanvas();
        log.l("SLOTS: opened " + (load ? "load" : "save") + " selector");
    }
    static void BlockCanvas() {
        if (blocker != null) return;
        previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        blocker = new GameObject("SaveSlotInputBlocker", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        var canvas = blocker.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = short.MaxValue;
        var panel = new GameObject("遮挡底层按钮", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(blocker.transform, false);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
        panel.GetComponent<Image>().color = Color.clear;
    }
    internal static void ShowError(string message) {
        if (!SaveSlots.Enabled || !Net.IsServer) return;
        error = message; session = Net.ServerEntityManager; world = WorldGeneration.world; BlockCanvas();
    }
    static void Close() {
        accept = null; error = ""; rows = null; selected = 0;
        if (blocker != null) UnityEngine.Object.Destroy(blocker);
        blocker = null;
        if (EventSystem.current != null && previousSelection != null && previousSelection.activeInHierarchy)
            EventSystem.current.SetSelectedGameObject(previousSelection);
        previousSelection = null;
    }
    internal static void Tick() {
        if (!IsOpen) return;
        if (!Net.IsServer || !Net.IsRunning || !ReferenceEquals(session, Net.ServerEntityManager) || !ReferenceEquals(world, WorldGeneration.world)) Close();
    }
    internal static void Draw() {
        if (!IsOpen || !Net.IsServer || !Net.IsRunning) return;
        bool enabled = GUI.enabled;
        try {
            GUI.enabled = true;
            Rect panel = MenuDialogUI.Panel(1080, 740, out float scale);
            float pad = Mathf.Max(12, 22 * scale), gap = Mathf.Max(8, 14 * scale);
            Rect area = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, panel.height - pad * 2);
            var title = MenuDialogUI.Text(scale, 1.3f);
            var body = MenuDialogUI.Text(scale);
            var muted = MenuDialogUI.Text(scale, .85f, MenuDialogUI.Muted);
            if (accept == null) {
                float yError = MenuDialogUI.Label(area, area.y, "存档操作提示", title);
                MenuDialogUI.Label(area, yError + gap, error, body);
                if (MenuDialogUI.Button(new Rect(area.xMax - 140 * scale, area.yMax - 42 * scale, 140 * scale, 42 * scale), "关闭", scale)) Close();
                return;
            }
            float y = MenuDialogUI.Label(area, area.y, load ? "继续探险" : "保存并退出", title);
            y = MenuDialogUI.Label(area, y + gap * .4f, load ? "选择存档槽位，确认后继续旅程" : "选择存档槽位，为这段旅程命名", muted);
            float buttonHeight = Mathf.Max(34, body.fontSize * 1.9f);
            Rect footer = new Rect(area.x, area.yMax - buttonHeight, area.width, buttonHeight);
            Rect content = new Rect(area.x, y + gap, area.width, Mathf.Max(40, footer.y - gap - y - gap));
            bool columns = content.width >= 650;
            Rect list = columns ? new Rect(content.x, content.y, content.width * .55f - gap, content.height) :
                new Rect(content.x, content.y, content.width, content.height * .53f - gap);
            Rect detail = columns ? new Rect(list.xMax + gap, content.y, content.xMax - list.xMax - gap, content.height) :
                new Rect(content.x, list.yMax + gap, content.width, content.yMax - list.yMax - gap);
            DrawRows(list, scale, body, muted, gap);
            DrawDetails(detail, scale, body, muted, gap);
            float cancelWidth = Mathf.Min(180 * scale, footer.width * .3f);
            bool cancel = MenuDialogUI.Button(new Rect(footer.x, footer.y, cancelWidth, footer.height), "取消", scale);
            string action = load ? "确认加载" : confirmed ? "确认覆盖并退出" :
                selected > 0 && (rows[selected - 1].head != null || rows[selected - 1].error != null) ? "覆盖此槽位…" : "新建并退出";
            bool submit = MenuDialogUI.Button(new Rect(footer.x + cancelWidth + gap, footer.y, footer.width - cancelWidth - gap, footer.height), action, scale, selected != 0, selected != 0);
            if (cancel) { Close(); return; }
            if (submit) Submit();
        } finally { GUI.enabled = enabled; }
    }
    static string SavedTime(SaveSlotStore.Meta meta, bool full = false) {
        try { return DateTimeOffset.FromUnixTimeSeconds(meta.savedAtUnix).LocalDateTime.ToString(full ? "yyyy-MM-dd HH:mm:ss" : "MM-dd HH:mm"); }
        catch { return "时间未知"; }
    }
    static void DrawRows(Rect area, float scale, GUIStyle body, GUIStyle muted, float gap) {
        float width = area.width - 18;
        float pad = Mathf.Max(8, 12 * scale);
        var names = new string[5]; var summaries = new string[5]; var versions = new string[5]; var heights = new float[5];
        float total = 0;
        for (int i = 0; i < 5; i++) {
            var meta = rows[i].head?.meta;
            names[i] = $"{i + 1:00}   " + (meta?.name ?? rows[i].error ?? "空槽位");
            summaries[i] = meta == null ? (rows[i].error == null ? "可新建 · 暂无存档" : "不可加载 · 可确认覆盖") :
                $"{SavedTime(meta)}   第 {meta.layer} 层 · {meta.playerCount} 人";
            versions[i] = meta == null ? (load ? "请选择已有存档" : "选择后输入名称") : $"{meta.modVersion} / {meta.buildTag} · 可覆盖";
            heights[i] = pad * 2 + MenuDialogUI.TextHeight(names[i], body, width - pad * 2) +
                MenuDialogUI.TextHeight(summaries[i], muted, width - pad * 2) + MenuDialogUI.TextHeight(versions[i], muted, width - pad * 2);
            total += heights[i] + gap * .6f;
        }
        scroll = GUI.BeginScrollView(area, scroll, new Rect(0, 0, width, total));
        try {
            float y = 0;
            for (int i = 0; i < 5; i++) {
                var row = rows[i]; var meta = row.head?.meta;
                var card = new Rect(0, y, width, heights[i]);
                bool usable = !load || meta != null;
                if (selected == i + 1) {
                    MenuDialogUI.Box(card, new Color(.13f, .12f, .09f), MenuDialogUI.Accent);
                    MenuDialogUI.Fill(new Rect(card.x, card.y, 3, card.height), MenuDialogUI.Accent);
                }
                bool click = MenuDialogUI.Button(card, "", scale, usable, selected == i + 1);
                if (selected == i + 1) MenuDialogUI.Fill(new Rect(card.x, card.y, 3, card.height), MenuDialogUI.Accent);
                var textArea = new Rect(card.x + pad, card.y + pad, card.width - pad * 2, card.height - pad * 2);
                var nameStyle = usable ? body : muted;
                float line = MenuDialogUI.Label(textArea, textArea.y, names[i], nameStyle);
                line = MenuDialogUI.Label(textArea, line, summaries[i], muted);
                MenuDialogUI.Label(textArea, line, versions[i], muted);
                if (click) {
                    selected = i + 1; confirmed = false; error = ""; detailsScroll = Vector2.zero;
                    name = meta?.name ?? $"第{WorldGeneration.world?.biomeDepth ?? 1}层存档";
                }
                y += heights[i] + gap * .6f;
            }
        } finally { GUI.EndScrollView(); }
    }
    static void DrawDetails(Rect area, float scale, GUIStyle body, GUIStyle muted, float gap) {
        MenuDialogUI.Box(area, new Color(.04f, .045f, .06f), new Color(.27f, .29f, .32f));
        float pad = Mathf.Max(8, 14 * scale);
        GUILayout.BeginArea(new Rect(area.x + pad, area.y + pad, area.width - pad * 2, Mathf.Max(1, area.height - pad * 2)));
        try {
            detailsScroll = GUILayout.BeginScrollView(detailsScroll);
            try {
                GUILayout.Label(selected == 0 ? "存档信息" : $"槽位 {selected:00}", MenuDialogUI.Text(scale, 1.1f));
                GUILayout.Space(gap);
                if (selected == 0) {
                    GUILayout.Label(load ? "选择一个已有存档，查看详情。\n空槽位无法加载。" : "选择一个槽位保存当前旅程。\n已有存档需再次确认后才会覆盖。", body);
                } else {
                    var row = rows[selected - 1]; var meta = row.head?.meta;
                    GUILayout.Label(meta?.name ?? row.error ?? "空槽位", body);
                    if (meta != null) {
                        GUILayout.Label($"保存时间：{SavedTime(meta, true)}\n层数：{meta.layer}   玩家：{meta.playerCount}\n主机：{meta.hostName}", muted);
                        GUILayout.Space(gap);
                        GUILayout.Label($"模组版本：{meta.modVersion}\n构建：{meta.buildTag}", muted);
                    }
                    GUILayout.Space(gap);
                    if (!load) {
                        GUILayout.Label("存档名称", body);
                        var field = MenuDialogUI.Text(scale, 1, Color.white, false);
                        field.normal.background = GUI.skin.textField.normal.background;
                        field.focused.background = GUI.skin.textField.focused.background;
                        field.padding = new RectOffset(8, 8, 4, 4);
                        string edited = GUILayout.TextField(name, 64, field, GUILayout.Height(Mathf.Max(32, body.fontSize * 1.9f)));
                        if (edited != name) { name = edited; confirmed = false; }
                        GUILayout.Label("最多 24 字；非法字符会自动去除。", muted);
                    } else if (meta != null && (meta.modVersion != Plugin.MOD_VERSION || meta.buildTag != KSteam.GetModBuildTag()))
                        GUILayout.Label("版本或构建与本机不同，可能导致不同步。确认后仍可加载。", MenuDialogUI.Text(scale, .9f, MenuDialogUI.Accent));
                    if (confirmed) {
                        GUILayout.Space(gap);
                        GUILayout.Label($"确认覆盖「{meta?.name ?? row.error}」？\n点击下方“确认覆盖并退出”后提交。", MenuDialogUI.Text(scale, 1, MenuDialogUI.Accent));
                    }
                }
                if (!string.IsNullOrEmpty(error)) { GUILayout.Space(gap); GUILayout.Label(error, MenuDialogUI.Text(scale, 1, MenuDialogUI.Accent)); }
            } finally { GUILayout.EndScrollView(); }
        } finally { GUILayout.EndArea(); }
    }
    static void Submit() {
        try {
            if (!load) name = SaveSlotStore.CleanName(name);
            if (!load && !confirmed && (rows[selected - 1].head != null || rows[selected - 1].error != null)) { confirmed = true; return; }
            var action = accept; int slot = selected; string chosenName = name;
            object oldSession = session; var oldWorld = world;
            Close();
            CharacterSelection.DeferUiAction("存档槽位确认", () => {
                if (!Net.IsServer || !Net.IsRunning || !ReferenceEquals(oldSession, Net.ServerEntityManager) || !ReferenceEquals(oldWorld, WorldGeneration.world)) return;
                try { action(slot, chosenName); }
                catch (Exception ex) { SaveSlots.Report("存档操作失败，原存档已保留，请重试。", ex); }
            });
        } catch (Exception ex) { error = ex.Message; }
    }
}

[HarmonyPatch(typeof(Plugin), "Update")]
internal static class SaveSlotMenuTick {
    static void Postfix() { SaveSlotMenu.Tick(); SaveSlots.Tick(); }
}
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleInput))]
internal static class SaveSlotMenuInput { static bool Prefix() => !SaveSlotMenu.IsOpen && !UpdateChecker.PopupOpen; }
#endif
