using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Together;

// The old names still exist in 1.0's resources/sharedassets1, but those sprites
// are not all loaded in the main menu. Supply cached, usable menu substitutes.
internal static class FullgameUISprites {
    static readonly Dictionary<string, Sprite> generated = new();

    internal static Sprite Resolve(string name, Sprite[] loaded) {
        switch (name) {
            case "uiBlockMini":
                return loaded.FirstOrDefault(s => s.name == "uiBlockNano") ?? Make(name);
            case "UIElement8px":
                return loaded.FirstOrDefault(s => s.name == "uiBlockSmall") ?? Make(name);
            case "UICheckMark":
                return CoopModAssets.checkbox_ticked != null ? CoopModAssets.checkbox_ticked : Make(name);
            case "uicursor":
            case "uicursorpressed":
            case "UIFoldoutClosed":
            case "UIFoldoutOpened":
            case "uigradientblack":
            case "uiPoint":
            case "userbarfill":
                return Make(name);
            default:
                return null;
        }
    }

    static Sprite Make(string name) {
        if (generated.TryGetValue(name, out var existing) && existing != null) return existing;
        const int size = 16;
        var pixels = new Color[size * size];
        bool closed = name == "UIFoldoutClosed", opened = name == "UIFoldoutOpened";
        bool cursor = name == "uicursor" || name == "uicursorpressed";
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                Color color = Color.white;
                if (closed) color.a = x >= 5 && x <= 10 && Mathf.Abs(y - 7) <= 10 - x ? 1f : 0f;
                else if (opened) color.a = y >= 5 && y <= 10 && Mathf.Abs(x - 7) <= y - 5 ? 1f : 0f;
                else if (cursor) {
                    color.a = x >= 2 && x <= 10 && y >= 3 && x <= (size - 1 - y) / 2 + 2 ? 1f : 0f;
                    if (name == "uicursorpressed") color *= new Color(0.65f, 0.65f, 0.65f, 1f);
                } else if (name == "uigradientblack") color = new Color(0f, 0f, 0f, x / (size - 1f));
                else if (name == "uiPoint") color.a = x >= 6 && x <= 9 && y >= 6 && y <= 9 ? 1f : 0f;
                else if (name == "UICheckMark") color.a = (x >= 3 && x <= 6 && y == 10 - x) || (x >= 6 && x <= 12 && y == x - 2) ? 1f : 0f;
                pixels[y * size + x] = color;
            }
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) {
            name = "TogetherFallback_" + name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        sprite.name = texture.name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        generated[name] = sprite;
        return sprite;
    }
}
