#if FULLGAME
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;

namespace Together;

// No Unity/network dependencies. Published generations are immutable; current.json
// is the single atomic commit point (data and metadata switch together).
internal sealed class SaveSlotStore {
    internal const int Count = 5;
    internal sealed class Meta {
        public string name, hostName, modVersion, buildTag;
        public long savedAtUnix;
        public int layer, playerCount, schema = 1;
    }
    internal sealed class Head {
        public string generation;
        public Meta meta;
    }
    readonly string root;
    static readonly ConcurrentDictionary<string, object> gates = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate;
    internal SaveSlotStore(string root) { this.root = Path.GetFullPath(root); gate = gates.GetOrAdd(this.root, _ => new object()); }
    internal string Working => Path.Combine(root, "_session");
    string Slot(int slot) {
        if (slot < 1 || slot > Count) throw new ArgumentOutOfRangeException(nameof(slot));
        return Path.Combine(root, "slots", "slot" + slot);
    }
    internal static string CleanName(string input) {
        var text = new StringBuilder();
        foreach (char c in (input ?? "").Normalize()) {
            if (char.IsControl(c) || char.IsSurrogate(c) || "<>:\"/\\|?*".IndexOf(c) >= 0 || Path.GetInvalidFileNameChars().Contains(c)) continue;
            text.Append(c);
        }
        string name = text.ToString().Replace("..", "").Trim().Trim('.').Trim();
        if (name.Length > 24) name = name.Substring(0, 24).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("存档名称不能为空，请输入名称（最多 24 字）。");
        return name;
    }
    internal Head Read(int slot) {
        lock (gate) return ReadCore(slot);
    }
    Head ReadCore(int slot) {
        string file = Path.Combine(Slot(slot), "current.json");
        if (!File.Exists(file)) return null;
        Head head;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream)) head = JsonConvert.DeserializeObject<Head>(reader.ReadToEnd());
        if (head?.meta == null || head.meta.schema != 1 || !Guid.TryParseExact(head.generation, "N", out _))
            throw new InvalidDataException("槽位索引损坏或格式不受支持。");
        if (!File.Exists(Path.Combine(Slot(slot), head.generation, "save.sv"))) throw new InvalidDataException("槽位缺少主机存档。");
        return head;
    }
    internal string Data(int slot) {
        var head = Read(slot) ?? throw new InvalidDataException("空槽位不能加载。");
        return Path.Combine(Slot(slot), head.generation);
    }
    internal bool HasAny => Enumerable.Range(1, Count).Any(i => File.Exists(Path.Combine(Slot(i), "current.json")));
    internal int ReadActive() {
        string file = Path.Combine(root, "active_slot.json");
        if (!File.Exists(file)) return 0;
        int slot = JsonConvert.DeserializeObject<int>(File.ReadAllText(file));
        return slot >= 1 && slot <= Count ? slot : 0;
    }
    internal void WriteActive(int slot) {
        if (slot != 0) Slot(slot);
        AtomicJson(Path.Combine(root, "active_slot.json"), slot);
    }
    internal void Publish(int slot, Meta meta, string source, Action<string> writeAndValidate) {
        lock (gate) PublishCore(slot, meta, source, writeAndValidate);
    }
    void PublishCore(int slot, Meta meta, string source, Action<string> writeAndValidate) {
        meta.name = CleanName(meta.name);
        string parent = Slot(slot);
        Directory.CreateDirectory(parent);
        using var writerLock = new FileStream(Path.Combine(root, ".slot-writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string previous = null;
        try { previous = Read(slot)?.generation; } catch { /* Explicit overwrite can repair a corrupt head. */ }
        string generation = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, ".tmp-" + generation);
        string destination = Path.Combine(parent, generation);
        bool published = false;
        try {
            Directory.CreateDirectory(staging);
            if (Directory.Exists(source)) CopyTree(source, staging);
            writeAndValidate(staging);
            if (!File.Exists(Path.Combine(staging, "save.sv")) || !File.Exists(Path.Combine(staging, "mp_rules.json")))
                throw new InvalidDataException("存档文件不完整，未替换旧存档。");
            // Flush every staged file before publishing its immutable generation.
            foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) stream.Flush(true);
            Directory.Move(staging, destination);
            AtomicJson(Path.Combine(parent, "current.json"), new Head { generation = generation, meta = meta });
            published = true;
            // Keep the preceding generation for manual recovery. Cleanup is deliberately
            // separate from the commit; a cleanup failure must never undo a good save.
            try {
                foreach (string dir in Directory.GetDirectories(parent)) {
                    string name = Path.GetFileName(dir);
                    if (name != generation && name != previous && Guid.TryParseExact(name, "N", out _))
                        try { Directory.Delete(dir, true); } catch { }
                }
            } catch { }
        } finally {
            if (Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch { } }
            if (!published && Directory.Exists(destination)) { try { Directory.Delete(destination, true); } catch { } }
        }
    }
    internal void PrepareLoad(int slot, Action<string> validate) {
        lock (gate) PrepareLoadCore(slot, validate);
    }
    void PrepareLoadCore(int slot, Action<string> validate) {
        string source = Data(slot);
        validate(source); // Never disturb the live working directory on preflight failure.
        string temp = Path.Combine(root, "_load-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(root, "_session.previous");
        try {
            CopyTree(source, temp);
            validate(temp);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(Working)) Directory.Move(Working, backup);
            try { Directory.Move(temp, Working); }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, Working); throw; }
        } finally {
            if (Directory.Exists(temp)) { try { Directory.Delete(temp, true); } catch { } }
        }
    }
    internal bool Migrate(Func<string, Meta> readMeta, Action<string> validate) {
        // Only legacy root files/player directories are copied. Reserved directories
        // are excluded, including abandoned stages from an interrupted migration.
        if (HasAny || !File.Exists(Path.Combine(root, "save.sv"))) return false;
        var meta = readMeta(root);
        meta.name = "旧存档（自动迁移）";
        Publish(1, meta, null, stage => {
            foreach (string file in Directory.GetFiles(root)) {
                string name = Path.GetFileName(file);
                if (name == "active_slot.json" || name == "slot_meta.json" || name == ".slot-writer.lock" || name.Contains(".tmp-")) continue;
                File.Copy(file, Path.Combine(stage, name));
            }
            foreach (string dir in Directory.GetDirectories(root)) {
                string name = Path.GetFileName(dir);
                if (name == "slots" || name.StartsWith("_", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal)) continue;
                CopyTree(dir, Path.Combine(stage, name));
            }
            validate(stage);
        });
        WriteActive(1);
        return true;
    }
    internal static void CopyTree(string source, string target) {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("不允许存档目录链接。");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("不允许存档文件链接。");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        foreach (string dir in Directory.GetDirectories(source)) CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
    static void AtomicJson(string file, object value) {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.Indented));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            // Atomic content replacement must also work for a user with Modify
            // access who cannot merge optional ACL/metadata from the old file.
            if (File.Exists(file)) File.Replace(temp, file, null, true);
            else File.Move(temp, file);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
#endif
