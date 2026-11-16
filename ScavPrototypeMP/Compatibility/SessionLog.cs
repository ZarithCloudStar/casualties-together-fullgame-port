using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Together;

// No Unity/Steam calls here. One worker owns the stream, recovery tails and failure copies.
internal static class SessionLog {
    const int RingSize = 256, QueueLimit = 4096;
    const int ReportIntervalSeconds = 5, KeepReports = 5;
    const int SessionTailBytes = 2 * 1024 * 1024, BepinexTailBytes = 1024 * 1024, PlayerTailBytes = 2 * 1024 * 1024;
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
    static string directory, primaryDirectory, fallbackDirectory, bepinexPath;
    static bool usingFallback;
    static bool cleanExitRequested, reportReady, reportWarned;
    static int processId;
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
                directory = primaryDirectory = folder;
                fallbackDirectory = fallbackFolder;
                bepinexPath = bepinexLog;
                using (var process = Process.GetCurrentProcess()) processId = process.Id;
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
                cleanExitRequested = true;
                if (accepting) {
                    pending.Enqueue(new Work { Text = Stamp() + " [SESSION] CLEAN EXIT" });
                    accepting = false;
                }
                stopping = true;
            }
            wake.Set();
            // Bounded wait only at shutdown. Normal logging never waits for the worker.
            if (worker != Thread.CurrentThread) worker?.Join(1500);
        } catch { }
        finally { DeleteLiveReports(); }
    }

    static StreamWriter CreateSession() {
        Directory.CreateDirectory(directory);
        DateTime time = DateTime.Now;
        for (int i = 0; i < 120; i++, time = time.AddSeconds(1)) {
            string path = Path.Combine(directory, $"session_{time:yyyyMMdd_HHmmss}_pid{processId}.log");
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
        FileStream stream;
        try {
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        } catch (IOException) { if (File.Exists(path)) return; throw; }
        using (stream)
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) {
            writer.WriteLine("游戏运行时每 5 秒更新 crash-report-live_<pid>.zip。崩溃或强杀后，logs 文件夹会留下 crash-report-live_*.zip，直接把这个 zip 发给作者。");
            writer.WriteLine("正常退出会自动删除本局 live zip；下次启动会将已退出实例留下的报告改名为 crash-report_*.zip，并保留最近 5 份。");
            writer.WriteLine("报告可能缺少最后约 5 秒的日志；不包含大型 dmp，转储位置见 zip 内 README.txt。");
            writer.WriteLine("如果未生成 zip，请按下面方式反馈日志：");
            writer.WriteLine("崩溃、卡死或进不去房时，请把 logs 文件夹里最新的 session_*.log 发给作者。");
            writer.WriteLine("如果有 join-failure_*.log、hang_marker_*.txt，也请一并发送。");
            writer.WriteLine("报告包含运行日志，请发给作者，不要删改报告内容。");
        }
    }

    static string LiveReportPath(string folder) => Path.Combine(folder, $"crash-report-live_{processId}.zip");

    static void DeleteLiveReports() {
        foreach (var folder in new[] { primaryDirectory, fallbackDirectory }) {
            if (string.IsNullOrEmpty(folder)) continue;
            DeleteReportFile(LiveReportPath(folder));
            DeleteReportFile(LiveReportPath(folder) + ".tmp");
        }
    }

    static void DeleteReportFile(string path) { try { File.Delete(path); } catch { } }

    static void ReportError(StreamWriter writer, string phase, Exception error) {
        try { writer.WriteLine(Stamp() + " [crash-report] " + phase + ": " + OneLine(error.ToString())); } catch { }
    }

    static void AddReportTail(ZipArchive zip, string name, string path, int limit, StringBuilder contents) {
        byte[] bytes;
        long length;
        try {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                length = source.Length;
                int size = (int)Math.Min(length, limit), read = 0, count;
                bytes = new byte[size];
                source.Position = length - size;
                // Bound by the captured length even if the source keeps growing.
                while (read < size && (count = source.Read(bytes, read, size - read)) > 0) read += count;
                if (read != size) Array.Resize(ref bytes, read);
            }
        } catch (Exception ex) {
            contents.AppendLine(name + ": unavailable (" + ex.GetType().Name + ")");
            return;
        }
        using (var target = zip.CreateEntry(name, CompressionLevel.Fastest).Open()) target.Write(bytes, 0, bytes.Length);
        contents.AppendLine(name + ": last " + bytes.Length + " bytes of " + length + " bytes (limit=" + limit + ")");
    }

    // Only the log worker compresses/copies files. Publication alone takes the gate,
    // so CleanExit can prevent a late worker from recreating an already deleted report.
    static void UpdateLiveReport(StreamWriter writer, string header) {
        string live = LiveReportPath(directory), temp = live + ".tmp";
        try {
            writer.Flush();
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 16384)) {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
                    var contents = new StringBuilder();
                    AddReportTail(zip, Path.GetFileName(FilePath), FilePath, SessionTailBytes, contents);
                    AddReportTail(zip, "BepInEx/LogOutput.log", bepinexPath, BepinexTailBytes, contents);
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    AddReportTail(zip, "Unity/Player.log", Path.Combine(local + "Low", "Orsoniks", "CasualtiesUnknown", "Player.log"), PlayerTailBytes, contents);
                    using (var readme = new StreamWriter(zip.CreateEntry("README.txt", CompressionLevel.Fastest).Open(), new UTF8Encoding(false))) {
                        readme.WriteLine("这是崩溃或强杀时留下的进程内滚动报告。请直接把这个 zip 发给作者。");
                        readme.WriteLine("正常退出会删除本局 live zip；游戏仍在运行时，该文件也会存在。");
                        readme.WriteLine("每 " + ReportIntervalSeconds + " 秒更新一次，可能缺少最后约 " + ReportIntervalSeconds + " 秒的日志。文件可能从一行中间开始。");
                        readme.WriteLine("PID=" + processId + " version=" + BuildInfo.MOD_VERSION);
                        readme.WriteLine("snapshot=" + Stamp());
                        readme.WriteLine(header);
                        readme.WriteLine("=== zip 内容 / contents ===");
                        readme.Write(contents.ToString());
                        readme.WriteLine("README.txt: report metadata and dump locations");
                        readme.WriteLine("未包含任何 .dmp 或崩溃后生成的文件；需要原生转储时，请查看以下目录（存在才有）：");
                        readme.WriteLine(Path.Combine(local, "Temp", "Orsoniks", "CasualtiesUnknown", "Crashes"));
                        readme.WriteLine(Path.Combine(local, "CrashDumps"));
                    }
                }
                stream.Flush(true);
            }
            lock (gate) {
                if (cleanExitRequested) return;
                // Never delete the previous complete zip before the replacement is ready.
                if (File.Exists(live)) File.Replace(temp, live, null, true);
                else File.Move(temp, live);
            }
            if (!reportReady) {
                writer.WriteLine(Stamp() + " [crash-report] rolling report ready: " + live + " interval=" + ReportIntervalSeconds + "s");
                reportReady = true;
            }
            reportWarned = false;
        } catch (Exception ex) {
            if (!reportWarned) { ReportError(writer, "rolling snapshot failed; keeping previous report", ex); reportWarned = true; }
        } finally { DeleteReportFile(temp); }
    }

    static bool ReportPid(string path, out int pid) {
        const string prefix = "crash-report-live_";
        string name = Path.GetFileName(path);
        pid = 0;
        return name.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(name.Substring(prefix.Length).Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0;
    }

    static bool ReportProcessRunning(int pid) {
        try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return true; } // Permission denied: leave another instance's report alone.
    }

    static string ValidateLiveReport(string path) {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read)) {
            if (zip.GetEntry("README.txt") == null || zip.Entries.Count > 16) throw new InvalidDataException("Incomplete rolling report.");
            byte[] buffer = new byte[8192];
            long total = 0;
            string session = null;
            foreach (var entry in zip.Entries) {
                if (entry.Length > 6 * 1024 * 1024 || (total += entry.Length) > 6 * 1024 * 1024)
                    throw new InvalidDataException("Rolling report exceeds tail limits.");
                using (var input = entry.Open()) {
                    long size = 0; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                        if ((size += count) > entry.Length) throw new InvalidDataException("Invalid report entry length.");
                    }
                    if (size != entry.Length) throw new InvalidDataException("Truncated report entry.");
                }
                if (entry.FullName == entry.Name && entry.Name.StartsWith("session_", StringComparison.Ordinal) && entry.Name.EndsWith(".log", StringComparison.Ordinal)) session = entry.Name;
            }
            return session;
        }
    }

    static void RecoverLiveReports(StreamWriter writer) {
        try {
            foreach (string live in Directory.GetFiles(directory, "crash-report-live_*.zip")) {
                if (!ReportPid(live, out int pid) || pid == processId || ReportProcessRunning(pid)) continue;
                try {
                    string session;
                    try { session = ValidateLiveReport(live); }
                    catch (Exception ex) { DeleteReportFile(live); ReportError(writer, "discarding unreadable/damaged rolling report", ex); continue; }
                    // A normal quit may have failed to delete a zip being copied by the user.
                    // Use its exact session filename, not another session that reused the PID.
                    if (session != null && Tail(Path.Combine(directory, session), 1).EndsWith(" [SESSION] CLEAN EXIT", StringComparison.Ordinal)) {
                        DeleteReportFile(live);
                        continue;
                    }
                    DateTime time = File.GetLastWriteTime(live);
                    string report = Path.Combine(directory, $"crash-report_{time:yyyyMMdd_HHmmss}_pid{pid}.zip");
                    for (int i = 1; File.Exists(report); i++) report = Path.Combine(directory, $"crash-report_{time:yyyyMMdd_HHmmss}_pid{pid}_{i}.zip");
                    File.Move(live, report); // Renaming preserves its last write time.
                    writer.WriteLine(Stamp() + " [crash-report] recovered: " + Path.GetFileName(report));
                } catch (Exception ex) { ReportError(writer, "previous rolling report recovery failed", ex); }
            }
            foreach (string temp in Directory.GetFiles(directory, "crash-report-live_*.zip.tmp"))
                if (ReportPid(temp, out int pid) && pid != processId && !ReportProcessRunning(pid)) DeleteReportFile(temp);
            foreach (var old in Directory.GetFiles(directory, "crash-report_*.zip").Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepReports))
                DeleteReportFile(old.FullName);
        } catch (Exception ex) { ReportError(writer, "report startup cleanup failed", ex); }
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
            RecoverLiveReports(writer);
            PreviousSession(writer);
            writer.WriteLine("=== current session events ===");
            writer.Flush();
            bool hung = false;
            long stalledHeartbeat = 0;
            long nextReport = 0;
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
                if (Stopwatch.GetTimestamp() >= nextReport) {
                    UpdateLiveReport(writer, header);
                    nextReport = Stopwatch.GetTimestamp() + (long)ReportIntervalSeconds * Stopwatch.Frequency;
                }
            }
        }
    }
}
