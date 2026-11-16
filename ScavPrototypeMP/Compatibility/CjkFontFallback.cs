#if FULLGAME
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Together;

// CJK names are allowed now (we no longer replace missing glyphs with '?'), so the
// pixel font used by nametags/chat may have no glyphs for Chinese. Attach a runtime
// dynamic TMP font built from an installed Windows CJK font as a fallback. No font
// file has to be shipped with the mod.
internal static class CjkFontFallback {
    static bool applied;
    static TMP_FontAsset cached;

    internal static void AttachTo(TMP_FontAsset font) {
        Ensure();
        Attach(font, cached);
    }
    static int attempts;

    internal static void Ensure() {
        if (applied || attempts >= 8) return;
        attempts++;
        try {
            var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            TMP_FontAsset cjk = null;
            if (attempts <= 2) log.l($"CJK: attempt {attempts}, loaded TMP fonts={all.Length}");

            // 1) The game ships CJK glyphs for its Chinese localization. Prefer reusing
            // one of its own font assets so the look matches and no font file is needed.
            foreach (var font in all) {
                if (font == null) continue;
                try {
                    if (font.HasCharacter('星') && font.HasCharacter('云') && font.HasCharacter('吖')) {
                        cjk = font;
                        break;
                    }
                } catch { }
            }

            // 2) Fallback: build a dynamic TMP asset from a system CJK font.
            if (cjk == null) {
                Font os = null;
                string[] candidates = {
                    "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun",
                    "Noto Sans CJK SC", "Source Han Sans SC", "Arial Unicode MS",
                };
                foreach (var name in candidates) {
                    try { os = Font.CreateDynamicFontFromOSFont(name, 36); } catch { os = null; }
                    if (os != null) break;
                }
                if (os != null) {
                    try {
                        // Dynamic OS fonts have no atlas until characters are requested;
                        // TMP needs a usable font object to build its dynamic asset.
                        os.RequestCharactersInTexture("星云吖中文名测试Aa", 36, FontStyle.Normal);
                        cjk = TMP_FontAsset.CreateFontAsset(os, 90, 9,
                            UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                            1024, 1024,
                            TMPro.AtlasPopulationMode.Dynamic, true);
                    } catch (Exception e) {
                        if (attempts >= 8) log.warn("CJK: CreateFontAsset failed: " + e.Message);
                        cjk = null;
                    }
                    if (cjk != null) cjk.name = "TogetherCjkFallback";
                    else if (attempts >= 8) log.warn($"CJK: CreateFontAsset returned null for OS font '{os.name}'.");
                } else {
                    if (attempts >= 8) log.warn("CJK: no OS CJK font could be created.");
                }
            }

            if (cjk == null) {
                if (attempts >= 8) log.warn("CJK: no CJK-capable TMP font found; non-latin names may show boxes in nametags.");
                return;
            }
            cached = cjk;
            applied = true;

            int attached = 0;
            foreach (var font in all) {
                if (Attach(font, cjk)) attached++;
            }
            if (Attach(TMP_Settings.defaultFontAsset, cjk)) attached++;
            log.l($"CJK: fallback '{cjk.name}' attached to {attached} font asset(s).");
        } catch (Exception e) {
            log.warn("CJK: runtime fallback setup failed: " + e.Message);
        }
    }

    static bool Attach(TMP_FontAsset font, TMP_FontAsset cjk) {
        if (font == null || cjk == null || font == cjk) return false;
        if (font.fallbackFontAssetTable == null) font.fallbackFontAssetTable = new List<TMP_FontAsset>();
        if (font.fallbackFontAssetTable.Contains(cjk)) return false;
        font.fallbackFontAssetTable.Add(cjk);
        return true;
    }
}
#endif
