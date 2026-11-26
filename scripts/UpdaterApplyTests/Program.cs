using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Together.AutoUpdate;

static class Program {
    const string MainDll = "BepInEx/plugins/Together/CasualtiesMP.dll";
    const string DepDll = "BepInEx/plugins/Together/nested/dep.dll";
    const string Obsolete = "BepInEx/plugins/Together/Steamworks.NET.dll";

    static int failures;
    static string baseRoot;

    static int Main(string[] args) {
        baseRoot = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "UpdaterApplyTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseRoot);

        RunSuccess();
        RunRequiredPreCopyFailure();
        RunRequiredCommitRollback();
        RunBestEffortFailure();
        RunBestEffortPreCopyFailureKeepsOldFile();
        RunUnchangedSkip();

        if (failures == 0) {
            try { Directory.Delete(baseRoot, true); } catch { }
            Console.WriteLine("ALL PASS");
            return 0;
        }
        Console.WriteLine("FAILURES: " + failures + " (temp kept at " + baseRoot + ")");
        return 1;
    }

    static void RunSuccess() {
        var (stage, game) = NewCase("success");
        Write(stage, MainDll, "new-main");
        Write(stage, DepDll, "new-dep");
        Write(stage, "BepInEx/plugins/Together/VERSION.txt", "version 2");
        Write(stage, "BepInEx/patchers/TogetherAutoUpdaterPatcher.dll", "new-patcher");
        Write(stage, "BepInEx/core/BepInEx.dll", "new-core");
        Write(stage, "winhttp.dll", "new-winhttp");

        Write(game, MainDll, "old-main");
        Write(game, DepDll, "old-dep");
        Write(game, Obsolete, "obsolete");
        Write(game, "BepInEx/patchers/TogetherAutoUpdaterPatcher.dll", "old-patcher");
        Write(game, "BepInEx/core/BepInEx.dll", "old-core");
        Write(game, "winhttp.dll", "old-winhttp");

        string patcherSelf = Target(game, "BepInEx/patchers/TogetherAutoUpdaterPatcher.dll");
        var logs = new List<string>();
        var result = Apply(stage, game, new[] { patcherSelf }, new[] { Obsolete }, logs.Add);

        Check(result.Success, "success result");
        Check(Read(game, MainDll) == "new-main", "main replaced");
        Check(Read(game, DepDll) == "new-dep", "nested dep replaced");
        Check(Read(game, "BepInEx/plugins/Together/VERSION.txt") == "version 2", "version replaced");
        Check(!File.Exists(Target(game, Obsolete)), "obsolete removed");
        Check(Read(game, "BepInEx/patchers/TogetherAutoUpdaterPatcher.dll") == "old-patcher", "patcher self skipped");
        Check(Read(game, "BepInEx/core/BepInEx.dll") == "new-core", "core best-effort replaced");
        Check(Read(game, "winhttp.dll") == "new-winhttp", "winhttp best-effort replaced");
        Check(!Directory.Exists(stage), "stage removed");
        Check(!HasTempFiles(game), "no temp files");
    }

    static void RunRequiredPreCopyFailure() {
        var (stage, game) = NewCase("precopy-fail");
        Write(stage, MainDll, "new-main");
        Write(stage, DepDll, "new-dep");
        Write(game, MainDll, "old-main");
        Write(game, DepDll, "old-dep");
        string blocker = Target(game, DepDll) + ".gcenew";
        Directory.CreateDirectory(blocker);

        var result = Apply(stage, game);

        Check(!result.Success, "pre-copy failure result");
        Check(Read(game, MainDll) == "old-main", "main untouched after pre-copy failure");
        Check(Read(game, DepDll) == "old-dep", "dep untouched after pre-copy failure");
        Check(Directory.Exists(stage), "stage kept after pre-copy failure");
        Check(!File.Exists(Target(game, MainDll) + ".gcenew"), "earlier pre-copy cleaned");
    }

    static void RunRequiredCommitRollback() {
        var (stage, game) = NewCase("commit-rollback");
        Write(stage, MainDll, "new-main");
        Write(stage, DepDll, "new-dep");
        Write(game, MainDll, "old-main");
        string depTarget = Target(game, DepDll);
        Directory.CreateDirectory(depTarget);

        var result = Apply(stage, game);

        Check(!result.Success, "commit failure result");
        Check(Read(game, MainDll) == "old-main", "main rolled back");
        Check(Directory.Exists(depTarget), "dep blocker intact");
        Check(Directory.Exists(stage), "stage kept after commit failure");
        Check(!HasTempFiles(game), "no temp files after rollback");
    }

    static void RunBestEffortFailure() {
        var (stage, game) = NewCase("best-effort-fail");
        Write(stage, MainDll, "new-main");
        Write(stage, "winhttp.dll", "new-winhttp");
        Write(game, MainDll, "old-main");
        string winhttp = Target(game, "winhttp.dll");
        Directory.CreateDirectory(winhttp);

        var logs = new List<string>();
        var result = Apply(stage, game, null, null, logs.Add);

        Check(result.Success, "best-effort failure still succeeds");
        Check(Read(game, MainDll) == "new-main", "required applied despite optional failure");
        Check(Directory.Exists(winhttp), "winhttp blocker intact");
        Check(result.BestEffortFailed == 1, "best-effort failure counted");
        Check(logs.Any(line => line.Contains("optional file skipped") && line.Contains("winhttp.dll")), "warning logged");
        Check(!Directory.Exists(stage), "stage removed after success");
    }

    static void RunBestEffortPreCopyFailureKeepsOldFile() {
        var (stage, game) = NewCase("best-effort-precopy-fail");
        Write(stage, MainDll, "new-main");
        Write(stage, "winhttp.dll", "new-winhttp");
        Write(game, MainDll, "old-main");
        Write(game, "winhttp.dll", "old-winhttp");
        Directory.CreateDirectory(Target(game, "winhttp.dll") + ".gcenew");

        var logs = new List<string>();
        var result = Apply(stage, game, null, null, logs.Add);

        Check(result.Success, "best-effort pre-copy failure still succeeds");
        Check(Read(game, MainDll) == "new-main", "required applied despite best-effort pre-copy failure");
        Check(File.Exists(Target(game, "winhttp.dll")), "old best-effort file kept");
        Check(Read(game, "winhttp.dll") == "old-winhttp", "old best-effort content kept");
        Check(result.BestEffortFailed == 1, "best-effort pre-copy failure counted");
        Check(!Directory.Exists(stage), "stage removed after success");
    }

    static void RunUnchangedSkip() {
        var (stage, game) = NewCase("unchanged");
        Write(stage, MainDll, "same");
        Write(stage, "winhttp.dll", "same");
        Write(game, MainDll, "same");
        Write(game, "winhttp.dll", "same");
        string blocker = Target(game, "winhttp.dll") + ".gcenew";
        Directory.CreateDirectory(blocker);

        var result = Apply(stage, game);

        Check(result.Success, "unchanged success");
        Check(Directory.Exists(blocker), "unchanged file did not touch .gcenew");
        Check(!HasTempFiles(game), "no temp files created for unchanged files");
        Check(!Directory.Exists(stage), "stage removed after unchanged success");
    }

    static (string stage, string game) NewCase(string name) {
        string root = Path.Combine(baseRoot, name);
        string stage = Path.Combine(root, "stage");
        string game = Path.Combine(root, "game");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(game);
        return (stage, game);
    }

    static StagedPackageApplyResult Apply(
        string stage,
        string game,
        IEnumerable<string> skip = null,
        IEnumerable<string> obsolete = null,
        Action<string> log = null) {
        return StagedPackageApplier.Apply(
            stage,
            game,
            skip ?? Array.Empty<string>(),
            obsolete ?? Array.Empty<string>(),
            log ?? (_ => { }));
    }

    static void Write(string root, string relative, string text) {
        string path = Target(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
    }

    static string Read(string root, string relative) => File.ReadAllText(Target(root, relative));

    static string Target(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    static bool HasTempFiles(string root) {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories).Any(path =>
            path.EndsWith(".gcenew", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gcebak", StringComparison.OrdinalIgnoreCase));
    }

    static void Check(bool condition, string name) {
        if (condition) {
            Console.WriteLine("PASS " + name);
            return;
        }
        failures++;
        Console.WriteLine("FAIL " + name);
    }
}
