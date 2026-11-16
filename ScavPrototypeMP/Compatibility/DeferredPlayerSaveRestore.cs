#if FULLGAME
using CasualtiesTogetherUtils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

internal static class DeferredPlayerSaveRestore {
    static int pendingRestores;
    internal static bool InProgress => pendingRestores > 0;
    internal static void Queue(string folder, bool midlevel) {
        var world = WorldGeneration.world;
        if (!Util.GameIsFull() || !Net.IsServer || world == null) return;
        // The old immediate body load also supplied these fields before worldgen.
        // Keep that part early, but never load a Body/inventory on this pass.
        var data = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(Path.Combine(folder, "save.sv"))));
        EndingFlow.RestoreSave(data);
        RecipeProgress.RestoreShared(data);
        world.biomeDepth = data.Value<int>("biome"); // already normalized by the writer; no +1
        world.totalTraveled = data.Value<int>("totalTraveled");
        world.lootRarityMultiplier = data.Value<float>("lootRarity");
        world.trapRarityMultiplier = data.Value<float>("trapRarity");
        world.endlessLoops = data.Value<int>("endlessLoops");
        world.isMercRun = data.Value<bool>("isMercRun") || EndingFlow.State.isMercRun;
        WorldGeneration.runSettings = SaveSystem.TupleListToDic(data["runSettings"].ToObject<List<(string, string)>>());
        SaveSystem.savedRunTime = data.Value<float>("runTime");
        PlayerCamera.main.caloriesConsumed = data.Value<int>("caloriesConsumed");
        WoundView.view.SetCharDetails(data.Value<int>("cHeight"), data.Value<int>("cAge"), data.Value<int>("cId"), data.Value<int>("cVer"));
        if (data.Value<bool>("cheatedSave")) SteamManager.disableAchievements = true;
        PlayerPlacementCheckpoint.Load(folder, midlevel);
        log.l($"LOAD: queued player restore midlevel={midlevel} layer={world.biomeDepth}; progress loaded, bodies deferred");
        Util.StartCoroutine(RestoreAfterGeneration(world, Net.ServerEntityManager, folder));
    }

    internal static IEnumerator RestoreAfterGeneration(WorldGeneration world, object session, string folder) {
        pendingRestores++;
        bool finished = false;
        bool slotRestored = false, restoreFailed = false;
        Action finish = () => finished = true;
        WorldMP.OnWorldgenFinish += finish;
        try {
            double started = Time.realtimeSinceStartupAsDouble;
            while (!finished || !Util.IsWorldGenerated() || !Body_PlaceBody_MultiplayerPatch.has_spawn_location) {
                if (!Current(world, session)) { log.warn("LOAD: deferred restore cancelled; world/session changed"); yield break; }
                if (Time.realtimeSinceStartupAsDouble - started > 180) { log.error("LOAD: timed out waiting for world generation/PlaceBody"); yield break; }
                yield return null;
            }
            // FinishWorldGeneration performs revival/re-registration/creation
            // before this event. Let new Body.Start callbacks finish as well.
            yield return null;
            if (!Current(world, session)) yield break;
            log.l($"LOAD: world generated and placed layer={world.biomeDepth}; restoring players");
            try { SharedMain.CreatePlayerCharacters(); }
            catch (Exception ex) { log.error($"LOAD: creating deferred bodies failed: {ex}"); yield break; }
            yield return null;
            var waiting = ScavPlayer.ClientIdToPlayerDict.Values.Where(p => p != null).ToList();
            var readyBodies = new Dictionary<ScavPlayer, Body>();
            double bodyDeadline = Time.realtimeSinceStartupAsDouble + 30;
            while (waiting.Count > 0 && Current(world, session)) {
                foreach (var player in waiting.ToArray()) {
                    if (player == null || !ScavPlayer.ClientIdToPlayerDict.TryGetValue(player.playerId, out var current) || !ReferenceEquals(player, current)) {
                        waiting.Remove(player);
                        log.warn("LOAD: player disconnected while waiting for a body");
                        continue;
                    }
                    if (player.body == null || player.BasePlayer == null) continue;
                    // A late body may have appeared this frame. Its Start and
                    // initial placement must settle before applying saved gear.
                    if (!readyBodies.TryGetValue(player, out var ready) || !ReferenceEquals(ready, player.body)) {
                        readyBodies[player] = player.body;
                        continue;
                    }
                    waiting.Remove(player); // one attempt: never duplicate items after a partial failure
                    try {
                        string path = Path.Combine(folder, player.GetPersistentId(), "save.sv");
                        if (!File.Exists(path)) { log.warn($"LOAD: player save missing player={player.playerName} path={path}"); continue; }
                        if (player.ServerPlayerState != null && player.ServerPlayerState.loaded_save) {
                            PlayerPlacementCheckpoint.TryApply(player);
                            log.l($"LOAD: player already restored player={player.playerName}");
                            continue;
                        }
                        // Reuses force-load, world snapshot, gear registration,
                        // current spawn position and per-player crafted state.
                        if (!PlayerCheckpoint.TryLoadBodyOnly(player)) throw new IOException("Body checkpoint restore returned false.");
                        if (player.body.TryGetNetBody(out var nb)) {
                            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
                            nb.Server_RemindPlayersCurrentState();
                        }
                        if (PlayerPlacementCheckpoint.TryApply(player)) {
                            var spawnPos = player.body.transform.position;
                            if (player.body.TryGetNetBody(out var movedBody)) {
                                movedBody.Server_RemindPlayersCurrentState();
                                MedicalSync.Server_QueueSendCharacterHealth(movedBody, true);
                            }
                            log.l($"[spawn] midlevel restore player={player.playerName} saved={spawnPos.x:F3},{spawnPos.y:F3}");
                        }
                        int filled = 0;
                        try {
                            if (player.body != null && player.body.slots != null) {
                                for (int slot = 0; slot < player.body.slots.Length; slot++) {
                                    if (player.body.HoldingItem(slot)) filled++;
                                }
                            }
                        } catch (Exception slotError) { log.warn($"LOAD: could not count slots player={player.playerName}: {slotError.Message}"); }
                        log.l($"LOAD: restored player={player.playerName} generated={Util.IsWorldGenerated()} layer={world.biomeDepth} filledSlots={filled} path={path}");
                    } catch (Exception ex) { restoreFailed = true; log.error($"LOAD: deferred player restore failed player={player.playerName}: {ex}"); }
                }
                if (waiting.Count == 0) break;
                if (Time.realtimeSinceStartupAsDouble >= bodyDeadline) {
                    foreach (var player in waiting) log.error($"LOAD: timed out waiting for body player={player.playerName}");
                    break;
                }
                yield return null;
            }
            slotRestored = Current(world, session) && waiting.Count == 0 && !restoreFailed && !WorldSnapshotTransfer.Failed;
        } finally {
            WorldMP.OnWorldgenFinish -= finish; pendingRestores--;
            SaveSlots.LoadCompleted(slotRestored);
        }
    }

    static bool Current(WorldGeneration world, object session) => Net.IsRunning && Net.IsServer &&
        world != null && ReferenceEquals(world, WorldGeneration.world) && ReferenceEquals(session, Net.ServerEntityManager);
}
#endif
