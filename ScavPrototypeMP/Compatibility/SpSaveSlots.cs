#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

// Single-player counterpart of SaveSlots. Same five-slot atomic store and the
// same SaveSlotMenu page, but the root is sp_save and each slot contains only
// the vanilla save.sv. It never touches mp_save.
internal static class SpSaveSlots {
    internal static bool Enabled => Util.GameIsFull();
    static SaveSlotStore store;
    static int active, saveTarget, loading;
    static string saveName, writeDirectory;
    static float loadingStarted;
    static bool enteredLoadingWorld;
    internal static bool Busy { get; private set; }
    // True while the vanilla SaveAndExit/SaveGame is running inside a slot
    // transaction. The SaveAndExit prefix and the SaveGame path reset must
    // both let this call through to the staging directory.
    internal static bool Committing { get; set; }
    internal static int CommitSerial { get; private set; }
    internal static bool LoadPrepared => loading != 0;
    internal static int Active { get { Ensure(); return active; } }
    internal static SaveSlotStore Store { get { Ensure(); return store; } }
    internal static string DirectoryPath { get { Ensure(); return writeDirectory ?? store.Working; } }
    internal static string Root {
        get {
#if FULLGAME && TESTING
            string fixture = Environment.GetEnvironmentVariable("CT_SP_SAVE_ROOT");
            if (!string.IsNullOrEmpty(fixture)) return fixture;
#endif
            return Path.Combine(Application.persistentDataPath, "sp_save");
        }
    }
    internal static string PreviewDirectory {
        get {
            Ensure();
            foreach (int i in Enumerable.Range(1, SaveSlotStore.Count).OrderBy(i => i == active ? 0 : 1)) {
                try { if (store.Read(i) != null) return store.Data(i); }
                catch (Exception ex) { log.warn($"SP-SLOTS: preview slot={i}: {ex.Message}"); }
            }
            return store.Working;
        }
    }
    internal static bool HasSave {
        get {
            Ensure();
            for (int i = 1; i <= SaveSlotStore.Count; i++) {
                try { if (store.Read(i) != null) return true; } catch { }
            }
            return false;
        }
    }

    internal static void Ensure() {
        if (store != null) return;
        store = new SaveSlotStore(Root, requireRules: false);
        ImportLegacySave();
        try { active = store.ReadActive(); }
        catch (Exception ex) { log.error("SP-SLOTS: active slot could not be read: " + ex); }
        log.l($"SP-SLOTS: initialized active={active} root={Root}");
    }

    // The old single-player save is the only legacy source. Copy only save.sv;
    // never enumerate persistentDataPath (it contains mp_save and game files).
    static void ImportLegacySave() {
        try {
            if (store.HasAny) return;
            string legacy = Path.Combine(Application.persistentDataPath, "save.sv");
            if (!File.Exists(legacy)) return;
            var meta = ReadMetaFile(legacy);
            meta.name = "旧存档（自动迁移）";
            meta.mode = "sp";
            store.Publish(1, meta, null, stage => {
                File.Copy(legacy, Path.Combine(stage, "save.sv"));
                Validate(stage);
            });
            store.WriteActive(1);
            log.l("SP-SLOTS: imported legacy save.sv into slot 1; original retained");
        } catch (Exception ex) {
            log.warn("SP-SLOTS: legacy import failed; original retained: " + ex.Message);
        }
    }

    internal static void BeginNewRun() {
        if (!Enabled) return;
        Ensure();
        // Only session scratch is discarded; the five slots stay.
        try { if (Directory.Exists(store.Working)) Directory.Delete(store.Working, true); }
        catch (Exception ex) { log.warn("SP-SLOTS: could not clear working directory: " + ex.Message); }
        active = loading = 0;
        enteredLoadingWorld = false;
        Committing = false;
        try { store.WriteActive(0); } catch (Exception ex) { log.warn("SP-SLOTS: could not reset active slot: " + ex.Message); }
        log.l("SP-SLOTS: new run; all slots retained");
    }

    internal static bool HandleLoadRun() {
        if (!Enabled) return true;
        Ensure();
        if (Net.IsRunning) return true;
        if (LoadPrepared) return true;
        RequestLoad();
        return false;
    }

    internal static void RequestLoad() {
        if (!Enabled || Net.IsRunning || Busy || LoadPrepared) return;
        Ensure();
        SaveSlotMenu.Open(SaveSlotMode.SinglePlayer, true, (slot, name) => {
            try {
                PrepareLoadSlot(slot);
                log.l($"SP-SLOTS: loading slot={slot} source={Store.Data(slot)} working={DirectoryPath}");
                if (PreRunScript.instance == null) throw new InvalidOperationException("主菜单尚未加载完成。");
                PreRunScript.instance.LoadRun();
            } catch (Exception ex) {
                loading = 0;
                Report("读档失败，原存档已保留。", ex);
            }
        });
    }

    static void PrepareLoadSlot(int slot) {
        if (Busy || LoadPrepared) throw new InvalidOperationException("存档加载正在进行。");
        Store.PrepareLoad(slot, Validate);
        loading = slot;
        loadingStarted = Time.realtimeSinceStartup;
        enteredLoadingWorld = false;
        active = slot;
        try { store.WriteActive(slot); } catch (Exception ex) { log.warn("SP-SLOTS: active write failed: " + ex.Message); }
        SavesystemPatch.savedatapathreplacement = DirectoryPath;
        SetSavedCharacter(ReadCharacter(DirectoryPath));
        log.l($"SP-SLOTS: prepared slot={slot} character={ReadCharacter(DirectoryPath)}");
    }

    internal static void CancelLoad() { loading = 0; enteredLoadingWorld = false; }

    // A menu scene load ends any loaded-run session; the menu preview must use
    // the published slots, not the (possibly deleted) working copy.
    internal static void OnMenuStart() {
        if (loading == 0) return;
        loading = 0;
        enteredLoadingWorld = false;
    }

    internal static void Tick() {
        if (loading == 0) return;
        if (!Enabled || Net.IsRunning) { loading = 0; enteredLoadingWorld = false; return; }
        // Keep the latch for the whole loaded world; the working copy is the
        // path source until the player actually leaves the run.
        if (Util.IsInWorld()) { enteredLoadingWorld = true; return; }
        if (enteredLoadingWorld || Time.realtimeSinceStartup - loadingStarted > 15f) {
            loading = 0;
            enteredLoadingWorld = false;
            if (Util.IsInMainMenu()) {
                try { SavesystemPatch.RefreshMainMenuSavePreview(); }
                catch (Exception ex) { log.warn("SP-SLOTS: preview refresh after load end failed: " + ex.Message); }
            }
        }
    }

    internal static void RequestSave(WorldGeneration world) {
        if (!Enabled || Net.IsRunning || world == null || Busy || SaveSlotMenu.IsOpen) return;
        if (!ReferenceEquals(WorldGeneration.world, world) || !Util.IsWorldGenerated()) return;
        SaveSlotMenu.Open(SaveSlotMode.SinglePlayer, false, (slot, name) => {
            if (!Enabled || Net.IsRunning || !ReferenceEquals(WorldGeneration.world, world)) return;
            SaveWithRollback(world, slot, name);
        });
    }

    // saveandquit saves the current layer, not the next one. Reuse the same
    // slot transaction but write after the vanilla depth decrement.
    internal static void RequestSaveCurrentLayer(WorldGeneration world) {
        if (!Enabled || Net.IsRunning || world == null || Busy || SaveSlotMenu.IsOpen) return;
        if (!ReferenceEquals(WorldGeneration.world, world) || !Util.IsWorldGenerated()) return;
        SaveSlotMenu.Open(SaveSlotMode.SinglePlayer, false, (slot, name) => {
            if (!Enabled || Net.IsRunning || !ReferenceEquals(WorldGeneration.world, world)) return;
            SaveWithRollback(world, slot, name, currentLayer: true);
        });
    }

    static void SaveWithRollback(WorldGeneration world, int slot, string name, bool currentLayer = false) {
        float loot = world.lootRarityMultiplier, traps = world.trapRarityMultiplier;
        int traveled = world.totalTraveled;
        int deepest = PlayerPrefs.GetInt("deepestlayer");
        var skills = world.body != null ? world.body.skills : null;
        string skillsJson = skills != null ? JsonConvert.SerializeObject(skills) : null;
        try {
            WithTarget(slot, name, () => Commit(() => {
                Committing = true;
                try {
                    if (currentLayer) {
                        int depth = world.biomeDepth;
                        world.biomeDepth--;
                        try { SaveSystem.SaveGame(); }
                        finally { world.biomeDepth = depth; }
                        PlayerCamera.main.ToMainMenu();
                    } else {
                        world.SaveAndExit();
                    }
                } finally { Committing = false; }
            }));
        } catch (Exception ex) {
            if (ReferenceEquals(WorldGeneration.world, world)) {
                world.lootRarityMultiplier = loot;
                world.trapRarityMultiplier = traps;
                world.totalTraveled = traveled;
                PlayerPrefs.SetInt("deepestlayer", deepest);
                if (skills != null && !string.IsNullOrEmpty(skillsJson)) {
                    try { JsonConvert.PopulateObject(skillsJson, skills); } catch { }
                }
            }
            Report("保存失败，旧存档已保留；已取消返回主菜单，请重试。", ex);
        }
    }

    internal static void WithTarget(int slot, string name, Action action) {
        if (!Enabled || Net.IsRunning) throw new InvalidOperationException("单人存档不可用。");
        Ensure();
        saveTarget = slot;
        saveName = SaveSlotStore.CleanName(name);
        try { action(); }
        finally { saveTarget = 0; saveName = null; }
    }

    internal static void Commit(Action writeAndValidate) {
        Ensure();
        if (!Enabled || Net.IsRunning || Busy) throw new InvalidOperationException("当前无法保存单人存档。");
        int slot = saveTarget != 0 ? saveTarget : active;
        if (slot == 0) throw new InvalidOperationException("请先选择存档槽位。");
        string name = saveName ?? store.Read(slot)?.meta.name ?? $"第{WorldGeneration.world?.biomeDepth ?? 1}层存档";
        Busy = true;
        bool published = false;
        string previousPath = SavesystemPatch.savedatapathreplacement;
        try {
            var meta = new SaveSlotStore.Meta {
                name = name, hostName = "单人", mode = "sp", playerCount = 1,
                modVersion = Plugin.MOD_VERSION, buildTag = KSteam.GetModBuildTag(),
                savedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                layer = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth + 1 : 1
            };
            log.l($"SP-SLOTS: saving slot={slot} name={name}");
            store.Publish(slot, meta, null, stage => {
                writeDirectory = stage;
                SavesystemPatch.savedatapathreplacement = stage;
                try {
                    writeAndValidate();
                    Validate(stage);
                    meta.layer = ReadLayer(stage);
                    log.l($"SP-SLOTS: validated slot={slot} layer={meta.layer}");
                } finally {
                    writeDirectory = null;
                    SavesystemPatch.savedatapathreplacement = previousPath;
                }
            });
            CommitSerial++;
            RememberActive(slot);
            loading = 0;
            enteredLoadingWorld = false;
            published = true;
            log.l($"SP-SLOTS: committed slot={slot} directory={store.Data(slot)}");
        } finally {
            Busy = false;
            writeDirectory = null;
            SavesystemPatch.savedatapathreplacement = published
                ? SavesystemPatch.GetTheOnePersistentDataPathWithSaveFile()
                : previousPath;
        }
        if (published) {
            try { SavesystemPatch.RefreshMainMenuSavePreview(); }
            catch (Exception ex) { log.warn("SP-SLOTS: menu preview refresh failed: " + ex.Message); }
        }
    }

    static void RememberActive(int slot) {
        active = slot;
        try { store.WriteActive(slot); }
        catch (Exception ex) { Report("存档已完成，但未能记住当前槽位；下次请重新选择。", ex); }
    }

    internal static JObject ReadRoot(string folder) =>
        JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(Path.Combine(folder, "save.sv"))));

    internal static void Validate(string folder) {
        if (!File.Exists(Path.Combine(folder, "save.sv"))) throw new InvalidDataException("槽位缺少 save.sv。");
        var root = ReadRoot(folder);
        if (root.Value<int?>("biome") == null || root["body"] == null || root["items"] == null)
            throw new InvalidDataException("单人存档数据不完整。");
    }

    internal static int ReadLayer(string folder) {
        try { return ReadRoot(folder).Value<int?>("biome") ?? 1; }
        catch { return 1; }
    }

    internal static int ReadCharacter(string folder) {
        try {
            int chr = ReadRoot(folder).Value<int?>("character") ?? 0;
            return chr >= 0 && chr <= 2 ? chr : 0;
        } catch { return 0; }
    }

    static SaveSlotStore.Meta ReadMetaFile(string file) {
        var meta = new SaveSlotStore.Meta {
            name = "", hostName = "单人", mode = "sp", playerCount = 1,
            modVersion = Plugin.MOD_VERSION, buildTag = KSteam.GetModBuildTag()
        };
        try {
            var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(file)));
            meta.layer = root.Value<int?>("biome") ?? 1;
            meta.savedAtUnix = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds();
        } catch {
            meta.layer = 1;
            meta.savedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        return meta;
    }

    static void SetSavedCharacter(int chr) {
        try {
            var prs = PreRunScript.instance;
            if (prs != null) Traverse.Create(prs).Field("savedCharacter").SetValue(chr);
        } catch (Exception ex) { log.warn("SP-SLOTS: could not set savedCharacter: " + ex.Message); }
        PreRunScript.characterSelected = chr;
    }

    internal static void Report(string message, Exception ex) {
        log.error("SP-SLOTS: " + message + (ex == null ? "" : " " + ex));
        try { SaveSlotMenu.ShowError(SaveSlotMode.SinglePlayer, message); }
        catch (Exception uiError) { log.error("SP-SLOTS: could not display modal: " + uiError); }
        try { Multiplayer.DoMultiplayerStatusMessageLog(message); }
        catch (Exception statusError) { log.error("SP-SLOTS: could not display status: " + statusError); }
    }
}

// Every single-player save opens the five-slot page. The re-entrant call is
// marked by SpSaveSlots.Committing so the vanilla method can run with the
// staging path selected by SpSaveSlots.Commit.
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SaveAndExit))]
internal static class WorldGeneration_SaveAndExit_SpSlots {
    [HarmonyPriority(Priority.First + 50)]
    static bool Prefix(WorldGeneration __instance, bool __runOriginal) {
        if (!__runOriginal) return false;
        if (!SpSaveSlots.Enabled || Net.IsRunning) return true;
        if (SavesystemPatch.PreserveSave) return true;
        if (SpSaveSlots.Committing) return true;
        if (SpSaveSlots.Busy || SaveSlotMenu.IsOpen) return false;
        SpSaveSlots.RequestSave(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.RegisterAllCommands))]
internal static class ConsoleSaveAndQuit_SpSlots {
    [HarmonyPriority(Priority.Last)]
    static void Postfix() {
        var command = ConsoleScript.SearchExact("saveandquit");
        if (command == null) return;
        var original = command.action;
        command.action = args => {
            if (!SpSaveSlots.Enabled || Net.IsRunning || SavesystemPatch.PreserveSave || !Util.IsInWorld()) { original(args); return; }
            if (WorldGeneration.world == null) return;
            if (WorldGeneration.world.biomeDepth >= 9) throw new Exception("Cannot save the game at WHERE WE MUST REACH and beyond!");
            if (Con.IsConsoleOpen()) ConsoleScript.instance.ToggleActiveState();
            SpSaveSlots.RequestSaveCurrentLayer(WorldGeneration.world);
        };
    }
}
#endif
