using BepInEx.Logging;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Together.AutoUpdate;

// BepInEx patcher: runs before Assembly-CSharp is loaded. The updater plugin
// only downloads and stages a complete package into %TEMP%; this patcher
// applies it on the next launch, before the old plugin DLL is locked.
public static class TogetherAutoUpdaterPatcher {
    static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "TogetherAutoUpdater");
    static readonly string StageRoot = Path.Combine(TempRoot, "MULTIPLAYER_STAGED");
    static readonly string GameRoot = Environment.CurrentDirectory;
    static readonly string PatcherSelf = Path.Combine(GameRoot, "BepInEx", "patchers", "TogetherAutoUpdaterPatcher.dll");

    public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

    public static void Patch(AssemblyDefinition assembly) {
        try { Apply(); }
        catch (Exception ex) { WriteLog("FATAL " + ex); }
        try { ApplyModToggles(); }
        catch (Exception ex) { WriteLog("FATAL mod toggles: " + ex); }
    }

    // The plugin writes BepInEx/config/casualtiestogether_mod_toggles.txt when
    // the user toggles another mod. Patchers run before Chainloader scans
    // BepInEx/plugins, which is the only safe point to rename plugin DLLs.
    static void ApplyModToggles() {
        string configPath = Path.Combine(GameRoot, "BepInEx", "config", "casualtiestogether_mod_toggles.txt");
        if (!File.Exists(configPath)) return;

        string pluginsRoot = Path.GetFullPath(Path.Combine(GameRoot, "BepInEx", "plugins"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int applied = 0;
        foreach (string raw in File.ReadAllLines(configPath)) {
            string line = raw == null ? "" : raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 3) continue;

            bool enabled = parts[0].Trim() != "0";
            string relative = parts[2].Trim();
            if (relative.Length == 0) continue;

            string full;
            try {
                full = Path.IsPathRooted(relative)
                    ? Path.GetFullPath(relative)
                    : Path.GetFullPath(Path.Combine(GameRoot, relative));
            } catch (Exception ex) { WriteLog("toggle path invalid " + relative + ": " + ex.Message); continue; }

            if (!full.StartsWith(pluginsRoot, StringComparison.OrdinalIgnoreCase)
                || !full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
                WriteLog("toggle path outside plugins, skipped: " + full);
                continue;
            }

            string disabled = full + ".disabled";
            try {
                if (enabled) {
                    if (!File.Exists(disabled)) continue;
                    if (File.Exists(full)) {
                        File.Delete(disabled);
                        WriteLog("re-enabled " + full + " (stale .disabled removed)");
                    } else {
                        File.Move(disabled, full);
                        WriteLog("re-enabled " + full);
                    }
                } else {
                    if (!File.Exists(full)) continue;
                    if (File.Exists(disabled)) {
                        File.Copy(full, disabled, true);
                        File.Delete(full);
                    } else {
                        File.Move(full, disabled);
                    }
                    WriteLog("disabled " + full);
                }
                applied++;
            } catch (Exception ex) {
                WriteLog("toggle failed " + full + ": " + ex.Message);
            }
        }
        if (applied > 0) WriteLog("Applied " + applied + " other-mod toggle(s).");
    }

    static void Apply() {
        if (!Directory.Exists(StageRoot)) return;
        if (!Directory.Exists(Path.Combine(GameRoot, "BepInEx"))) {
            WriteLog("Game root not found: " + GameRoot);
            return;
        }
        if (!File.Exists(Path.Combine(StageRoot, "BepInEx", "plugins", "Together", "CasualtiesMP.dll"))) {
            WriteLog("Staged package has no Together plugin; ignored.");
            return;
        }

        WriteLog("Applying staged update from " + StageRoot);
        // These assemblies are loaded by BepInEx after patchers, so they can
        // still be replaced here. The patcher itself is already loaded and is
        // deliberately skipped by CopyTree.
        DeleteIfExists(Path.Combine(GameRoot, "BepInEx", "plugins", "Together", "CasualtiesMP.dll"));
        DeleteIfExists(Path.Combine(GameRoot, "BepInEx", "plugins", "Together", "Steamworks.NET.dll"));

        CopyTree(StageRoot, GameRoot);
        if (!File.Exists(Path.Combine(GameRoot, "BepInEx", "plugins", "Together", "CasualtiesMP.dll"))) {
            WriteLog("Verification failed: new CasualtiesMP.dll is missing.");
            return;
        }
        WriteLog("Update applied successfully.");
        try { Directory.Delete(TempRoot, true); }
        catch (Exception ex) { WriteLog("Cleanup skipped: " + ex.Message); }
    }

    static void CopyTree(string source, string destination) {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source)) {
            string target = Path.Combine(destination, Path.GetFileName(file));
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(PatcherSelf), StringComparison.OrdinalIgnoreCase))
                continue;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            } catch (Exception ex) { WriteLog("copy failed " + target + ": " + ex.Message); }
        }
        foreach (string dir in Directory.GetDirectories(source)) {
            try { CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir))); }
            catch (Exception ex) { WriteLog("dir failed " + dir + ": " + ex.Message); }
        }
    }

    static void DeleteIfExists(string path) {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { WriteLog("delete failed " + path + ": " + ex.Message); }
    }

    static void WriteLog(string text) {
        try {
            Directory.CreateDirectory(TempRoot);
            File.AppendAllText(Path.Combine(TempRoot, "patcher.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine);
        } catch { }
    }
}
