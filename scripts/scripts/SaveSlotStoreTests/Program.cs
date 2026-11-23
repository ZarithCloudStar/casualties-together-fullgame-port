using Together;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;

// Executes only the production storage code. No game, Unity, networking or user
// save paths are loaded. Faults are real IO failures on isolated test files.
static class Program {
    static int passed;
    static void Check(bool condition, string name) {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); passed++;
    }
    static void Throws(Action action, string name) {
        bool threw = false;
        try { action(); } catch { threw = true; }
        Check(threw, name);
    }
    static SaveSlotStore.Meta Meta(string name) => new() { name = name, layer = 3, playerCount = 2,
        hostName = "测试主机", savedAtUnix = 1234567890, modVersion = "测试版本", buildTag = "测试构建" };
    static void Write(string dir, string content) {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "save.sv"), content);
        File.WriteAllText(Path.Combine(dir, "mp_rules.json"), "规则 " + content);
        Directory.CreateDirectory(Path.Combine(dir, "NAME_123"));
        File.WriteAllText(Path.Combine(dir, "NAME_123", "save.sv"), "角色 " + content);
        File.WriteAllText(Path.Combine(dir, "character_choices.txt"), "seed=" + content);
        File.WriteAllText(Path.Combine(dir, "world_tiles.bin"), "地形 " + content);
    }
    static void WriteSp(string dir, string content) {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "save.sv"), content);
    }
    static string Hash(string dir) => string.Join(";", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x)
        .Select(x => Path.GetRelativePath(dir, x) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x)))));
    static void Main(string[] args) {
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new Exception("Test root must be new: " + root);
        Directory.CreateDirectory(root);
        var store = new SaveSlotStore(Path.Combine(root, "new"));
        for (int i = 1; i <= 5; i++) {
            Check(store.Read(i) == null, "empty slot " + i);
            int copy = i;
            store.Publish(i, Meta("槽位 " + i), null, d => Write(d, "内容 " + copy));
        }
        for (int i = 1; i <= 5; i++) Check(File.ReadAllText(Path.Combine(store.Data(i), "save.sv")) == "内容 " + i, "five-slot isolation " + i);
        // SP slots contain only save.sv and must not require mp_rules.json.
        var spStore = new SaveSlotStore(Path.Combine(root, "sp"), requireRules: false);
        for (int i = 1; i <= 5; i++) {
            int copy = i;
            spStore.Publish(i, Meta("单人 " + i), null, d => WriteSp(d, "单机 " + copy));
        }
        for (int i = 1; i <= 5; i++) Check(File.ReadAllText(Path.Combine(spStore.Data(i), "save.sv")) == "单机 " + i, "singleplayer slot isolation " + i);
        Throws(() => spStore.Publish(1, Meta("缺 save"), null, d => { }), "singleplayer missing save.sv rejected");
        var strictStore = new SaveSlotStore(Path.Combine(root, "strict"));
        Throws(() => strictStore.Publish(1, Meta("缺 rules"), null, d => WriteSp(d, "仅 save")), "multiplayer rules still required");
        Check(strictStore.Read(1) == null, "failed strict publish leaves the slot empty");
        Check(File.ReadAllText(Path.Combine(spStore.Data(3), "save.sv")) == "单机 3", "singleplayer store unaffected by strict failure");
        string slot2 = Hash(store.Data(2)), slot1 = Hash(store.Data(1));
        string head1 = store.Read(1).generation;
        store.Publish(1, Meta("重命名"), store.Data(1), d => Write(d, "新内容"));
        Check(store.Read(1).meta.name == "重命名" && store.Read(1).meta.layer == 3 && store.Read(1).meta.schema == 1, "metadata commits with data");
        Check(Hash(store.Data(2)) == slot2, "overwriting slot 1 preserves slot 2");
        Check(Directory.Exists(Path.Combine(root, "new", "slots", "slot1", head1)), "preceding generation retained");
        string good = Hash(store.Data(1)), head = store.Read(1).generation;
        Throws(() => store.Publish(1, Meta("失败"), store.Data(1), d => { Write(d, "半写"); throw new IOException("injected write failure"); }), "partial writer failure propagates");
        Check(store.Read(1).generation == head && Hash(store.Data(1)) == good, "writer failure preserves exact old data and head");
        using (var held = new FileStream(Path.Combine(root, "new", "slots", "slot1", "current.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Throws(() => store.Publish(1, Meta("替换失败"), store.Data(2), d => Write(d, "完成但未提交")), "atomic pointer replacement IO failure propagates");
        }
        Check(store.Read(1).generation == head && Hash(store.Data(1)) == good, "pointer failure preserves exact old data");
        Check(Directory.GetDirectories(Path.Combine(root, "new", "slots", "slot1"), ".tmp-*").Length == 0, "failed stages cleaned");
        Throws(() => store.Publish(0, Meta("非法"), null, _ => {}), "slot 0 rejected");
        Throws(() => store.Publish(6, Meta("非法"), null, _ => {}), "slot 6 rejected");
        Throws(() => SaveSlotStore.CleanName(" ../\\:*?<>|\" "), "empty traversal name rejected");
        Check(SaveSlotStore.CleanName("../测试:名\\/称").Contains("测试名称"), "invalid characters and traversal removed");
        Check(SaveSlotStore.CleanName(new string('层', 40)).Length == 24, "24-character limit");
        Throws(() => SaveSlotStore.CleanName("   "), "blank name rejected");
        store.WriteActive(4);
        Check(new SaveSlotStore(Path.Combine(root, "new")).ReadActive() == 4, "active slot survives store restart");
        store.PrepareLoad(2, _ => {});
        Check(Hash(store.Working) == slot2, "load copies complete chosen slot");
        File.WriteAllText(Path.Combine(store.Working, "save.sv"), "后台 checkpoint");
        Check(Hash(store.Data(2)) == slot2, "background working writes cannot mutate committed slot");
        string working = Hash(store.Working);
        Throws(() => store.PrepareLoad(3, _ => throw new IOException("preflight")), "failed load preflight propagates");
        Check(Hash(store.Working) == working, "failed load preserves working data");
        store.PrepareLoad(5, _ => {});
        Check(File.ReadAllText(Path.Combine(store.Working, "save.sv")) == "内容 5", "switch slot replaces working data");
        var empty = new SaveSlotStore(Path.Combine(root, "empty"));
        Throws(() => empty.PrepareLoad(1, _ => {}), "empty slot cannot load");
        string legacy = Path.Combine(root, "legacy"); Write(legacy, "旧档");
        var migration = new SaveSlotStore(legacy); string original = Hash(legacy);
        Check(migration.Migrate(_ => Meta("无名"), _ => {}), "legacy automatic migration");
        Check(migration.Read(1).meta.name == "旧存档（自动迁移）" && migration.ReadActive() == 1, "migration name and active slot");
        Check(Hash(migration.Data(1)) == original, "migration copies every legacy file and character binding");
        foreach (string file in Directory.GetFiles(legacy, "*", SearchOption.AllDirectories).Where(x => !x.Contains(Path.DirectorySeparatorChar + "slots" + Path.DirectorySeparatorChar) && !x.EndsWith("active_slot.json") && !x.EndsWith(".slot-writer.lock")))
            Check(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(migration.Data(1), Path.GetRelativePath(legacy, file)))), "legacy original retained " + Path.GetRelativePath(legacy, file));
        Check(!migration.Migrate(_ => Meta("不可迁移"), _ => throw new Exception()), "existing slots prevent duplicate migration");
        string broken = Path.Combine(root, "failed-migration"); Write(broken, "旧档不动");
        var failedMigration = new SaveSlotStore(broken); string beforeMigration = Hash(broken);
        Throws(() => failedMigration.Migrate(_ => Meta("失败"), _ => throw new IOException("migration failure")), "migration failure propagates to logged startup guard");
        Check(!failedMigration.HasAny && Hash(broken).Replace(".slot-writer.lock:" + Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())) + ";", "") == beforeMigration, "migration failure preserves original files");
        Check(failedMigration.Migrate(_ => Meta("重试"), _ => {}), "migration retries after failure");
        var readerErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() => {
            while (!stop.IsCancellationRequested) {
                try { store.Read(1); /* Read checks that the committed generation is complete under the storage gate. */ }
                catch (Exception ex) { readerErrors.Enqueue(ex); }
            }
        });
        for (int i = 0; i < 12; i++) store.Publish(1, Meta("原子提交 " + i), null, d => Write(d, "原子提交 " + i));
        stop.Cancel(); reader.Wait();
        Check(readerErrors.IsEmpty, "concurrent readers only see complete committed heads");
        Check(Directory.GetDirectories(Path.Combine(root, "new", "slots", "slot1")).Length == 2, "bounded retention keeps current and previous generation");
        if (args.Length >= 3) CheckProductionPreflight(root, args[1], args[2]);
        Console.WriteLine($"ALL {passed} CHECKS PASSED; isolated files: {root}");
    }
    static void CheckProductionPreflight(string root, string plugin, string fixture) {
        // Execute only the built DLL's disk preflight, without creating a Unity
        // world or calling any game lifecycle method. Dependencies are read-only.
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) => {
            string name = new AssemblyName(request.Name).Name + ".dll";
            foreach (string dir in new[] { Path.GetDirectoryName(plugin), @"F:\cu-port\CU_Fullgame\CasualtiesUnknown_Data\Managed", @"F:\cu-port\CU_Fullgame\BepInEx\core" }) {
                string file = Path.Combine(dir, name);
                if (File.Exists(file)) return Assembly.LoadFrom(file);
            }
            return null;
        };
        var assembly = Assembly.LoadFrom(Path.GetFullPath(plugin));
        var validate = assembly.GetType("Together.SaveSlots", true).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static);
        void Validate(string folder) {
            try { validate.Invoke(null, new object[] { folder }); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }
        Validate(fixture);
        Check(true, "production DLL preflights an existing real checkpoint without a Unity world");
        string realRoot = Path.Combine(root, "real-checkpoint");
        SaveSlotStore.CopyTree(fixture, realRoot);
        var real = new SaveSlotStore(realRoot);
        Check(real.Migrate(_ => Meta("真实旧档"), Validate), "real checkpoint migration uses production preflight");
        string original = Hash(real.Data(1));
        real.PrepareLoad(1, Validate);
        Check(Hash(real.Working) == original, "real checkpoint load preserves terrain, body, choices and poses byte-for-byte");
        Throws(() => real.Publish(1, Meta("截断快照"), real.Working, stage => {
            File.WriteAllBytes(Path.Combine(stage, "world_tiles.bin"), new byte[] { 1, 2, 3 });
            Validate(stage);
        }), "production preflight rejects truncated terrain before commit");
        Check(Hash(real.Data(1)) == original, "truncated real checkpoint preserves old committed generation");
        string rules = Path.Combine(real.Working, "mp_rules.json");
        var header = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(rules));
        header["SAVEID"] = -1;
        File.WriteAllText(rules, header.ToString());
        Throws(() => Validate(real.Working), "production preflight rejects mismatched host/rules identity");
    }
}
