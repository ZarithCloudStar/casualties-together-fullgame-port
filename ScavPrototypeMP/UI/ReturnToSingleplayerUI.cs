using CasualtiesTogetherUtils;
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together;

// Small centered confirm dialog used by the main-menu Play/Tutorial icons while
// the local player is in a room. Drawn from UIBullshit.OnGUI so it works with
// the multiplayer menu closed; the uGUI shield keeps the native menu buttons
// from receiving the same click.
internal static class ReturnToSingleplayerUI {
    static bool open;
    static GameObject blocker, previousSelection;

    internal static bool IsOpen => open;

    internal static void Open() {
        if (open) return;
        open = true;
        BlockCanvas();
        Util.PlayUISound(PlayerCamera.UISoundType.Click);
    }

    internal static void Close() {
        if (!open) return;
        open = false;
        if (blocker != null) UnityEngine.Object.Destroy(blocker);
        blocker = null;
        if (EventSystem.current != null && previousSelection != null && previousSelection.activeInHierarchy)
            EventSystem.current.SetSelectedGameObject(previousSelection);
        previousSelection = null;
        Util.PlayUISound(PlayerCamera.UISoundType.Close);
    }

    static void BlockCanvas() {
        if (blocker != null) return;
        previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        blocker = new GameObject("CT_ReturnToSingleplayerShield", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        var canvas = blocker.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue - 1;
        var panel = new GameObject("遮挡底层按钮", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(blocker.transform, false);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
        var image = panel.GetComponent<Image>();
        image.color = new Color(0, 0, 0, .55f);
        image.raycastTarget = true;
    }

    internal static void Draw() {
        if (!open) return;
        var panel = MenuDialogUI.Panel(600, 300, out float scale);
        float pad = Mathf.Max(14, 24 * scale);
        var area = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, panel.height - pad * 2);
        var title = MenuDialogUI.Text(scale, 1.35f);
        title.alignment = TextAnchor.MiddleCenter;
        var body = MenuDialogUI.Text(scale, 1.15f);
        body.alignment = TextAnchor.MiddleCenter;

        float y = MenuDialogUI.Label(area, area.y, Lang.Get("return_sp_title", true) ?? "回到单人模式", title);
        MenuDialogUI.Label(area, y + 12 * scale, Lang.Get("return_sp_confirm", true) ?? "您确定要回到单人模式吗？", body);

        float buttonW = Mathf.Min(170 * scale, (area.width - 16 * scale) * .5f);
        float buttonH = 46 * scale;
        float gap = 14 * scale;
        var okRect = new Rect(area.center.x - buttonW - gap * .5f, area.yMax - buttonH, buttonW, buttonH);
        var cancelRect = new Rect(area.center.x + gap * .5f, area.yMax - buttonH, buttonW, buttonH);
        bool ok = MenuDialogUI.Button(okRect, Lang.Get("return_sp_ok", true) ?? "确定", scale, true, true);
        bool cancel = MenuDialogUI.Button(cancelRect, Lang.Get("return_sp_cancel", true) ?? "取消", scale);

        var key = Event.current;
        if (key != null && key.type == EventType.KeyDown && key.keyCode == KeyCode.Escape) {
            cancel = true;
            key.Use();
        }

        if (cancel) { Close(); return; }
        if (!ok) return;
        Close();
        LeaveToSingleplayer.Confirm();
    }
}
