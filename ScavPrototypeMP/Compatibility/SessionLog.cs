using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Together;

// No Unity/Steam calls here. One worker owns the stream, recovery tails and failure copies.
internal static class SessionLog {
    const int RingSize = 256, QueueLimit = 4096;
    // 日志目录只保留最近 10 个日志文件（session / join-failure / hang_marker），
    // 旧的自动删除；如何反馈日志.txt 不参与清理。
    const int KeepLogFiles = 10;
    static readonly string[] LogFilePatterns = { "session_*.log", "join-failure_*.log", "hang_marker_*.txt" };
    static readonly object gate = new object();
    static readonly Queue<string> steps = new Queue<string>(RingSize);
    static readonly Queue<Work> pending = new Queue<Work>();
    static readonly AutoResetEvent wake = new AutoResetEvent(false);
    static Thread worker;
    static Work[] retryBatch;
    static bool accepting, stopping;
    static long heartbeat;
    static string directory, fallbackDirectory, bepinexPath;
    static bool usingFallback;
    static int dropped;
    internal static string FilePath { get; private set; }

    sealed class Work {
        internal string Text, CopyTarget;
        internal bool BepinexTail;
    }

    internal static void Start(string folder, string bepinexLog, string header, string fallbackFolder = null) {
        try {
            lock (gate) {
                if (worker != null) return;
                directory = folder;
                fallbackDirectory = fallbackFolder;
                bepinexPath = bepinexLog;
                accepting = true;
                heartbeat = Stopwatch.GetTimestamp();
                worker = new Thread(() => Run(header)) { IsBackground = true, Name = "TogetherSessionLog" };
                worker.Start();
            }
            Mark("plugin startup");
        } catch { }
    }

    // Exactly one timestamp exchange per frame; no allocation, disk I/O or Unity access.
    internal static void Heartbeat() {
        try { Interlocked.Exchange(ref heartbeat, Stopwatch.GetTimestamp()); } catch { }
    }

    static string Stamp() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
    internal static string OneLine(string text) => (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
    internal static void Mark(string step) => Record("STEP", step, false);
    internal static void Event(string name, string details = "") => Record(name, details, true);

    static void Record(string name, string details, bool urgent) {
        try {
            lock (gate) {
                if (!accepting) return;
                string line = Stamp() + " [" + OneLine(name) + "] " + OneLine(details);
                if (steps.Count == RingSize) steps.Dequeue();
                steps.Enqueue(line);
                if (pending.Count < QueueLimit) pending.Enqueue(new Work { Text = line });
                else dropped++;
            }
            if (urgent) wake.Set();
        } catch { }
    }

    static string RecentLocked(int count) => string.Join(Environment.NewLine, steps.Skip(Math.Max(0, steps.Count - count)));
    static string Recent(int count) { lock (gate) return RecentLocked(count); }

    // Called with already captured metadata; the main thread never reads LogOutput.log.
    internal static void JoinFailure(string details, string target) {
        try {
            lock (gate) {
                if (!accepting) return;
                string line = Stamp() + " JOIN FAILED: " + OneLine(details);
                pending.Enqueue(new Work {
                    Text = line + Environment.NewLine + "=== last 30 steps ===" + Environment.NewLine + RecentLocked(30),
                    BepinexTail = true, CopyTarget = SafeName(target)
                });
                if (steps.Count == RingSize) steps.Dequeue();
                steps.Enqueue(line);
            }
            wake.Set();
        } catch { }
    }

    static string SafeName(string text) {
        var invalid = Path.GetInvalidFileNameChars();
        string result = new string(OneLine(text).Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result.Substring(0, Math.Min(100, result.Length));
    }

    internal static void CleanExit() {
        try {
            lock (gate) {
                if (!accepting) return;
                pending.Enqueue(new Work { Text = Stamp() + " [SESSION] CLEAN EXIT" });
                accepting = false;
                stopping = true;
            }
            wake.Set();
            // Bounded wait only at shutdown. Normal logging never waits for the worker.
            if (worker != Thread.CurrentThread) worker?.Join(1500);
        } catch { }
    }

    static StreamWriter CreateSession() {
        Directory.CreateDirectory(directory);
        DateTime time = DateTime.Now;
        int pid;
        using (var process = Process.GetCurrentProcess()) pid = process.Id;
        for (int i = 0; i < 120; i++, time = time.AddSeconds(1)) {
            string path = Path.Combine(directory, $"session_{time:yyyyMMdd_HHmmss}_pid{pid}.log");
            try {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384);
                FilePath = path;
                return new StreamWriter(stream, new UTF8Encoding(false));
            } catch (IOException) { if (!File.Exists(path)) throw; }
        }
        throw new IOException("No unique session filename available.");
    }

    static void CreateLogInstructions() {
        string path = Path.Combine(directory, "如何反馈日志.txt");
        if (File.Exists(path)) return;
        FileStream stream;
        try {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        } catch (IOException) { if (File.Exists(path)) return; throw; }
        using (stream)
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) {
            writer.WriteLine("崩溃、卡死或进不去房时，请把 logs 文件夹里最新的 session_*.log 发给作者。");
            writer.WriteLine("如果有 join-failure_*.log、hang_marker_*.txt，也请一并发送。");
            writer.WriteLine("这些文件不含账号密码，只有构建版本和操作记录。请不要删改文件内容。");
        }
    }

    static bool CanFallback => !usingFallback && !string.IsNullOrEmpty(fallbackDirectory);

    // Scan backwards in small blocks, including files held open by BepInEx.
    // Decode only the requested lines, even when the source file is very large.
    internal static string Tail(string path, int count) {
        try {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (count <= 0) return "";
                long end = stream.Length, position = end, start = 0;
                byte[] buffer = new byte[8192];
                int lines = 0;
                bool found = false;
                while (position > 0 && !found) {
                    long block = Math.Max(0, position - buffer.Length);
                    stream.Position = block;
                    int size = (int)(position - block), read = 0, n;
                    while (read < size && (n = stream.Read(buffer, read, size - read)) > 0) read += n;
                    for (int i = read - 1; i >= 0; i--) {
                        if (buffer[i] != (byte)'\n' || block + i == end - 1) continue;
                        if (++lines == count) { start = block + i + 1; found = true; break; }
                    }
                    position = block;
                }
                stream.Position = start;
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 8192))
                    return reader.ReadToEnd().TrimEnd('\r', '\n');
            }
        } catch (Exception ex) { return "(tail unavailable: " + ex.GetType().Name + ")"; }
    }

    static void PreviousSession(StreamWriter writer) {
        try {
            var files = Directory.GetFiles(directory, "session_*.log").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
            string previous = files.FirstOrDefault(p => p != FilePath);
            if (previous != null) {
                string tail = Tail(previous, 150);
                // Imported tails can contain CLEAN EXIT; only the final native marker counts.
                bool clean = tail.TrimEnd().EndsWith(" [SESSION] CLEAN EXIT", StringComparison.Ordinal);
                if (!clean) {
                    writer.WriteLine("PREVIOUS SESSION DID NOT EXIT CLEANLY: " + Path.GetFileName(previous));
                    writer.WriteLine("=== previous session tail ===");
                    writer.WriteLine(tail);
                    writer.WriteLine("=== BepInEx LogOutput tail ===");
                    writer.WriteLine(Tail(bepinexPath, 200));
                    writer.WriteLine("=== end previous session recovery ===");
                }
            }
            PruneOldFiles();
        } catch { }
    }

    // 只保留最近 KeepLogFiles 个日志文件；任何异常都不能影响游戏。
    static void PruneOldFiles() {
        try {
            var all = new List<FileInfo>();
            foreach (var pattern in LogFilePatterns)
                foreach (var path in Directory.GetFiles(directory, pattern)) all.Add(new FileInfo(path));
            var ordered = all.OrderByDescending(x => x.LastWriteTimeUtc)
                             .ThenByDescending(x => x.Name, StringComparer.Ordinal)
                             .ToArray();
            for (int i = KeepLogFiles; i < ordered.Length; i++) {
                if (string.Equals(ordered[i].FullName, FilePath, StringComparison.OrdinalIgnoreCase)) continue;
                try { ordered[i].Delete(); } catch { }
            }
        } catch { }
    }

    static void CopyFailure(StreamWriter writer, string target) {
        writer.Flush();
        DateTime time = DateTime.Now;
        for (int i = 0; i < 120; i++, time = time.AddSeconds(1)) {
            string copy = Path.Combine(directory, $"join-failure_{time:yyyyMMdd_HHmmss}_{target}.log");
            try { File.Copy(FilePath, copy, false); PruneOldFiles(); return; }
            catch (IOException) { if (!File.Exists(copy)) throw; }
        }
    }

    static void Run(string header) {
        try { RunSession(header); }
        catch {
            if (!CanFallback) return;
            directory = fallbackDirectory;
            usingFallback = true;
            try { RunSession(header); }
            catch { /* Neither location is writable; logging must never affect the game. */ }
        }
    }

    static void RunSession(string header) {
        using (var writer = CreateSession()) {
            writer.WriteLine("=== Casualties Together session ===");
            writer.WriteLine(header);
            writer.WriteLine("logdir=" + OneLine(directory) + " (fallback=" + (usingFallback ? "yes" : "no") + ")");
            writer.Flush();
            CreateLogInstructions();
            PreviousSession(writer);
            writer.WriteLine("=== current session events ===");
            writer.Flush();
            bool hung = false;
            long stalledHeartbeat = 0;
            while (true) {
                wake.WaitOne(500);
                Work[] batch; int lost; bool stop;
                lock (gate) {
                    batch = retryBatch == null ? pending.ToArray() : retryBatch.Concat(pending).ToArray();
                    pending.Clear();
                    lost = dropped; dropped = 0; stop = stopping;
                }
                // Keep in-flight events/failure copies if a write forces the directory fallback.
                retryBatch = batch;
                if (lost != 0) writer.WriteLine(Stamp() + " LOG QUEUE OVERFLOW: dropped=" + lost);
                foreach (var work in batch) {
                    try {
                        writer.WriteLine(work.Text);
                        if (work.BepinexTail) {
                            writer.WriteLine("=== BepInEx LogOutput tail (last 100 lines) ===");
                            writer.WriteLine(Tail(bepinexPath, 100));
                            writer.WriteLine("=== end join failure ===");
                        }
                        if (work.CopyTarget != null) CopyFailure(writer, work.CopyTarget);
                    } catch { if (CanFallback) throw; }
                }
                if (!stop) {
                    long now = Stopwatch.GetTimestamp(), beat = Interlocked.Read(ref heartbeat);
                    double seconds = (now - beat) / (double)Stopwatch.Frequency;
                    if (hung && beat != stalledHeartbeat) {
                        double duration = (beat - stalledHeartbeat) / (double)Stopwatch.Frequency;
                        writer.WriteLine(Stamp() + " RECOVERED: stalled " + duration.ToString("F1", CultureInfo.InvariantCulture) + "s");
                        hung = false;
                    }
                    if (seconds > 5 && !hung) {
                        hung = true; stalledHeartbeat = beat;
                        string last = Recent(20);
                        writer.WriteLine(Stamp() + " HANG: main thread stalled " + seconds.ToString("F1", CultureInfo.InvariantCulture) + "s; last steps:");
                        writer.WriteLine(last);
                        try {
                            File.WriteAllText(Path.Combine(directory, "hang_marker_" + Path.GetFileName(FilePath) + ".txt"),
                                Stamp() + "/" + seconds.ToString("F1", CultureInfo.InvariantCulture) + "s/" + OneLine(last.Split('\n').LastOrDefault()) + Environment.NewLine);
                        } catch { if (CanFallback) throw; }
                    }
                }
                writer.Flush();
                retryBatch = null;
                if (stop) break;
            }
        }
    }
}
