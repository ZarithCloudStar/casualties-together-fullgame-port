#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;

namespace Together;

internal static class SaveSlots {
    // Existing opt-in A/B fixtures share one legacy folder between two processes.
    // Keep that explicit testing contract; slot storage has separate offline tests.
    internal static bool Enabled {
        get {
#if TESTING
            if (Environment.GetCommandLineArgs().Contains("--mp-ending-test")) return false;
#endif
            return Util.GameIsFull();
        }
    }
    static SaveSlotStore store;
    static int active, saveTarget, loading;
    static object loadingSession;
    static bool enteredLoadingWorld;
    static string saveName, writeDirectory;
    internal static bool Busy { get; private set; }
    internal static int CommitSerial { get; private set; }
    internal static bool LoadPrepared => loading != 0;
    internal static int Active { get { Ensure(); return active; } }
    internal static SaveSlotStore Store { get { Ensure(); return store; } }
    internal static string DirectoryPath { get { Ensure(); return writeDirectory ?? store.Working; } }
    internal static string PreviewDirectory {
        get {
            Ensure();
            foreach (int i in Enumerable.Range(1, 5).OrderBy(i => i == active ? 0 : 1)) {
                try { if (store.Read(i) != null) return store.Data(i); }
                catch (Exception ex) { log.warn($"SLOTS: preview slot={i}: {ex.Message}"); }
            }
            return store.Working;
        }
    }
    internal static bool HasSave {
        get {
            Ensure();
            for (int i = 1; i <= 5; i++) { try { if (store.Read(i) != null) return true; } catch { } }
            return false;
        }
    }
    internal static void Ensure() {
        if (store != null) return;
        store = new SaveSlotStore(SavesystemPatch.MultiplayerSaveRoot);
        try {
            // 迁移前先修复旧档常见的不一致：当前层快照与 save.sv 不是同一层/同一存档时，
            // 丢弃快照并按"本层开头"迁移（旧版本也只是 warn，不会因此拒绝）。
            if (store.Migrate(ReadMeta, stage => { RepairLegacySnapshot(stage); Validate(stage); }))
                log.l("SLOTS: copied legacy mp_save to slot=1; root originals retained");
        } catch (Exception ex) { log.error("SLOTS: legacy migration failed; original files retained: " + ex); }
        try { active = store.ReadActive(); }
        catch (Exception ex) { log.error("SLOTS: active slot could not be read: " + ex); }
        log.l($"SLOTS: initialized active={active} working={store.Working}");
    }
    static void RepairLegacySnapshot(string folder) {
        try {
            var rulesPath = Path.Combine(folder, SavesystemPatch.mpsaveheaderfile);
            if (!File.Exists(rulesPath)) return;
            var rules = JObject.Parse(File.ReadAllText(rulesPath));
            if (!(rules.Value<bool?>("LEVEL") ?? false)) return;
            var root = ReadRoot(folder);
            int? saveBiome = root.Value<int?>("biome");
            var objects = Path.Combine(folder, "world_objects.bin");
            if (saveBiome == null || !File.Exists(objects)) return;
            var document = ReadSnapshotDocument(objects);
            if (document.biome == saveBiome.Value) return;
            foreach (var name in new[] { "world_tiles.bin", "world_fluid.bin", "world_objects.bin" }) {
                var file = Path.Combine(folder, name);
                if (File.Exists(file)) File.Delete(file);
            }
            rules["LEVEL"] = false;
            File.WriteAllText(rulesPath, rules.ToString(Newtonsoft.Json.Formatting.Indented));
            log.warn($"SLOTS: 旧存档的当前层快照属于层 {document.biome}，而 save.sv 在层 {saveBiome.Value}；"
                + "已丢弃该快照并按“本层开头”迁移（角色/物品/规则保留）。");
        } catch (Exception e) {
            log.warn("SLOTS: 旧存档快照修复失败，按原样继续迁移: " + e.Message);
        }
    }
    internal static void BeginNewRun() {
        Ensure();
        // Only session scratch is discarded; published slots and legacy root stay.
        if (Directory.Exists(store.Working)) Directory.Delete(store.Working, true);
        active = loading = 0;
        try { store.WriteActive(0); } catch (Exception ex) { log.warn("SLOTS: could not persist new-run active reset: " + ex); }
        ResetCaches();
        log.l("SLOTS: new run, no active slot; all five slots retained");
    }
    static void ResetCaches() {
        CharacterRunMemory.ResetForDirectory();
        PlayerCheckpoint.ResetSaveCaches();
        foreach (var state in ServerMain.Server_LastPlayerStates.Values) state.Discard();
        ServerMain.Server_LastPlayerStates.Clear();
        foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values)
            if (player?.ServerPlayerState != null) player.ServerPlayerState.loaded_save = false;
    }
    internal static void RequestLoad() {
        if (!Net.IsServer || !Net.IsRunning || Util.IsInWorld() || Busy || LoadPrepared) return;
        SaveSlotMenu.Open(true, (slot, name) => {
            try {
                PrepareLoadSlot(slot);
                log.l($"SLOTS: loading slot={slot} source={Store.Data(slot)} working={DirectoryPath}");
                PreRunScript.instance.LoadRun(); // character admission and existing world/WS1 path
            } catch (Exception ex) { loading = 0; Report("读档失败，原存档已保留。", ex); }
        });
    }

    internal static void ImportTransferredSlot(int slot, string source, string hostName) {
        if (!Enabled || slot < 1 || slot > SaveSlotStore.Count) throw new InvalidOperationException("无效的存档槽位。");
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) throw new InvalidDataException("迁移存档暂存目录不存在。");
        Ensure();
        Validate(source);
        var root = ReadRoot(source);
        var meta = new SaveSlotStore.Meta {
            name = SaveSlotStore.CleanName("主机迁移存档"),
            hostName = string.IsNullOrWhiteSpace(hostName) ? "新主机" : hostName,
            modVersion = Plugin.MOD_VERSION,
            buildTag = KSteam.GetModBuildTag(),
            savedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            layer = root.Value<int>("biome"),
            playerCount = Directory.GetDirectories(source).Count(path => Path.GetFileName(path).StartsWith("STEAM_", StringComparison.Ordinal))
        };
        Store.Publish(slot, meta, source, Validate);
        RememberActive(slot);
        log.l($"SLOTS: imported host transfer slot={slot} source={source}");
    }

    internal static void PrepareTransferredLoad(int slot) {
        if (!Enabled || !Net.IsServer || !Net.IsRunning || slot < 1 || slot > SaveSlotStore.Count)
            throw new InvalidOperationException("无法准备迁移存档。");
        PrepareLoadSlot(slot);
        log.l($"SLOTS: prepared transferred slot={slot} source={Store.Data(slot)} working={DirectoryPath}");
    }

    static void PrepareLoadSlot(int slot) {
        if (Busy || LoadPrepared) throw new InvalidOperationException("存档加载正在进行。");
        Store.PrepareLoad(slot, Validate);
        ResetCaches();
        loading = slot;
        active = 0;
        loadingSession = Net.ServerEntityManager;
        enteredLoadingWorld = false;
        SavesystemPatch.savedatapathreplacement = DirectoryPath;
    }
    internal static void ApplyPreparedSettings() {
        var settings = ReadRunSettings(ReadRoot(DirectoryPath));
        var header = JObject.Parse(File.ReadAllText(Path.Combine(DirectoryPath, SavesystemPatch.mpsaveheaderfile)));
        Multiplayer.rules = header["RULES"].ToObject<MultiplayerGameRules>();
        WorldMP.ApplyRunSettings(settings);
        log.l($"SLOTS: applied saved run settings and multiplayer rules before starting slot={loading}");
    }

    internal static Dictionary<string, object> ReadRunSettings(JObject root) {
        return RunSettingsCodec.DecodeSaved(root["runSettings"]);
    }

    internal static void LoadCompleted(bool success) {
        if (!Enabled || loading == 0) return;
        if (success) { RememberActive(loading); log.l($"SLOTS: load completed slot={loading}"); }
        else Report("槽位读档未完整完成，未更改当前槽位。", null);
        loading = 0;
    }
    internal static void CancelLoad() { loading = 0; enteredLoadingWorld = false; }
    internal static void Tick() {
        if (loading == 0) return;
        if (!Net.IsRunning || !Net.IsServer || !ReferenceEquals(loadingSession, Net.ServerEntityManager)) { CancelLoad(); return; }
        if (Util.IsInWorld()) enteredLoadingWorld = true;
        else if (enteredLoadingWorld || (!WorldMP.IsMenuLoadPending && !CharacterReadyGate.LoadQueued)) CancelLoad();
    }
    internal static void WithTarget(int slot, string name, Action action) {
        if (!Net.IsServer || !Net.IsRunning) throw new InvalidOperationException("只有主机可以保存。");
        saveTarget = slot; saveName = SaveSlotStore.CleanName(name);
        try { action(); }
        finally { saveTarget = 0; saveName = null; }
    }
    internal static void Commit(Action writeAndValidate) {
        Ensure();
        if (!Net.IsServer || !Net.IsRunning || Busy || DeferredPlayerSaveRestore.InProgress)
            throw new InvalidOperationException("主机或玩家尚未准备好保存。");
        int slot = saveTarget != 0 ? saveTarget : active;
        if (slot == 0) throw new InvalidOperationException("请先选择存档槽位。");
        string name = saveName ?? store.Read(slot)?.meta.name ?? $"第{WorldGeneration.world.biomeDepth}层存档";
        Busy = true;
        string previousPath = SavesystemPatch.savedatapathreplacement;
        try {
            var meta = new SaveSlotStore.Meta { name = name, hostName = ScavPlayer.LocalPlayer?.playerName ?? "主机",
                modVersion = Plugin.MOD_VERSION, buildTag = KSteam.GetModBuildTag(), savedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                playerCount = ScavPlayer.ClientIdToPlayerDict.Count };
            log.l($"SLOTS: saving slot={slot} name={name} source={store.Working}");
            store.Publish(slot, meta, store.Working, stage => {
                writeDirectory = stage;
                SavesystemPatch.savedatapathreplacement = stage;
                try {
                    writeAndValidate();
                    Validate(stage);
                    meta.layer = ReadRoot(stage).Value<int>("biome");
                    log.l($"SLOTS: validated slot={slot} temporary={stage} layer={meta.layer}");
                } finally { writeDirectory = null; SavesystemPatch.savedatapathreplacement = previousPath; }
            });
            CommitSerial++;
            RememberActive(slot);
            log.l($"SLOTS: committed slot={slot} directory={store.Data(slot)}");
        } finally { Busy = false; writeDirectory = null; SavesystemPatch.savedatapathreplacement = previousPath; }
    }
    static void RememberActive(int slot) {
        active = slot;
        // The slot itself is already committed. A preference failure is reported
        // separately and must not be misreported as loss of the successful save.
        try { store.WriteActive(slot); }
        catch (Exception ex) { Report("存档已完成，但未能记住当前槽位；下次请重新选择。", ex); }
    }
    internal static void SaveVanilla() {
        bool midlevel = !WorldGeneration_SaveAndExit_Slots.InBottomExit && !ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();
        Commit(() => {
            if (midlevel) { SaveCurrentLayerExit.WriteAndValidate(WorldGeneration.world, WorldGeneration.world.biomeDepth); return; }
            var players = ScavPlayer.ClientIdToPlayerDict.Values.Where(p => p != null).OrderBy(p => p.IsLocal).ToArray();
            if (players.Length == 0 || !players.Any(p => p.IsLocal)) throw new IOException("缺少主机角色。");
            foreach (var player in players) {
                if (player.body == null) throw new IOException("玩家角色尚未就绪：" + player.playerName);
                if (!player.body.alive && Multiplayer.rules.CanReviveOnNextLevel())
                    player.Server_RespawnCharacter(Util.GetLocalBody().transform.position, true);
                player.ResetEntropy();
                string playerFile = Path.Combine(DirectoryPath, player.GetPersistentId(), "save.sv");
                // Never accept a cloned offline checkpoint if a guarded writer skipped.
                if (File.Exists(playerFile)) File.Delete(playerFile);
                PlayerCheckpoint.RunChecked(() => PlayerCheckpoint.WriteOne(player, true));
                ReadRoot(Path.Combine(DirectoryPath, player.GetPersistentId()));
            }
            PlayerCheckpoint.RunChecked(() => PlayerCheckpoint.WriteRules(false));
            foreach (string file in new[] { "world_tiles.bin", "world_fluid.bin", "world_objects.bin" }) {
                string path = Path.Combine(DirectoryPath, file);
                if (File.Exists(path)) File.Delete(path); // next-layer saves must not carry stale terrain
            }
        });
    }
    internal static JObject ReadRoot(string folder) => JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(Path.Combine(folder, "save.sv"))));
    internal static void Validate(string folder) {
        var root = ReadRoot(folder);
        if (root.Value<int?>("biome") == null || root["body"] == null || root["items"] == null) throw new InvalidDataException("主机存档数据不完整。");
        var rules = JObject.Parse(File.ReadAllText(Path.Combine(folder, SavesystemPatch.mpsaveheaderfile)));
        if (rules["RULES"] == null || rules["LEVEL"] == null) throw new InvalidDataException("多人规则数据不完整。");
        // 旧版本从不要求这两个 id 相等（SavesystemPatch 旧加载路径只 warn 不拒绝），而玩家的历史存档
        // 经常由不同时间点的两次存档拼成。保持旧版本的兼容语义：不一致只告警，快照按它自己写入时的
        // id 校验，否则老进度永远迁不进槽位。
        var rulesSaveId = rules.Value<int?>("SAVEID");
        var bodySaveId = root.Value<int?>("cId");
        bool identityMismatch = rulesSaveId != bodySaveId;
        if (identityMismatch) {
            log.warn($"SLOTS: 存档身份不一致（rules.SAVEID={rulesSaveId} save.sv.cId={bodySaveId}），按旧版本兼容方式继续");
        }
        if (rules.Value<bool>("LEVEL")) {
            foreach (string file in new[] { "world_tiles.bin", "world_fluid.bin", "world_objects.bin" })
                if (!File.Exists(Path.Combine(folder, file)) || new FileInfo(Path.Combine(folder, file)).Length == 0) throw new InvalidDataException("当前层快照缺失：" + file);
            // Menu/migration preflight cannot call WorldObjectSnapshot.Validate,
            // which deliberately requires a live server world. Decode disk fields
            // against the saved identity, then reuse its structural validator.
            var document = ReadSnapshotDocument(Path.Combine(folder, "world_objects.bin"));
            int expectedSnapshotId = identityMismatch ? document.saveId : (rulesSaveId ?? bodySaveId ?? 0);
            WorldObjectSnapshotData.Validate(document, root.Value<int>("biome"), document.width, document.height, expectedSnapshotId);
            long cells = (long)document.width * document.height;
            if (cells <= 0 || cells > WorldSnapshotTransfer.MaxBytes) throw new InvalidDataException("快照世界尺寸无效。");
            WorldSnapshotTransfer.Inflate(File.ReadAllBytes(Path.Combine(folder, "world_tiles.bin")), (int)cells);
            WorldSnapshotTransfer.Inflate(File.ReadAllBytes(Path.Combine(folder, "world_fluid.bin")), (int)cells);
        }
    }
    static WorldObjectDocument ReadSnapshotDocument(string file) {
        using var input = File.OpenRead(file);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = gzip.Read(buffer, 0, buffer.Length)) != 0) {
            if (output.Length + count > 128 * 1024 * 1024) throw new InvalidDataException("快照超过大小限制。");
            output.Write(buffer, 0, count);
        }
        return Newtonsoft.Json.JsonConvert.DeserializeObject<WorldObjectDocument>(System.Text.Encoding.UTF8.GetString(output.ToArray()),
            new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None, MaxDepth = 64 })
            ?? throw new InvalidDataException("世界对象快照为空。");
    }
    static SaveSlotStore.Meta ReadMeta(string folder) {
        var root = ReadRoot(folder);
        var rules = JObject.Parse(File.ReadAllText(Path.Combine(folder, SavesystemPatch.mpsaveheaderfile)));
        return new SaveSlotStore.Meta { layer = root.Value<int>("biome"), savedAtUnix = new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(folder, "save.sv"))).ToUnixTimeSeconds(),
            playerCount = (rules["PLRPOSE"] as JObject)?.Count ?? (rules["PLRPOS"] as JObject)?.Count ?? 1,
            hostName = "旧存档主机", modVersion = rules.Value<string>("MPVERSION") ?? "未知", buildTag = "未知（旧存档）" };
    }
    internal static void Report(string message, Exception ex) {
        log.error("SLOTS: " + message + (ex == null ? "" : " " + ex));
        try { SaveSlotMenu.ShowError(message); }
        catch (Exception uiError) { log.error("SLOTS: could not display modal: " + uiError); }
        try { Multiplayer.DoMultiplayerStatusMessageLog(message); }
        catch (Exception statusError) { log.error("SLOTS: could not display status: " + statusError); }
    }
}

// Stop before native progress mutations when no target exists. Once selected,
// re-enter the same native flow. The SaveGame prefix commits before ToMainMenu.
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SaveAndExit))]
internal static class WorldGeneration_SaveAndExit_Slots {
    internal static bool InBottomExit { get; private set; }
    internal sealed class Progress {
        internal float loot, traps;
        internal int traveled;
        internal int commitSerial, deepest;
        internal Skills skills;
        internal string skillsJson;
    }
    [HarmonyPriority(Priority.First)]
    static bool Prefix(WorldGeneration __instance, bool __runOriginal, out Progress __state) {
        __state = null;
        if (!__runOriginal) return false;
        if (!SaveSlots.Enabled) return true;
        if (!Net.IsServer || !Net.IsRunning || SaveSlots.Busy || SaveCurrentLayerExit.IsSaving || SaveSlotMenu.IsOpen) return false;
        if (SavesystemPatch.PreserveSave) return true;
        if (SaveSlots.Active == 0 && !resuming) {
            SaveSlotMenu.Open(false, (slot, name) => {
                if (__instance == null || !ReferenceEquals(WorldGeneration.world, __instance)) return;
                SaveSlots.WithTarget(slot, name, () => {
                    resuming = true;
                    try { __instance.SaveAndExit(); } finally { resuming = false; }
                });
            });
            return false;
        }
        __state = new Progress { loot = __instance.lootRarityMultiplier, traps = __instance.trapRarityMultiplier,
            traveled = __instance.totalTraveled, skills = __instance.body.skills,
            commitSerial = SaveSlots.CommitSerial, deepest = PlayerPrefs.GetInt("deepestlayer"),
            skillsJson = Newtonsoft.Json.JsonConvert.SerializeObject(__instance.body.skills) };
        InBottomExit = true;
        return true;
    }
    static bool resuming;
    static Exception Finalizer(Exception __exception, WorldGeneration __instance, Progress __state) {
        if (__state != null) InBottomExit = false;
        if (__exception == null || __state == null) return __exception;
        if (SaveSlots.CommitSerial != __state.commitSerial) {
            SaveSlots.Report("存档已保存，但返回大厅失败；可稍后手动返回。", __exception);
            return null;
        }
        __instance.lootRarityMultiplier = __state.loot; __instance.trapRarityMultiplier = __state.traps;
        __instance.totalTraveled = __state.traveled;
        Newtonsoft.Json.JsonConvert.PopulateObject(__state.skillsJson, __state.skills);
        PlayerPrefs.SetInt("deepestlayer", __state.deepest);
        SaveSlots.Report("保存失败，旧存档已保留；已取消返回大厅，请重试。", __exception);
        return null;
    }
}

// Native saveandquit decrements biomeDepth before SaveGame. Route it to the
// current-layer entry before that mutation, so it shares the slot transaction
// and cannot save one layer too early (or leave a decremented live world on error).
[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.RegisterAllCommands))]
internal static class ConsoleSaveAndQuit_Slots {
    [HarmonyPriority(Priority.Last)]
    static void Postfix() {
        var command = ConsoleScript.SearchExact("saveandquit");
        if (command == null) return;
        var original = command.action;
        command.action = args => {
            if (!SaveSlots.Enabled || !Net.IsRunning || SavesystemPatch.PreserveSave || !Util.IsInWorld()) { original(args); return; }
            if (!Net.IsServer) { SaveSlots.Report("只有主机可以保存并退出。", null); return; }
            if (Con.IsConsoleOpen()) ConsoleScript.instance.ToggleActiveState();
            SaveCurrentLayerExit.Request();
        };
    }
}
#endif
