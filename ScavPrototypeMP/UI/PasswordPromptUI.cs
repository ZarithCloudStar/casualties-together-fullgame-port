#if FULLGAME
using System;
using UnityEngine;

namespace Together;

// Shared centered password prompt. The IP flow connects first with an empty
// password; if the server rejects with "Wrong password", this prompt retries
// the same address with the entered password.
internal static class PasswordPromptUI {
    static bool open;
    static string input = "";
    static string error = "";
    static string address = "";
    static Action<string> accept;
    static bool focus;

    internal static bool IsOpen => open;

    internal static void OpenForIp(string ipport) {
        address = ipport;
        input = "";
        error = "";
        accept = value => {
            Multiplayer.INPUT_PASSWORD = value;
            TransportLiteNetLib.OnWantToConnect(address, Net.NetType.Client);
        };
        open = true;
        focus = true;
        UIMainMenu.current_tab = UIMainMenu.menutab_directconnect;
        UIMainMenu.SetOpen(true);
    }

    internal static void Close() {
        open = false;
        accept = null;
        input = "";
        error = "";
        focus = false;
    }

    internal static void Draw() {
        if (!open) return;

        var panel = MenuDialogUI.Panel(560, 290, out float scale);
        var title = MenuDialogUI.Text(scale, 1.3f);
        var muted = MenuDialogUI.Text(scale, .9f, MenuDialogUI.Muted);
        float pad = Mathf.Max(12, 24 * scale);
        var area = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, panel.height - pad * 2);

        float y = MenuDialogUI.Label(area, area.y, Lang.Get("password_title", true) ?? "Enter password", title);
        string hint = Lang.Get("password_ip_hint", true) ?? "The server requires a password. Enter it to retry.";
        y = MenuDialogUI.Label(area, y + 6 * scale, hint, muted);

        var fieldRect = new Rect(area.x, y + 10 * scale, area.width, 42 * scale);
        GUI.SetNextControlName("CT_PasswordInput");
        var fieldStyle = MenuDialogUI.Text(scale, 1.1f, Color.white, false);
        fieldStyle.alignment = TextAnchor.MiddleLeft;
        fieldStyle.padding = new RectOffset(Mathf.RoundToInt(10 * scale), 8, 4, 4);
        input = GUI.PasswordField(fieldRect, input ?? "", '*', fieldStyle);

        if (!string.IsNullOrEmpty(error)) {
            MenuDialogUI.Label(area, fieldRect.yMax + 4 * scale, error, MenuDialogUI.Text(scale, .85f, MenuDialogUI.Accent));
        }

        float buttonW = Mathf.Min(150 * scale, (area.width - 12 * scale) * .5f);
        float buttonH = 44 * scale;
        float gap = 12 * scale;
        var okRect = new Rect(area.center.x - buttonW - gap * .5f, area.yMax - buttonH, buttonW, buttonH);
        var cancelRect = new Rect(area.center.x + gap * .5f, area.yMax - buttonH, buttonW, buttonH);
        bool ok = MenuDialogUI.Button(okRect, Lang.Get("password_ok", true) ?? "OK", scale, true, true);
        bool cancel = MenuDialogUI.Button(cancelRect, Lang.Get("password_cancel", true) ?? "Cancel", scale);

        var key = Event.current;
        if (key != null && key.type == EventType.KeyDown) {
            if (key.keyCode == KeyCode.Return || key.keyCode == KeyCode.KeypadEnter) { ok = true; key.Use(); }
            else if (key.keyCode == KeyCode.Escape) { cancel = true; key.Use(); }
        }

        if (focus) {
            GUI.FocusControl("CT_PasswordInput");
            focus = false;
        }

        if (cancel) {
            Close();
            return;
        }
        if (!ok) return;

        if (string.IsNullOrEmpty(input)) {
            error = Lang.Get("password_empty", true) ?? "Enter a password.";
            focus = true;
            return;
        }

        var callback = accept;
        string value = input;
        Close();
        callback?.Invoke(value);
    }
}
#endif
