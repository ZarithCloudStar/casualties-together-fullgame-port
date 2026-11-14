using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Together;

internal static class Program {
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Until(Func<bool> condition, string message, int ms = 3000) {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < ms) Thread.Sleep(20);
        Assert(condition(), message);
    }
    static string Read(string path) => SessionLog.Tail(path, 20000).Replace("\r", "");
    static string Current => SessionLog.FilePath == null ? "" : Read(SessionLog.FilePath);

    static int Main(string[] args) {
        try {
            if (args[0] == "second") {
                SessionLog.Start(args[1], args[2], "second header");
                Until(() => Current.Contains("=== current session events ==="), "second writer startup");
                SessionLog.CleanExit();
                return 0;
            }
            if (args[0] == "unwritable") {
                SessionLog.Start(args[1], args[1], "cannot write");
                Parallel.For(0, 1000, _ => { SessionLog.Heartbeat(); SessionLog.Mark("safe"); });
                SessionLog.JoinFailure("safe", ":invalid/");
                SessionLog.CleanExit();
                return 0;
            }
            string fixture = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(fixture);
            string large = Path.Combine(fixture, "large-tail.log");
            File.WriteAllLines(large, Enumerable.Range(0, 200).Select(n => n + ":" + new string('中', 6000)));
            string largeTail = SessionLog.Tail(large, 150).Replace("\r", "");
            Assert(largeTail.Split('\n').Length == 150 && largeTail.StartsWith("50:") && largeTail.Contains("199:"), "large UTF-8 tail with long lines");
            string logs = Path.Combine(fixture, "logs"), bep = Path.Combine(fixture, "LogOutput.log");
            Directory.CreateDirectory(logs);
            File.WriteAllLines(bep, Enumerable.Range(0, 250).Select(n => "BEP-" + n));
            for (int i = 0; i < 35; i++) {
                string old = Path.Combine(logs, $"session_20000101_0000{i:00}_pid1.log");
                File.WriteAllText(old, "old session");
            }
            string previous = Path.Combine(logs, "session_20000101_000034_pid1.log");
            File.WriteAllLines(previous, Enumerable.Range(0, 300).Select(n => "PREVIOUS-" + n));
            using var sharedBep = new FileStream(bep, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            SessionLog.Start(logs, bep, "UPDATE_VERSION=test\nFULL_VERSION_TAG=unchanged\nplayer=测试玩家");
            Until(() => Current.Contains("PREVIOUS-299"), "unclean recovery missing");
            Assert(Current.Contains("PREVIOUS SESSION DID NOT EXIT CLEANLY: " + Path.GetFileName(previous)), "previous warning");
            Assert(Current.Contains("PREVIOUS-150") && !Current.Contains("PREVIOUS-149"), "previous tail must be last 150 lines");
            Assert(Current.Contains("BEP-50") && !Current.Contains("BEP-49\n"), "BepInEx recovery tail must be last 200");
            Assert(Directory.GetFiles(logs, "session_*.log").Length == 30, "30-session retention");
            Assert(!File.Exists(Path.Combine(logs, "session_20000101_000000_pid1.log")), "oldest filename pruning");

            for (int i = 0; i < 400; i++) SessionLog.Mark("RING-" + i.ToString("D3"));
            var ring = (System.Collections.ICollection)typeof(SessionLog).GetField("steps", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert(ring.Count == 256, "ring must retain exactly 256 entries");
            SessionLog.Event("IMPORTANT", "flush now");
            Until(() => Current.Contains("[IMPORTANT] flush now"), "important event not flushed", 1500);
            SessionLog.JoinFailure("method=direct lobby=0 owner=127.0.0.1 name=\"测试\" port=9050 end=\"rejected\" debug=\"hash\" attempts=2", "127.0.0.1:9050");
            Until(() => Directory.GetFiles(logs, "join-failure_*.log").Length == 1, "failure copy missing");
            string copy = Read(Directory.GetFiles(logs, "join-failure_*.log")[0]);
            Assert(copy.Contains("JOIN FAILED: method=direct"), "structured block");
            string recent = copy.Split("=== last 30 steps ===")[1].Split("=== BepInEx")[0];
            Assert(recent.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 30, "exactly 30 recent steps");
            Assert(!recent.Contains("RING-370") && recent.Contains("RING-371"), "ring order");
            string bepTail = copy.Split("=== BepInEx LogOutput tail (last 100 lines) ===")[1];
            Assert(bepTail.Contains("BEP-150") && !bepTail.Contains("BEP-149\n"), "failure BepInEx tail size");
            Assert(!Path.GetFileName(Directory.GetFiles(logs, "join-failure_*.log")[0]).Contains(':'), "sanitized failure filename");
            SessionLog.JoinFailure("method=invite attempts=3", "127.0.0.1:9050");
            Until(() => Directory.GetFiles(logs, "join-failure_*.log").Length == 2, "copies must never overwrite");

            // A real stopped heartbeat exercises the background monitor without any game process.
            SessionLog.Heartbeat();
            SessionLog.Mark("stall test last step");
            Until(() => Current.Contains("HANG: main thread stalled"), "hang threshold", 7000);
            Assert(Directory.GetFiles(logs, "hang_marker_*.txt").Length == 1, "hang marker");
            string marker = File.ReadAllText(Directory.GetFiles(logs, "hang_marker_*.txt")[0]);
            Assert(marker.Contains("stall test last step") && marker.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1, "one-line marker");
            string last20 = Current.Split("s; last steps:")[1].Trim().TrimEnd().Replace("\r", "");
            Assert(last20.Split('\n').Length == 20, "20 hang context steps");
            SessionLog.Heartbeat();
            Until(() => Current.Contains("RECOVERED: stalled"), "recovery missing");
            Parallel.For(0, 1000, n => SessionLog.Mark("concurrent-" + n));
            SessionLog.Event("CONCURRENT END");
            Until(() => Current.Contains("[CONCURRENT END]"), "concurrent drain");
            SessionLog.CleanExit(); SessionLog.CleanExit();
            SessionLog.Event("AFTER EXIT");
            string firstPath = SessionLog.FilePath, first = File.ReadAllText(firstPath);
            Assert(first.Split("[SESSION] CLEAN EXIT").Length == 2 && first.TrimEnd().EndsWith("[SESSION] CLEAN EXIT"), "idempotent final clean exit");
            Assert(!first.Contains("[AFTER EXIT]"), "closed session must stop accepting entries");
            Assert(first.Split("HANG: main thread stalled").Length == 2, "one hang report per stall");
            Assert(first.Contains("player=测试玩家"), "UTF-8 content");

            Child("second", logs, bep);
            string second = Directory.GetFiles(logs, "session_*.log").Except(new[] { firstPath }).OrderByDescending(Path.GetFileName).First();
            Assert(!Read(second).Contains("PREVIOUS SESSION DID NOT EXIT CLEANLY"), "clean predecessor must not trigger recovery");
            Assert(File.ReadAllText(firstPath) == first, "new session must not overwrite old file");
            Assert(Directory.GetFiles(logs, "session_*.log").Length == 30, "retention after second process");
            string blocked = Path.Combine(fixture, "file-not-directory");
            File.WriteAllText(blocked, "obstacle");
            Child("unwritable", blocked, bep);
            Console.WriteLine("PASS: recovery tails, retention, UTF-8, event flush, ring ordering, failure copies, hang/recovery, concurrent append, clean exit, immutable sessions, I/O failure isolation");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void Child(string mode, string folder, string bep) {
        var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add(mode); info.ArgumentList.Add(folder); info.ArgumentList.Add(bep);
        using var process = Process.Start(info);
        Assert(process.WaitForExit(10000) && process.ExitCode == 0, "child test " + mode);
    }
}
