using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

internal static class WoundViewIntegration {
    private sealed class StockImage {
        internal Sprite Sprite;
        internal Vector2 Size;
    }

    // Unity instance IDs can be reused after leaving the map. Bind stock art to
    // the Image itself so a new panel cannot inherit the previous panel's skin.
    private static readonly ConditionalWeakTable<Image, StockImage> Images = new();
    private static readonly ConditionalWeakTable<SpriteRenderer, StockImage> Renderers = new();

    internal static void ApplyAll() {
        foreach (WoundView view in Resources.FindObjectsOfTypeAll<WoundView>()) {
            if (view == null) {
                continue;
            }

            if (!view.gameObject.scene.IsValid()) {
                continue;
            }

            Apply(view);
        }
    }

    // species retarget swaps stock head sprites; keep the lookup key on the live stock art.
    internal static void RememberStock(Image image) {
        if (image == null || image.sprite == null) {
            return;
        }

        var stock = Images.GetOrCreateValue(image);
        stock.Sprite = image.sprite;
        stock.Size = image.rectTransform.sizeDelta;
    }

    internal static void RememberPanel(WoundView view) {
        foreach (Image image in view.GetComponentsInChildren<Image>(true))
            if (image != null && !Images.TryGetValue(image, out _) && image.sprite != null &&
                SkinImporter.WoundViewNames.Contains(image.sprite.name))
                RememberStock(image);
    }

    internal static void Apply(WoundView view) {
        if (view == null) {
            return;
        }

        IReadOnlyDictionary<string, Sprite> sprites = SpritesForView(view);
        HashSet<string> colored = ColoredForView(view);
        foreach (Image image in view.GetComponentsInChildren<Image>(true)) {
            if (image == null) {
                continue;
            }

            if (!Images.TryGetValue(image, out var stock)) {
                if (image.sprite == null || !SkinImporter.WoundViewNames.Contains(image.sprite.name)) continue;
                RememberStock(image);
                stock = Images.GetOrCreateValue(image);
            }
            Sprite original = stock.Sprite;
            if (original == null) continue;
            DefaultSpriteExport.Capture(original);

            if (sprites != null && sprites.TryGetValue(original.name, out var replacement) && replacement != null) {
                image.sprite = replacement;
                // Keep the prefab's anchors. SetNativeSize changes them as well
                // as the size, making a restored stretched image a large quad.
                image.rectTransform.sizeDelta = new Vector2(
                    stock.Size.x * replacement.rect.width / original.rect.width,
                    stock.Size.y * replacement.rect.height / original.rect.height);
            } else Restore(image, stock);
        }

        foreach (SpriteRenderer renderer in view.GetComponentsInChildren<SpriteRenderer>(true)) {
            if (renderer == null) {
                continue;
            }

            if (!Renderers.TryGetValue(renderer, out var stock)) {
                if (renderer.sprite == null || !SkinImporter.WoundViewNames.Contains(renderer.sprite.name)) continue;
                stock = Renderers.GetOrCreateValue(renderer);
                stock.Sprite = renderer.sprite;
            }
            Sprite original = stock.Sprite;
            if (original == null) continue;
            DefaultSpriteExport.Capture(original);

            Sprite replacement;
            if (sprites != null && sprites.TryGetValue(original.name, out replacement) && replacement != null) {
                renderer.sprite = replacement;
            } else {
                renderer.sprite = original;
            }
        }

        ShowCustomColors(view, colored);
        DefaultSpriteExport.ExportWoundSpecies(view);
        DefaultSpriteExport.ExportNew();
    }

    private static void Restore(Image image, StockImage stock) {
        image.sprite = stock.Sprite;
        image.rectTransform.sizeDelta = stock.Size;
    }

    internal static void UnbindPackage(MpSkinPackage package) {
        foreach (WoundView view in Resources.FindObjectsOfTypeAll<WoundView>()) {
            if (view == null || !view.gameObject.scene.IsValid()) continue;
            foreach (Image image in view.GetComponentsInChildren<Image>(true)) {
                if (Images.TryGetValue(image, out var stock) && stock.Sprite != null && UsesPackage(image.sprite, package))
                    Restore(image, stock);
            }
            foreach (SpriteRenderer renderer in view.GetComponentsInChildren<SpriteRenderer>(true)) {
                if (Renderers.TryGetValue(renderer, out var stock) && stock.Sprite != null && UsesPackage(renderer.sprite, package))
                    renderer.sprite = stock.Sprite;
            }
        }
    }

    private static bool UsesPackage(Sprite sprite, MpSkinPackage package) => sprite != null &&
        package.Sprites.TryGetValue(sprite.name, out var replacement) && sprite == replacement;

    // An Image whose Sprite was destroyed otherwise draws a solid, tinted quad.
    private static void RestoreMissing(Image image) {
        if (image != null && image.sprite == null && Images.TryGetValue(image, out var stock) && stock.Sprite != null)
            Restore(image, stock);
    }

    // the game tints the inner wound near black. keep a custom color visible.
    internal static void ShowCustomColors(WoundView view) {
        if (view != null && view.limbImages != null) {
            foreach (Image image in view.limbImages) {
                RestoreMissing(image);
                if (image != null && image.transform.childCount > 0)
                    RestoreMissing(image.transform.GetChild(0).GetComponent<Image>());
            }
        }
        ShowCustomColors(view, ColoredForView(view));
    }

    private static void ShowCustomColors(WoundView view, HashSet<string> colored) {
        if (view == null || view.limbImages == null || colored == null) {
            return;
        }

        foreach (Image image in view.limbImages) {
            if (image == null || image.transform.childCount == 0) {
                continue;
            }

            Image middle = image.transform.GetChild(0).GetComponent<Image>();
            if (middle == null || middle.sprite == null) {
                continue;
            }

            if (!colored.Contains(middle.sprite.name)) {
                continue;
            }

            Color color = middle.color;
            middle.color = new Color(1f, 1f, 1f, color.a);
        }
    }

    internal static void RefreshForBody(Body body) {
        if (body == null) {
            return;
        }

        foreach (WoundView view in Resources.FindObjectsOfTypeAll<WoundView>()) {
            if (view == null || view.body != body || !view.gameObject.scene.IsValid()) {
                continue;
            }

            Apply(view);
        }
    }

    private static IReadOnlyDictionary<string, Sprite> SpritesForView(WoundView view) {
        Body body = view != null ? view.body : null;
        // a remote panel with no package stays stock. falling back to LoadedSprites painted the local wound set onto them.
        if (body != null && !MpBody.CanApply(body)) {
            return MpSkinSync.SpritesForBody(body);
        }
        return SpriteReplacerPlugin.LoadedSprites;
    }

    private static HashSet<string> ColoredForView(WoundView view) {
        Body body = view != null ? view.body : null;
        if (body != null && !MpBody.CanApply(body)) {
            return MpSkinSync.ColoredWoundSpritesForBody(body);
        }
        return SpriteReplacerPlugin.ColoredWoundSprites;
    }
}
