using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

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
        try { ApplyPendingModSync(); }
        catch (Exception ex) { WriteLog("FATAL mod sync: " + ex); }
    }

    // ModSync writes target.dll.new + target.dll.new.sha256 when the plugin is
    // already loaded. Patchers run before Chainloader, so this is the safe point
    // to verify and replace the target file.
    static void ApplyPendingModSync() {
        var pluginsRoot = Path.Combine(GameRoot, "BepInEx", "plugins");
        if (!Directory.Exists(pluginsRoot)) return;
        int applied = 0;
        foreach (string newPath in Directory.GetFiles(pluginsRoot, "*.dll.new", SearchOption.AllDirectories)) {
            string sidecar = newPath + ".sha256";
            if (!File.Exists(sidecar)) {
                WriteLog("mod sync: missing sha sidecar " + newPath);
                continue;
            }
            string target = newPath.Substring(0, newPath.Length - 4);
            try {
                string expected = File.ReadAllText(sidecar).Trim().ToLowerInvariant();
                if (expected.Length != 64) {
                    WriteLog("mod sync: invalid sha sidecar " + sidecar);
                    continue;
                }
                string actual = BitConverter.ToString(SHA256.Create().ComputeHash(File.ReadAllBytes(newPath)))
                    .Replace("-", "").ToLowerInvariant();
                if (!string.Equals(expected, actual, StringComparison.Ordinal)) {
                    WriteLog("mod sync: sha mismatch, skipped " + newPath);
                    continue;
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(newPath, target);
                File.Delete(sidecar);
                WriteLog("mod sync applied " + target);
                applied++;
            } catch (Exception ex) {
                WriteLog("mod sync failed " + newPath + ": " + ex.Message);
            }
        }
        if (applied > 0) WriteLog("Applied " + applied + " pending mod sync file(s).");
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
        var result = StagedPackageApplier.Apply(
            StageRoot,
            GameRoot,
            new[] { PatcherSelf },
            new[] { "BepInEx/plugins/Together/Steamworks.NET.dll" },
            WriteLog);
        if (!result.Success) {
            WriteLog("Update NOT applied: " + (result.Error ?? "unknown error") + "; stage kept for retry.");
            return;
        }
        WriteLog("Update applied successfully (" + result.RequiredVerified + " required file(s) verified, "
            + result.BestEffortApplied + " optional applied, " + result.BestEffortFailed + " optional skipped).");
    }

    static void WriteLog(string text) {
        try {
            Directory.CreateDirectory(TempRoot);
            File.AppendAllText(Path.Combine(TempRoot, "patcher.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine);
        } catch { }
    }
}
