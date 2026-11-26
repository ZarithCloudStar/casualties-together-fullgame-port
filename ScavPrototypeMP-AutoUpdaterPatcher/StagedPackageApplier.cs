using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Together.AutoUpdate;

public sealed class StagedPackageApplyResult {
    public bool Success;
    public string Error;
    public int RequiredVerified;
    public int BestEffortApplied;
    public int BestEffortFailed;
}

// Applies a staged release package without ever trusting a single file. The
// plugin payload (BepInEx/plugins/**) is all-or-nothing: every file is
// pre-copied and verified before anything is replaced, and any failure rolls
// the committed files back and keeps the stage for the next launch. Everything
// else (BepInEx/core, winhttp, patchers, docs) is best-effort because those
// files may already be mapped/locked by the running process.
//
// Pure BCL on purpose: the patcher build script compiles this file directly
// into TogetherAutoUpdaterPatcher.dll, and the updater tests link it.
public static class StagedPackageApplier {
    const string NewSuffix = ".gcenew";
    const string BackupSuffix = ".gcebak";
    const string RequiredPrefix = "BepInEx/plugins/";

    sealed class Entry {
        internal string Source;
        internal string Relative;
        internal string Target;
        internal bool Required;
        internal bool Satisfied;
        internal bool Committed;
        internal bool HadOriginal;
        internal long ExpectedLength;
        internal string ExpectedHash;
    }

    public static StagedPackageApplyResult Apply(
        string stageRoot,
        string targetRoot,
        IEnumerable<string> skipTargetPaths,
        IEnumerable<string> obsoleteRelativePaths,
        Action<string> log) {
        var result = new StagedPackageApplyResult();
        Action<string> write = log ?? (_ => { });
        try {
            ApplyCore(stageRoot, targetRoot, skipTargetPaths, obsoleteRelativePaths, write, result);
        } catch (Exception ex) {
            result.Success = false;
            result.Error = ex.Message;
            write("apply failed: " + ex);
        }
        return result;
    }

    static void ApplyCore(
        string stageRoot,
        string targetRoot,
        IEnumerable<string> skipTargetPaths,
        IEnumerable<string> obsoleteRelativePaths,
        Action<string> log,
        StagedPackageApplyResult result) {
        if (string.IsNullOrEmpty(stageRoot) || !Directory.Exists(stageRoot)) {
            result.Error = "stage directory not found";
            return;
        }
        if (string.IsNullOrEmpty(targetRoot) || !Directory.Exists(Path.Combine(targetRoot, "BepInEx"))) {
            result.Error = "game BepInEx directory not found";
            return;
        }
        string stagedMain = Path.Combine(stageRoot, "BepInEx", "plugins", "Together", "CasualtiesMP.dll");
        if (!File.Exists(stagedMain)) {
            result.Error = "staged package has no CasualtiesMP.dll";
            return;
        }

        string stageFull = NormalizeRoot(stageRoot);
        string targetFull = NormalizeRoot(targetRoot);
        var skips = new HashSet<string>(
            (skipTargetPaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => Path.GetFullPath(path)),
            StringComparer.OrdinalIgnoreCase);

        var entries = new List<Entry>();
        foreach (string file in Directory.GetFiles(stageFull, "*", SearchOption.AllDirectories)) {
            string relative = file.Substring(stageFull.Length).Replace('\\', '/');
            string target = Path.Combine(targetFull, relative.Replace('/', Path.DirectorySeparatorChar));
            if (skips.Contains(Path.GetFullPath(target))) continue;
            entries.Add(new Entry {
                Source = file,
                Relative = relative,
                Target = target,
                Required = relative.StartsWith(RequiredPrefix, StringComparison.OrdinalIgnoreCase),
            });
        }
        entries.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in entries) {
            try {
                entry.ExpectedLength = new FileInfo(entry.Source).Length;
                entry.ExpectedHash = HashFile(entry.Source);
            } catch (Exception ex) {
                if (entry.Required) {
                    result.Error = "cannot read staged " + entry.Relative + ": " + ex.Message;
                    return;
                }
                entry.ExpectedHash = null;
            }
            DeleteIfFile(entry.Target + NewSuffix);
        }

        foreach (var entry in entries) {
            entry.Satisfied = TargetMatches(entry);
        }

        // Required pre-copy: complete .gcenew copies first, old install intact.
        var copied = new List<string>();
        foreach (var entry in entries.Where(e => e.Required && !e.Satisfied)) {
            string newPath = entry.Target + NewSuffix;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Target));
                File.Copy(entry.Source, newPath, true);
                if (!VerifyFile(newPath, entry)) throw new IOException("copied file did not verify");
            } catch (Exception ex) {
                DeleteIfFile(newPath);
                DeleteCopies(copied);
                result.Error = "required pre-copy failed for " + entry.Relative + ": " + ex.Message;
                log(result.Error);
                return;
            }
            copied.Add(newPath);
        }

        // Required commit: replace only after every required copy verified.
        foreach (var entry in entries.Where(e => e.Required && !e.Satisfied)) {
            try {
                Commit(entry);
            } catch (Exception ex) {
                result.Error = "required commit failed for " + entry.Relative + ": " + ex.Message;
                log(result.Error);
                // File.Replace can fail after creating the backup but before
                // the destination is fully swapped; put the original back.
                try {
                    if (File.Exists(entry.Target + BackupSuffix)) RestoreBackup(entry, log);
                } catch { }
                Rollback(entries, log);
                DeleteCopies(entries.Select(e => e.Target + NewSuffix));
                return;
            }
        }

        foreach (var entry in entries.Where(e => e.Required)) {
            if (!VerifyFile(entry.Target, entry)) {
                result.Error = "required verification failed for " + entry.Relative;
                log(result.Error);
                Rollback(entries, log);
                DeleteCopies(entries.Select(e => e.Target + NewSuffix));
                return;
            }
            result.RequiredVerified++;
        }

        // Best effort: locked core/winhttp/patcher files must not fail the
        // plugin payload, and must not keep the stage alive forever.
        foreach (var entry in entries.Where(e => !e.Required)) {
            if (entry.Satisfied) {
                result.BestEffortApplied++;
                continue;
            }
            string newPath = entry.Target + NewSuffix;
            bool commitAttempted = false;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Target));
                DeleteIfFile(newPath);
                File.Copy(entry.Source, newPath, true);
                if (!VerifyFile(newPath, entry)) throw new IOException("copied file did not verify");
                commitAttempted = true;
                Commit(entry);
                if (!VerifyFile(entry.Target, entry)) throw new IOException("committed file did not verify");
                result.BestEffortApplied++;
            } catch (Exception ex) {
                result.BestEffortFailed++;
                log("optional file skipped " + entry.Relative + ": " + ex.Message);
                DeleteIfFile(newPath);
                // Pre-copy/verify failures must leave the old target alone.
                // Only a commit attempt can have changed it.
                if (commitAttempted) RestoreBackup(entry, log);
            }
        }

        // Obsolete files are removed only after the new payload is verified,
        // and never when the package itself ships that path.
        var stagedPaths = new HashSet<string>(entries.Select(e => e.Relative), StringComparer.OrdinalIgnoreCase);
        foreach (string raw in obsoleteRelativePaths ?? Enumerable.Empty<string>()) {
            string relative = (raw ?? "").Replace('\\', '/');
            if (relative.Length == 0 || stagedPaths.Contains(relative)) continue;
            string target = Path.Combine(targetFull, relative.Replace('/', Path.DirectorySeparatorChar));
            try {
                if (File.Exists(target)) {
                    File.Delete(target);
                    log("removed obsolete " + relative);
                }
            } catch (Exception ex) {
                log("could not remove obsolete " + relative + ": " + ex.Message);
            }
        }

        foreach (var entry in entries) {
            DeleteIfFile(entry.Target + NewSuffix);
            DeleteIfFile(entry.Target + BackupSuffix);
        }
        try {
            Directory.Delete(stageFull, true);
        } catch (Exception ex) {
            log("stage cleanup skipped: " + ex.Message);
        }
        result.Success = true;
        result.Error = null;
    }

    static void Commit(Entry entry) {
        entry.HadOriginal = File.Exists(entry.Target);
        string newPath = entry.Target + NewSuffix;
        if (entry.HadOriginal) {
            File.Replace(newPath, entry.Target, entry.Target + BackupSuffix);
        } else {
            File.Move(newPath, entry.Target);
        }
        entry.Committed = true;
    }

    static void Rollback(IEnumerable<Entry> entries, Action<string> log) {
        var list = entries.ToList();
        for (int i = list.Count - 1; i >= 0; i--) {
            var entry = list[i];
            if (!entry.Committed) continue;
            try {
                RestoreBackup(entry, log);
            } catch (Exception ex) {
                log("rollback failed for " + entry.Relative + ": " + ex.Message);
            }
            entry.Committed = false;
        }
    }

    static void RestoreBackup(Entry entry, Action<string> log) {
        string backup = entry.Target + BackupSuffix;
        if (!File.Exists(backup)) {
            if (!entry.HadOriginal && File.Exists(entry.Target)) File.Delete(entry.Target);
            return;
        }
        if (File.Exists(entry.Target)) {
            File.Replace(backup, entry.Target, null);
        } else {
            File.Move(backup, entry.Target);
        }
    }

    static bool TargetMatches(Entry entry) {
        try {
            if (!File.Exists(entry.Target)) return false;
            if (new FileInfo(entry.Target).Length != entry.ExpectedLength) return false;
            return string.Equals(HashFile(entry.Target), entry.ExpectedHash, StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    static bool VerifyFile(string path, Entry entry) {
        try {
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length != entry.ExpectedLength) return false;
            return string.Equals(HashFile(path), entry.ExpectedHash, StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    static string HashFile(string path) {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path)) {
            byte[] hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    static string NormalizeRoot(string root) {
        string full = Path.GetFullPath(root);
        string separator = Path.DirectorySeparatorChar.ToString();
        return full.EndsWith(separator, StringComparison.Ordinal) ? full : full + separator;
    }

    static void DeleteCopies(IEnumerable<string> paths) {
        foreach (string path in paths) DeleteIfFile(path);
    }

    static void DeleteIfFile(string path) {
        try {
            if (File.Exists(path)) File.Delete(path);
        } catch { }
    }
}
