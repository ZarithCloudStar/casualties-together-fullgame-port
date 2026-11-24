using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class SkinTailAlignment {
    private static readonly Dictionary<Sprite, Vector2> StockRoots = new();

    internal static bool IsTailName(string name) =>
        name == "experimentTail" || name == "milkytail" || name == "duneTail";

    // All three stock tails extend left from the body. The root is the center
    // of the visible span in the rightmost column with alpha >= 128.
    private static bool TryGetRoot(Color32[] pixels, int width, int height, out Vector2 root) {
        for (int x = width - 1; x >= 0; x--) {
            int bottom = height;
            int top = -1;
            for (int y = 0; y < height; y++) {
                if (pixels[y * width + x].a < 128) continue;
                bottom = Mathf.Min(bottom, y);
                top = y;
            }
            if (top >= 0) {
                root = new Vector2(x + 0.5f, (bottom + top + 1) * 0.5f);
                return true;
            }
        }
        root = default;
        return false;
    }

    internal static bool HasRoot(Texture2D texture) =>
        TryGetRoot(texture.GetPixels32(), texture.width, texture.height, out _);

    internal static Sprite Align(Sprite stock, Sprite replacement, out Texture2D padded) {
        padded = null;
        Texture2D texture = replacement.texture;
        try {
            Color32[] pixels = texture.GetPixels32();
            int width = texture.width;
            int height = texture.height;
            if (!TryGetRoot(pixels, width, height, out Vector2 root))
                throw new InvalidOperationException("No visible tail pixels at alpha >= 128.");
            if (!StockRoots.TryGetValue(stock, out Vector2 stockRoot)) {
                stockRoot = ReadStockRoot(stock);
                StockRoots.Add(stock, stockRoot);
            }

            // Keep the stock root's distance from the rotation center in stock
            // pixels so native animation and mirroring preserve the attachment.
            Vector2 pivot = root + (stock.pivot - stockRoot);
            int left = Mathf.Max(0, Mathf.CeilToInt(-pivot.x));
            int bottom = Mathf.Max(0, Mathf.CeilToInt(-pivot.y));
            int paddedWidth = width + left + Mathf.Max(0, Mathf.CeilToInt(pivot.x - width));
            int paddedHeight = height + bottom + Mathf.Max(0, Mathf.CeilToInt(pivot.y - height));
            if (paddedWidth != width || paddedHeight != height) {
                if (paddedWidth > SystemInfo.maxTextureSize || paddedHeight > SystemInfo.maxTextureSize)
                    throw new InvalidOperationException("Tail canvas exceeds the texture limit after pivot padding.");
                padded = new Texture2D(paddedWidth, paddedHeight, TextureFormat.RGBA32, false) {
                    filterMode = texture.filterMode
                };
                var expanded = new Color32[paddedWidth * paddedHeight];
                for (int y = 0; y < height; y++)
                    Array.Copy(pixels, y * width, expanded, (y + bottom) * paddedWidth + left, width);
                padded.SetPixels32(expanded);
                padded.Apply();
                texture = padded;
                pivot += new Vector2(left, bottom);
            }

            Sprite aligned = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                new Vector2(pivot.x / texture.width, pivot.y / texture.height), stock.pixelsPerUnit,
                0, SpriteMeshType.FullRect);
            if (aligned == null)
                throw new InvalidOperationException("Unity could not create the aligned tail sprite.");
            aligned.name = stock.name;
            if (stock.rect.size != replacement.rect.size || pivot != stock.pivot)
                SkinTrace.Event("tail-alignment", $"{stock.name} png={width}x{height} stock={stock.rect.size} root={root} pivot={pivot}");
            return aligned;
        } catch (Exception ex) {
            if (padded != null) Object.Destroy(padded);
            padded = null;
            SkinTrace.Problem("tail-alignment " + stock.name, $"{texture.width}x{texture.height}: {ex.Message}");
            return stock;
        }
    }

    private static Vector2 ReadStockRoot(Sprite stock) {
        // Read only the sprite rect. The game can mark native textures unreadable.
        Rect rect = stock.packed ? stock.textureRect : stock.rect;
        Texture2D source = stock.texture;
        RenderTexture previous = RenderTexture.active;
        RenderTexture target = RenderTexture.GetTemporary(source.width, source.height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Texture2D copy = null;
        try {
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            copy = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(rect, 0, 0, false);
            copy.Apply(false);
            if (!TryGetRoot(copy.GetPixels32(), copy.width, copy.height, out Vector2 root))
                throw new InvalidOperationException("Stock tail has no visible root.");
            return root + (stock.packed ? stock.textureRectOffset : Vector2.zero);
        } finally {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (copy != null) Object.Destroy(copy);
        }
    }
}
