using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Together;

// Local mod manager for the other BepInEx plugins in this installation.
// A loaded plugin cannot be unloaded, so a toggle only soft-disables it now
// (plugin component + its Harmony patches) and records the choice. The bundled
// BepInEx patcher applies the recorded choice before the next launch, which is
// the real on/off switch for BepInEx.
internal static class OtherMods {
    internal const string ToggleFileName = "casualtiestogether_mod_toggles.txt";
    internal const string UpdaterGuid = "dannad.krmultiupdater";
    internal const string CoreLibGuid = "net.cucorelib";
    const float RefreshInterval = 0.75f;

    internal sealed class Entry {
        internal string Guid;
        internal string Name;
        internal string Version;
        internal string RelativePath;
        internal bool Enabled;
        internal bool Loaded;
        internal bool FileDisabled;
        internal bool RestartRequired;
        internal PluginInfo Plugin;

        internal string DisplayName {
            get {
                var name = string.IsNullOrWhiteSpace(Name)
                    ? Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(RelativePath) ? "?" : RelativePath)
                    : Name;
                return string.IsNullOrWhiteSpace(Version) ? name : name + " " + Version;
            }
        }
    }

    sealed class StateEntry {
        internal string Guid;
        internal string RelativePath;
        internal string Name;
        internal string Version;
        internal bool Enabled = true;
    }

    static readonly List<StateEntry> states = new();
    static bool statesLoaded;
    static List<Entry> cache;
    static float cacheTime = -100;
    static int cachePluginCount = -1;
    static int cacheDisabledCount = -1;

    static string TogglePath {
        get {
            try {
                var config = Paths.ConfigPath;
                return string.IsNullOrEmpty(config) ? null : Path.Combine(config, ToggleFileName);
            } catch {
                return null;
            }
        }
    }

    // Used by Multiplayer.GetBepinPlugins so a disabled mod drops out of the
    // advertised mod list immediately, even while its assembly is still loaded.
    // The path check covers several plugin classes that share one DLL.
    internal static bool IsDisabled(PluginInfo info) {
        if (info?.Metadata == null) return false;
        if (IsDisabled(info.Metadata.GUID)) return true;
        if (string.IsNullOrEmpty(info.Location)) return false;
        var state = FindStateByPath(RelativeToGameRoot(SafeFullPath(info.Location)));
        return state != null && !state.Enabled;
    }

    static bool IsDisabled(string guid) {
        if (string.IsNullOrEmpty(guid)) return false;
        EnsureStates();
        foreach (var state in states) {
            if (string.Equals(state.Guid, guid, StringComparison.Ordinal)) return !state.Enabled;
        }
        return false;
    }

    internal static void Invalidate() {
        cache = null;
        cacheTime = -100;
    }

    internal static List<Entry> Snapshot() {
        EnsureStates();
        RefreshIfNeeded();
        return cache ?? new List<Entry>();
    }

    internal static void SetEnabled(Entry entry, bool enabled) {
        if (entry == null) return;
        EnsureStates();

        var state = FindState(entry.Guid, entry.RelativePath);
        if (state == null) {
            state = new StateEntry {
                Guid = entry.Guid ?? "",
                RelativePath = entry.RelativePath ?? "",
                Name = entry.Name ?? "",
                Version = entry.Version ?? "",
            };
            states.Add(state);
        }
        state.Enabled = enabled;
        if (!string.IsNullOrEmpty(entry.Guid)) state.Guid = entry.Guid;
        if (!string.IsNullOrEmpty(entry.RelativePath)) state.RelativePath = entry.RelativePath;
        if (!string.IsNullOrWhiteSpace(entry.Name)) state.Name = entry.Name;
        if (!string.IsNullOrWhiteSpace(entry.Version)) state.Version = entry.Version;
        SaveStates();

        var instance = entry.Plugin?.Instance;
        if (instance != null) {
            try {
                instance.enabled = enabled;
            } catch (Exception e) {
                log.warn($"[mods] could not toggle {entry.DisplayName}: {e.Message}");
            }
            if (!enabled) {
                UnpatchAssembly(instance.GetType().Assembly);
                log.l($"[mods] soft-disabled {entry.DisplayName}; full unload takes effect after restart");
            }
        }

        Invalidate();
    }

    static void RefreshIfNeeded() {
        var now = Time.realtimeSinceStartup;
        if (cache != null && now - cacheTime < RefreshInterval) return;

        var disabledFiles = DisabledFiles();
        var pluginCount = Chainloader.PluginInfos.Count;
        if (cache != null && pluginCount == cachePluginCount && disabledFiles.Count == cacheDisabledCount) {
            cacheTime = now;
            return;
        }

        var result = new List<Entry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in Chainloader.PluginInfos.Values.ToList()) {
            if (!IsToggleable(info)) continue;
            var fullPath = SafeFullPath(info.Location);
            var relativePath = RelativeToGameRoot(fullPath);
            // Several [BepInPlugin] classes can share one DLL; the file is the
            // smallest unit BepInEx can actually enable or disable.
            if (!string.IsNullOrEmpty(relativePath) && !seenPaths.Add(relativePath)) continue;
            var state = FindState(info.Metadata.GUID, relativePath);
            var enabled = state == null || state.Enabled;
            result.Add(new Entry {
                Guid = info.Metadata.GUID ?? "",
                Name = info.Metadata.Name ?? "",
                Version = info.Metadata.Version?.ToString() ?? "",
                RelativePath = relativePath,
                Enabled = enabled,
                Loaded = true,
                FileDisabled = false,
                RestartRequired = !enabled,
                Plugin = info,
            });
        }

        foreach (var disabledFile in disabledFiles) {
            var fullPath = disabledFile.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? disabledFile.Substring(0, disabledFile.Length - ".disabled".Length)
                : disabledFile;
            var relativePath = RelativeToGameRoot(fullPath);
            if (!string.IsNullOrEmpty(relativePath) && !seenPaths.Add(relativePath)) continue;
            if (string.IsNullOrEmpty(relativePath)) continue;

            var state = FindStateByPath(relativePath);
            var enabled = state != null && state.Enabled;
            result.Add(new Entry {
                Guid = state?.Guid ?? "",
                Name = state?.Name ?? Path.GetFileNameWithoutExtension(fullPath),
                Version = state?.Version ?? "",
                RelativePath = relativePath,
                Enabled = enabled,
                Loaded = false,
                FileDisabled = true,
                RestartRequired = enabled,
            });
        }

        result.Sort((a, b) => {
            var byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Guid, b.Guid, StringComparison.Ordinal);
        });

        cache = result;
        cacheTime = now;
        cachePluginCount = pluginCount;
        cacheDisabledCount = disabledFiles.Count;
    }

    static bool IsToggleable(PluginInfo info) {
        if (info?.Metadata == null) return false;
        var guid = info.Metadata.GUID;
        if (string.IsNullOrEmpty(guid)) return false;
        if (string.Equals(guid, MyPluginInfo.PLUGIN_GUID, StringComparison.Ordinal)) return false;
        if (string.Equals(guid, UpdaterGuid, StringComparison.Ordinal)) return false;
        // Bundled dependency, not a user-facing mod; disabling it breaks other mods.
        if (string.Equals(guid, CoreLibGuid, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(info.Location) && IsOwnPluginFolder(info.Location)) return false;
        return true;
    }

    static bool IsOwnPluginFolder(string path) {
        try {
            var own = Path.GetFullPath(Path.Combine(Paths.PluginPath, "Together"));
            var full = Path.GetFullPath(path);
            if (string.Equals(full, own, StringComparison.OrdinalIgnoreCase)) return true;
            return full.StartsWith(own.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    static List<string> DisabledFiles() {
        var result = new List<string>();
        try {
            var plugins = Paths.PluginPath;
            if (string.IsNullOrEmpty(plugins) || !Directory.Exists(plugins)) return result;
            foreach (var file in Directory.GetFiles(plugins, "*.dll.disabled", SearchOption.AllDirectories)) {
                if (!IsOwnPluginFolder(file)) result.Add(file);
            }
        } catch (Exception e) {
            log.warn("[mods] disabled-plugin scan failed: " + e.Message);
        }
        return result;
    }

    static void EnsureStates() {
        if (statesLoaded) return;
        var path = TogglePath;
        if (string.IsNullOrEmpty(path)) return;
        statesLoaded = true;
        try {
            if (!File.Exists(path)) return;
            foreach (var raw in File.ReadAllLines(path)) {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var state = new StateEntry {
                    Enabled = parts[0].Trim() != "0",
                    Guid = parts[1].Trim(),
                    RelativePath = parts[2].Trim(),
                    Name = parts.Length > 3 ? parts[3] : "",
                    Version = parts.Length > 4 ? parts[4] : "",
                };
                if (string.IsNullOrEmpty(state.RelativePath)) continue;
                if (FindState(state.Guid, state.RelativePath) != null) continue;
                states.Add(state);
            }
        } catch (Exception e) {
            log.warn("[mods] toggle state load failed: " + e.Message);
        }
    }

    static void SaveStates() {
        var path = TogglePath;
        if (string.IsNullOrEmpty(path)) return;
        try {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var lines = new List<string> {
                "# Casualties: Together - other-mod toggles v1",
                "# enabled\tguid\trelativePath\tname\tversion",
            };
            foreach (var state in states.OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase)) {
                lines.Add((state.Enabled ? "1" : "0") + "\t"
                    + Clean(state.Guid) + "\t"
                    + Clean(state.RelativePath) + "\t"
                    + Clean(state.Name) + "\t"
                    + Clean(state.Version));
            }
            var temp = path + ".tmp";
            File.WriteAllLines(temp, lines);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        } catch (Exception e) {
            log.warn("[mods] toggle state save failed: " + e.Message);
        }
    }

    static string Clean(string value) =>
        (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    static StateEntry FindState(string guid, string relativePath) {
        if (!string.IsNullOrEmpty(guid)) {
            foreach (var state in states) {
                if (string.Equals(state.Guid, guid, StringComparison.Ordinal)) return state;
            }
        }
        return FindStateByPath(relativePath);
    }

    static StateEntry FindStateByPath(string relativePath) {
        if (string.IsNullOrEmpty(relativePath)) return null;
        foreach (var state in states) {
            if (string.Equals(NormalizePath(state.RelativePath), NormalizePath(relativePath), StringComparison.OrdinalIgnoreCase))
                return state;
        }
        return null;
    }

    static string NormalizePath(string path) {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetFullPath(path).Replace('/', '\\'); } catch { return path.Replace('/', '\\'); }
    }

    static string SafeFullPath(string path) {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    static string RelativeToGameRoot(string path) {
        if (string.IsNullOrEmpty(path)) return "";
        try {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Paths.GameRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        } catch {
            return path;
        }
    }

    // Remove only the patches whose patch method lives in this plugin's
    // assembly. Unpatching by Harmony owner could take another mod's patches
    // with it when two plugins share an owner id.
    static void UnpatchAssembly(Assembly assembly) {
        if (assembly == null) return;
        var targets = new List<(MethodBase original, MethodInfo patch)>();
        try {
            foreach (var original in Harmony.GetAllPatchedMethods()) {
                Patches info;
                try { info = Harmony.GetPatchInfo(original); } catch { continue; }
                if (info == null) continue;
                foreach (var patch in AllPatches(info)) {
                    MethodInfo method;
                    try { method = patch?.PatchMethod; } catch { method = null; }
                    if (method?.DeclaringType?.Assembly == assembly) targets.Add((original, method));
                }
            }
        } catch (Exception e) {
            log.warn("[mods] patch scan failed: " + e.Message);
            return;
        }

        var harmony = new Harmony("casualtiestogether.modtoggle");
        foreach (var target in targets) {
            try {
                harmony.Unpatch(target.original, target.patch);
            } catch (Exception e) {
                log.warn("[mods] unpatch failed: " + e.Message);
            }
        }
    }

    static IEnumerable<Patch> AllPatches(Patches info) {
        foreach (var patch in info.Prefixes) yield return patch;
        foreach (var patch in info.Postfixes) yield return patch;
        foreach (var patch in info.Transpilers) yield return patch;
        foreach (var patch in info.Finalizers) yield return patch;
        if (info.ILManipulators != null) {
            foreach (var patch in info.ILManipulators) yield return patch;
        }
    }
}
