#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace Together;

internal static class SaveCurrentLayerExit {
    internal static bool IsSaving { get; private set; }
    static string Text(string key, string fallback) => Lang.Get(key, true) ?? fallback;

    // Debug-only hook (--mp-saveexit-after N): fires the same Request() once the
    // host has been in a generated world for N seconds. Used by automated tests;
    // it never runs without the argument.
    static float testDelay = -1f, testStart = -1f;
    static bool testDone;

    internal static void ParseTestDelay(string value) {
        if (float.TryParse(value, out float seconds) && seconds >= 0f && seconds <= 3600f) {
            testDelay = seconds;
            log.l($"SAVE-EXIT: test hook armed after {seconds}s");
        } else {
            log.warn($"SAVE-EXIT: ignoring invalid --mp-saveexit-after '{value ?? "<missing>"}'");
        }
    }

    internal static void TickTestHook() {
        if (testDelay < 0f) return;
        if (!Util.GameIsFull() || !Net.IsServer || !Util.IsInWorld() || !Util.IsWorldGenerated()) {
            // Leaving the world re-arms the hook, so a save -> load -> save cycle
            // produces two comparable snapshots in one run.
            testStart = -1f;
            return;
        }
        if (WorldGeneration.world == null) return;
        if (testStart < 0f) { testStart = Time.realtimeSinceStartup; return; }
        if (Time.realtimeSinceStartup - testStart < testDelay) return;
        testStart = Time.realtimeSinceStartup;
        log.l($"SAVE-EXIT: test hook firing after {testDelay}s");
        Request();
    }

    // Second debug hook (--mp-loadrun-after N): in the main menu, press the game's
    // own "continue" entry after N seconds. Used by automated load tests only.
    static float loadTestDelay = -1f, loadTestStart = -1f;
    static bool loadTestDone;

    internal static void ParseLoadTestDelay(string value) {
        if (float.TryParse(value, out float seconds) && seconds >= 0f && seconds <= 600f) {
            loadTestDelay = seconds;
            log.l($"SAVE-EXIT: load test hook armed after {seconds}s");
        } else {
            log.warn($"SAVE-EXIT: ignoring invalid --mp-loadrun-after '{value ?? "<missing>"}'");
        }
    }

    internal static void TickLoadTestHook() {
        if (loadTestDelay < 0f || loadTestDone) return;
        if (!Util.GameIsFull() || !Net.IsServer || Util.IsInWorld()) return;
        if (PreRunScript.instance == null || !SavesystemPatch.HasMultiplayerSaveFile()) return;
        if (loadTestStart < 0f) { loadTestStart = Time.realtimeSinceStartup; return; }
        if (Time.realtimeSinceStartup - loadTestStart < loadTestDelay) return;
        loadTestDone = true;
        log.l($"SAVE-EXIT: load test hook firing after {loadTestDelay}s");
        PreRunScript.instance.LoadRun();
    }

    internal static void DrawButton(float height) {
        if (!Util.GameIsFull() || !Net.IsRunning || !Net.IsServer || !Util.IsInWorld() || Util.IsTutorialWorld()) return;
        bool enabled = GUI.enabled;
        try {
            GUI.enabled = enabled && !IsSaving;
            if (GUILayout.Button(new GUIContent(Text("save_current_exit_button", "Save and exit (current layer)"),
                Text("save_current_exit_hint", "Continue from the beginning of this layer when you load.")), GUILayout.Height(height))) Request();
        } finally { GUI.enabled = enabled; }
    }

    static bool Stable(WorldGeneration world) => Net.IsRunning && Net.IsServer && world != null &&
        ReferenceEquals(WorldGeneration.world, world) && Util.IsWorldGenerated() && world.worldExists &&
        !Util.IsTutorialWorld() && !world.generatingWorld && !Traverse.Create(world).Field("doingRegen").GetValue<bool>() && !SavesystemPatch.PreserveSave;

    internal static bool Request() {
        if (!Util.GameIsFull()) return false;
        try {
            if (!Net.IsRunning || !Net.IsServer) {
                Multiplayer.DoMultiplayerStatusMessageLog(Text("save_current_exit_host_only", "Only the host can save."));
                return false;
            }
            if (IsSaving) {
                Multiplayer.DoMultiplayerStatusMessageLog(Text("save_current_exit_busy", "Saving…"));
                return false;
            }
            var world = WorldGeneration.world;
            if (!Stable(world) || PlayerCamera.main == null) {
                Multiplayer.DoMultiplayerStatusMessageLog(Text("save_current_exit_unavailable", "Wait until the world has finished loading and changing layers."));
                return false;
            }
            if (SaveSlots.Enabled) {
                if (SaveSlotMenu.IsOpen || SaveSlots.Busy) return false;
                SaveSlotMenu.Open(false, (slot, name) => {
                    if (!Stable(world) || PlayerCamera.main == null) return;
                    StartSave(world, slot, name);
                });
                return true;
            }
            StartSave(world, 0, null);
            return true;
        } catch (Exception ex) { Fail(ex); return false; }
    }

    static void StartSave(WorldGeneration world, int slot, string name) {
        IsSaving = true; // latch before the coroutine's first yield
        try { Util.StartCoroutine(Save(world, world.biomeDepth, slot, name)); }
        catch { IsSaving = false; throw; }
    }

    static IEnumerator Save(WorldGeneration world, int layer, int slot, string name) {
        // Leave the button event before any file IO or scene transition.
        // All writes below then run in one main-thread turn, without yielding
        // between players or leaving a substituted camera body across frames.
        try {
            log.l($"SAVE-EXIT: begin layer={layer}");
            Multiplayer.DoMultiplayerStatusMessageLog(Text("save_current_exit_busy", "Saving…"));
            yield return null;
            bool success = false;
            try {
                if (SaveSlots.Enabled) SaveSlots.WithTarget(slot, name, () => SaveSlots.Commit(() => WriteAndValidate(world, layer)));
                else WriteAndValidate(world, layer);
                success = true;
            }
            catch (Exception ex) { Fail(ex); }
            if (!success) yield break;
            try {
                Multiplayer.DoMultiplayerStatusMessageLog(Text("save_current_exit_success", "Saved. Returning to the lobby."));
                log.l($"SAVE-EXIT: success layer={layer}; returning to lobby");
                PlayerCamera.main.ToMainMenu(); // existing reliable client announcement
            } catch (Exception ex) { SaveSlots.Report("存档已保存，但返回大厅失败；可稍后手动返回。", ex); }
        } finally { IsSaving = false; }
    }

    internal static void WriteAndValidate(WorldGeneration world, int layer) {
        if (!Stable(world) || world.biomeDepth != layer || PlayerCamera.main == null) throw new InvalidOperationException("World/session changed before saving.");
        var players = ScavPlayer.ClientIdToPlayerDict.Values.Where(p => p != null).ToArray();
        if (DeferredPlayerSaveRestore.InProgress || players.Any(p => p.body == null)) throw new InvalidOperationException("玩家角色尚未完成恢复。");
        var host = players.SingleOrDefault(p => p.IsLocal);
        if (host == null) throw new InvalidOperationException("A local host body is required for the root save.");
        var bodies = players.Select(p => p.body).ToArray();
        string root = SavesystemPatch.mpsavefolder;
        string rules = Path.Combine(root, SavesystemPatch.mpsaveheaderfile);
        string rootSave = Path.Combine(root, "save.sv");
        var rootBefore = Stamp.Read(rootSave);
        var rulesBefore = Stamp.Read(rules);
        // Serialize the complete supported object set before writing any snapshot.
        try {
            string tilesPath = Path.Combine(root, "world_tiles.bin");
            string fluidPath = Path.Combine(root, "world_fluid.bin");
            string objectsPath = Path.Combine(root, "world_objects.bin");
            byte[] tiles = SavesystemPatch.SerializeWorldBlocks();
            byte[] fluids = SavesystemPatch.SerializeWorldFluids();
            byte[] objects = SavesystemPatch.SerializeWorldObjects();
            // A failed full-layer capture must not turn an older checkpoint's
            // header into a current-layer save before any world data was written.
            PlayerCheckpoint.RunChecked(() => PlayerCheckpoint.WriteRules(true));
            Fresh(rules, rulesBefore, false);
            if (JObject.Parse(File.ReadAllText(rules)).Value<bool?>("LEVEL") != true)
                throw new IOException("Rules header is not a current-layer save.");
            File.WriteAllBytes(tilesPath, tiles);
            File.WriteAllBytes(fluidPath, fluids);
            File.WriteAllBytes(objectsPath, objects);
            byte[] objectsOnDisk = File.ReadAllBytes(objectsPath);
            if (!objects.SequenceEqual(objectsOnDisk)) throw new IOException("World object snapshot verification failed.");
            int objectCount = WorldObjectSnapshot.Validate(objectsOnDisk);
            log.l($"SAVE-EXIT: world objects written count={objectCount} bytes={objectsOnDisk.Length} hash={Hash(objectsOnDisk)}");
            long tilesBytes = new FileInfo(tilesPath).Length;
            long fluidBytes = new FileInfo(fluidPath).Length;
            if (tilesBytes <= 0 || fluidBytes <= 0) throw new IOException("World snapshot is empty.");
            string tilesHash = Hash(tiles);
            string fluidHash = Hash(fluids);
            log.l($"SAVE-EXIT: world snapshot written tiles={tilesBytes} hash={tilesHash} fluids={fluidBytes} hash={fluidHash}");
        } catch (Exception worldEx) {
            throw new IOException("World snapshot failed", worldEx);
        }
        // Only online players are visited. Offline folders and character_choices.txt
        // are never enumerated or changed. Host last supplies the root save.
        foreach (var player in players.OrderBy(p => p.IsLocal)) {
            if (!Stable(world) || world.biomeDepth != layer || !ReferenceEquals(player.body, bodies[Array.IndexOf(players, player)]))
                throw new InvalidOperationException("World or player body changed during saving.");
            string path = Path.Combine(root, player.GetPersistentId(), "save.sv");
            var before = Stamp.Read(path);
            PlayerCheckpoint.RunChecked(() => PlayerCheckpoint.WriteOne(player, false));
            var json = Fresh(path, before, true);
            if (json.Value<int?>("biome") != layer) throw new IOException("Player checkpoint has the wrong layer: " + path);
            log.l($"SAVE-EXIT: player written name={player.playerName} layer={layer} bytes={new FileInfo(path).Length}");
        }
        var saved = Fresh(rootSave, rootBefore, true);
        if (saved.Value<int?>("biome") != layer || !File.ReadAllBytes(rootSave).SequenceEqual(
            File.ReadAllBytes(Path.Combine(root, host.GetPersistentId(), "save.sv"))))
            throw new IOException("Root save does not match the host checkpoint.");
        if (!Stable(world) || world.biomeDepth != layer) throw new InvalidOperationException("Session changed before leaving.");
    }

    static string Hash(byte[] data) {
        using (var sha = SHA256.Create()) {
            string hex = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
            return hex.Length >= 16 ? hex.Substring(0, 16) : hex;
        }
    }

    sealed class Stamp {
        internal bool Exists;
        internal DateTime Time;
        internal long Size;
        internal byte[] Hash;
        internal static Stamp Read(string path) {
            var file = new FileInfo(path);
            if (!file.Exists) return new Stamp();
            using var hash = SHA256.Create();
            return new Stamp { Exists = true, Time = file.LastWriteTimeUtc, Size = file.Length, Hash = hash.ComputeHash(File.ReadAllBytes(path)) };
        }
    }

    static JObject Fresh(string path, Stamp before, bool compressed) {
        var after = Stamp.Read(path);
        if (!after.Exists || after.Size == 0 || (before.Exists && after.Time == before.Time && after.Size == before.Size && after.Hash.SequenceEqual(before.Hash)))
            throw new IOException("Checkpoint was not refreshed: " + path);
        return JObject.Parse(compressed ? SaveSystem.Unzip(File.ReadAllBytes(path)) : File.ReadAllText(path));
    }

    static void Fail(Exception ex) {
        log.error($"SAVE-EXIT: failed; automatic return cancelled: {ex}");
        SaveSlotMenu.ShowError("保存失败，旧存档已保留；已取消返回大厅，请重试。");
        try { Multiplayer.DoMultiplayerStatusMessageLog(Text("save_current_exit_failed", "Save failed. Staying in the game.")); }
        catch (Exception statusError) { log.error($"SAVE-EXIT: failure status could not be displayed: {statusError}"); }
    }
}
#endif
