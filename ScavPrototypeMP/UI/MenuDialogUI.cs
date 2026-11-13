using System;
using UnityEngine;

namespace Together;

// Use the same font and normal point size as multiplayer buttons. Dialog text
// gets its own geometry: no inherited fixedHeight, bitmap offsets or three-line
// button captions. Do not mutate GUI.skin (other panels reuse it in this pass).
internal static class MenuDialogUI {
    internal static readonly Color Accent = new(1f, .72f, .28f);
    internal static readonly Color Muted = new(.68f, .71f, .75f);
    internal static float FitScale(float width, float height) => Mathf.Max(.25f,
        Mathf.Min(Mathf.Max(.5f, UIBullshit.uiScale), (Screen.width - 24) / width, (Screen.height - 24) / height));
    internal static Rect Panel(float width, float height, out float scale) {
        scale = FitScale(width, height);
        var rect = new Rect(0, 0, width * scale, height * scale);
        rect.center = new Vector2(Screen.width * .5f, Screen.height * .5f);
        Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, .38f));
        Box(rect, new Color(.025f, .03f, .045f, .96f), new Color(.65f, .67f, .7f));
        UIBullshit.CheckCursorOverlap(new Rect(0, 0, Screen.width, Screen.height));
        return rect;
    }
    internal static GUIStyle Text(float scale = 1, float multiplier = 1, Color? color = null, bool wrap = true) {
        // Parameterless construction also avoids the optimizer's shared copies
        // retaining geometry from unrelated GUIStyle clone call sites.
        var style = new GUIStyle();
        style.font = UIBullshit.USE_GAME_FONT && CoopModAssets.gamefont != null ? CoopModAssets.gamefont : GUI.skin.label.font ?? GUI.skin.font;
        float menuScale = Mathf.Max(.1f, UIBullshit.uiScale);
        style.fontSize = Mathf.Max(14, Mathf.RoundToInt(UIBullshit.GetCurFontSize() * scale / menuScale * multiplier));
        style.fontStyle = FontStyle.Normal;
        style.alignment = TextAnchor.UpperLeft;
        style.wordWrap = wrap; style.richText = false;
        style.fixedHeight = style.fixedWidth = 0;
        style.contentOffset = Vector2.zero;
        style.clipping = TextClipping.Overflow;
        style.normal.textColor = color ?? Color.white;
        return style;
    }
    internal static float TextHeight(string text, GUIStyle style, float width) =>
        Mathf.Ceil(Mathf.Max(style.lineHeight, style.CalcHeight(new GUIContent(text ?? ""), Mathf.Max(1, width))) + 4);
    internal static float Label(Rect area, float y, string text, GUIStyle style) {
        float height = TextHeight(text, style, area.width);
        GUI.Label(new Rect(area.x, y, area.width, height), text, style);
        return y + height;
    }
    internal static bool Button(Rect rect, string text, float scale, bool enabled = true, bool accent = false) {
        bool previous = GUI.enabled;
        bool usable = previous && enabled;
        bool hover = rect.Contains(Event.current.mousePosition) && usable;
        Box(rect, accent && usable ? new Color(.16f, .12f, .065f) : hover ? new Color(.16f, .17f, .20f) : new Color(.055f, .06f, .075f),
            accent && usable ? Accent : new Color(.5f, .52f, .55f, usable ? 1 : .5f));
        GUI.enabled = usable;
        bool click = GUI.Button(rect, GUIContent.none, GUIStyle.none);
        GUI.enabled = previous;
        if (!string.IsNullOrEmpty(text)) {
            var label = Text(scale, 1, !usable ? Muted : accent ? Accent : Color.white, false);
            label.alignment = TextAnchor.MiddleCenter;
            GUI.Label(new Rect(rect.x + 8, rect.y, rect.width - 16, rect.height), text, label);
        }
        return click;
    }
    internal static void Fill(Rect rect, Color color) {
        Color previous = GUI.color;
        GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = previous;
    }
    internal static void Box(Rect rect, Color fill, Color border) {
        Fill(rect, fill);
        Fill(new Rect(rect.x, rect.y, rect.width, 1), border);
        Fill(new Rect(rect.x, rect.yMax - 1, rect.width, 1), border);
        Fill(new Rect(rect.x, rect.y, 1, rect.height), border);
        Fill(new Rect(rect.xMax - 1, rect.y, 1, rect.height), border);
    }
}
