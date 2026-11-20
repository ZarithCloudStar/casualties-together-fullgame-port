#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Together;

internal enum EndingPhase : byte { Exploring, Cargo, Breach, Laboratory, Escape, Surgery, Escaping, Finished, Failed, Narrative }
internal enum EndingRequest : byte { Heart, Drill, Door, Surgery, SurgeryProgress, SurgeryComplete, NarrativeComplete, EntranceReady, Anesthesia }
internal enum EndingEvent : byte { Notice, Drill, Breach, Escape, Door, Surgery, Finish, HeartPickup }

// A save owns the cargo, never a transient network/body ID. This document is also
// embedded in save.sv (SaveInfo extension) and the optional world snapshot.
internal sealed class EndingSaveData {
    public int version = 1;
    public long updatedUtcTicks;
    public string cargoOwner = "";
    public bool cargoTaken, hasCargo, isMercRun, completed, guardKilled, hostUnchipped, hostMercUnlocked;
    public EndingPhase phase;
    public byte ending;
    public float secondsLeft = 60f, downSeconds;
    public int surgeryStage;
    public int surgeryMask, guardsSpawned;
    public string surgeon = ""; // Legacy save marker; no exclusive ownership.
    public List<string> surgeryOperators = new();
    public bool guardDown;
    public bool surgeryReady, timerExpired, entranceFading;
    public bool cargoUnchipped;
    public float guardDoorProgress = 5f;
    public float escapeElapsed;
    public List<string> entranceReady = new();
    public string narrativeKey = "";
    public string exitWalker = "";
    public bool exitFalling;
    public List<string> narrativeReady = new();
    public bool labEntered, labDialogueComplete;
    public bool heartSceneReady;
    public int heartSceneTravel;
    public float heartSceneX, heartSceneY;
    public bool cargoAcquiring;
    public float cargoPickupProgress;
    public List<EndingGuardSave> guards = new();
}

internal static partial class EndingFlow {
    static EndingFlow() {
        ScavPlayer.OnPlayerLeft += player => {
            if (!Active || player?.body == null) return;
            if (Key(player) == State.cargoOwner) lastCargoPosition = player.body.transform.position;
            player.body.hasCargo = false;
            if (player.body.cargo != null) player.body.cargo.SetActive(false);
        };
    }
    internal static EndingSaveData State = new();
    internal static bool Active => Util.GameIsFull() && Net.IsRunning && !Util.IsTutorialWorld();
    // Release14: Escape is a supported shipping route.
    internal static readonly bool EnableEscape = true;
    internal static ScavPlayer KeyPlayer => Players().FirstOrDefault(p => Key(p) == State.cargoOwner);
    static object session;
    static bool initialized;
    static float syncAt, heartRequestAt;
    static float stateReceivedAt;
    static Vector2 lastCargoPosition;
    static string FilePath => Path.Combine(SavesystemPatch.mpsavefolder, "ending_flow.json");
    static IEnumerable<ScavPlayer> Players() => ScavPlayer.ClientIdToPlayerDict.Values.Where(p => p != null);
    static string Key(ScavPlayer p) => p == null ? "" : p.GetPersistentId();
    static bool Stable => Active && WorldGeneration.world != null && Util.IsWorldGenerated() &&
        !WorldGeneration.world.generatingWorld && !DeferredPlayerSaveRestore.InProgress;

    internal static void ResetRun() {
        if (!Active || !Net.IsServer || CharacterReadyGate.Blocked) return;
        State = new(); initialized = true; session = Net.ServerEntityManager;
        EndingGuardSync.QueueRestore(null);
        log.l("[ending] new run; cargo and completion reset");
    }

    internal static void ClientStartingRun() {
        if (!Active || !Net.IsClient) return;
        State = new(); initialized = false; localResultStarted = false;
        log.l("[ending] client starting a run; awaiting authoritative state");
    }

    internal static void Restore(EndingSaveData saved) {
        if (!Active || !Net.IsServer) return;
        State = saved ?? new(); initialized = true; session = Net.ServerEntityManager;
        // Disk is trusted only after the enclosing save was selected by the host.
        if (State.version != 1 || !Enum.IsDefined(typeof(EndingPhase), State.phase) || State.ending > 3)
            throw new InvalidDataException("Unknown ending save state");
        State.cargoOwner ??= "";
        State.narrativeReady ??= new();
        State.entranceReady ??= new();
        State.surgeryOperators = new(); // UI participation is local to the live session.
        State.surgeon = "";
        if (State.phase == EndingPhase.Narrative) State.narrativeReady.Clear();
        if (State.entranceFading) State.entranceReady.Clear();
        cargoAcquireUntil = 0f;
        EndingGuardSync.QueueRestore(State.guards);
        log.l($"[ending] restore phase={State.phase} owner={State.cargoOwner} cargo={State.hasCargo} completed={State.completed}");
    }

    internal static EndingSaveData Capture() {
        State.isMercRun = WorldGeneration.world != null && WorldGeneration.world.isMercRun;
        if (Net.IsServer) {
            if (State.cargoAcquiring && cargoAcquireUntil > 0f)
                State.cargoPickupProgress = Mathf.Clamp01(1f - (cargoAcquireUntil - Time.time) / 5f);
            State.updatedUtcTicks = DateTime.UtcNow.Ticks;
            State.hostUnchipped = WorldGeneration.unchipped;
            if (KeyPlayer != null) State.cargoUnchipped = ChipState.IsUnchipped(KeyPlayer);
            if (DuringEscape && EndLabScript.instance?.endGuardSpawner != null) State.guardDoorProgress = EndLabScript.instance.endGuardSpawner.openProgress;
            State.hostMercUnlocked = PlayerPrefs.GetInt("didbiopod") == 1 || PlayerPrefs.GetInt("didescape") == 1;
            if (DuringEscape && !EndingGuardSync.Restoring) State.guards = EndingGuardSync.Capture();
        }
        return JsonConvert.DeserializeObject<EndingSaveData>(JsonConvert.SerializeObject(State));
    }

    internal static void Persist() {
        if (!Active || !Net.IsServer || SavesystemPatch.PreserveSave) return;
        Directory.CreateDirectory(SavesystemPatch.mpsavefolder);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(Capture(), Formatting.Indented));
        if (File.Exists(FilePath)) {
            try { File.Replace(temp, FilePath, null); }
            catch (UnauthorizedAccessException) { File.Copy(temp, FilePath, true); File.Delete(temp); }
            catch (PlatformNotSupportedException) { File.Copy(temp, FilePath, true); File.Delete(temp); }
        }
        else File.Move(temp, FilePath);
    }

    internal static void StampSave(string folder, Body body) {
        if (!Active || !Net.IsServer) return;
        string path = Path.Combine(folder, "save.sv");
        var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
        root["endingFlow"] = JObject.FromObject(Capture());
        root["hasCargo"] = body != null && body.hasCargo;
        root["cargoOwner"] = State.cargoOwner;
        File.WriteAllBytes(path, SaveSystem.Zip(root.ToString(Formatting.None)));
        Persist();
    }

    internal static void RestoreSave(JObject data) {
        EndingSaveData saved = data["endingFlow"]?.ToObject<EndingSaveData>();
        // Sidecar records transfers/completion after the most recent checkpoint.
        if (File.Exists(FilePath)) {
            var live = JsonConvert.DeserializeObject<EndingSaveData>(File.ReadAllText(FilePath));
            if (live != null && (saved == null || live.updatedUtcTicks > saved.updatedUtcTicks)) saved = live;
        }
        if (saved == null && data.Value<int>("biome") >= 10) {
            saved = new EndingSaveData { cargoTaken = true, hasCargo = true, cargoOwner = Key(ScavPlayer.LocalPlayer),
                phase = data.Value<int>("biome") == 11 ? EndingPhase.Laboratory : EndingPhase.Cargo };
            log.l("[cargo] migrated legacy ending-layer save without an owner; cargo assigned to host");
        }
        Restore(saved);
    }

    internal static void SendTo(knetid clientId) => Publish(new[] { clientId });

    static void Publish(IEnumerable<knetid> clients = null) {
        if (!Net.IsServer) return;
        var writer = Net.CreateWriter(NetmsgId.CLIENT_EndingState);
        writer.Put(JsonConvert.SerializeObject(Capture()));
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clients ?? ServerMain.AllClientIdsExceptHost);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_EndingState)]
    static void ReceiveState(knetid _, NetDataReader reader) {
        var data = JsonConvert.DeserializeObject<EndingSaveData>(reader.GetString(32768));
        if (data == null || data.version != 1 || data.ending > 3 || !Enum.IsDefined(typeof(EndingPhase), data.phase)) return;
        bool changed = State.cargoOwner != data.cargoOwner || State.phase != data.phase;
        State = data; initialized = true; stateReceivedAt = Time.time;
        if (changed) log.l($"[ending] received phase={State.phase} owner={State.cargoOwner} cargo={State.hasCargo}");
        ApplyCargo();
    }

    static void ApplyCargo() {
        EndingWorldObjects.Apply();
        ApplyHeartPickup();
        foreach (var p in Players()) {
            if (p.body == null) continue;
            bool owns = State.hasCargo && Key(p) == State.cargoOwner;
            p.body.hasCargo = owns;
            if (p.body.cargo != null && p.body.cargo.activeSelf != owns) p.body.cargo.SetActive(owns);
        }
        if (State.cargoTaken && !State.cargoAcquiring && BlueHeart.main != null) Object.Destroy(BlueHeart.main.gameObject);
    }

    static void Changed(string message) {
        log.l(message); ApplyCargo(); Publish();
        try { Persist(); } catch (Exception ex) { log.error($"[ending] could not persist live state: {ex}"); }
    }

    internal static void Request(EndingRequest action, Vector2 position, int value = 0) {
        if (!Active) return;
        if (Net.IsServer) { Handle(ScavPlayer.LocalPlayer, action, position, value); return; }
        var writer = Net.CreateWriter(NetmsgId.SERVER_EndingRequest);
        writer.Put((byte)action); writer.Put(position.x); writer.Put(position.y); writer.Put(value);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_EndingRequest)]
    static void ReceiveRequest(knetid sender, NetDataReader reader) {
        var action = (EndingRequest)reader.GetByte();
        var position = new Vector2(reader.GetFloat(), reader.GetFloat());
        int value = reader.GetInt();
        if (!ScavPlayer.TryGetPlayerFromClientId(sender, out var p) || p == null ||
            p.ServerPlayerState == null || !p.ServerPlayerState.Cooldown("ending_" + action, .2f)) return;
        Handle(p, action, position, value);
    }

    static void Handle(ScavPlayer p, EndingRequest action, Vector2 position, int value) {
        // Dead/spectating participants must also be able to finish reading.
        if (Stable && p != null && action == EndingRequest.NarrativeComplete) {
            NarrativeComplete(p, value); return;
        }
        if (Stable && p != null && action == EndingRequest.EntranceReady) {
            if (State.phase == EndingPhase.Escape && State.entranceFading && !State.entranceReady.Contains(Key(p))) {
                State.entranceReady.Add(Key(p));
                Changed($"[escape] entrance fade complete player={Key(p)}");
            }
            return;
        }
        if (!Stable || p?.body == null || !p.IsAlive() || !p.IsConscious() ||
            State.phase == EndingPhase.Finished || State.phase == EndingPhase.Failed) return;
        if (action == EndingRequest.Heart) {
            if (State.cargoTaken) { Notice(p, "ending_cargo_taken"); return; }
            var heart = BlueHeart.main;
            if (WorldGeneration.GetRunSettingBool("endless") || WorldGeneration.world.biomeDepth != 9 ||
                heart == null || Vector2.Distance(p.pos, heart.transform.position) > 10f || !p.body.standing) {
                Notice(p, "ending_invalid_interaction"); return;
            }
            State.cargoTaken = State.cargoAcquiring = true;
            State.hasCargo = false; State.cargoPickupProgress = 0f;
            State.cargoOwner = Key(p); State.phase = EndingPhase.Cargo;
            cargoAcquireUntil = Time.time + 5f;
            lastCargoPosition = p.pos;
            Changed($"[cargo] pickup reserved owner={State.cargoOwner}; playing one authoritative 5s sequence");
            BroadcastEvent(EndingEvent.HeartPickup);
            return;
        }
        HandleFlow(p, action, position, value);
    }

    static partial void HandleFlow(ScavPlayer p, EndingRequest action, Vector2 position, int value);
    static partial void TickFlow();
    static partial void ApplyFlow();

    static void Notice(ScavPlayer p, string key) {
        log.l($"[ending] denied player={Key(p)} reason={key}");
        if (p == null || p.IsLocal) ShowNotice(key);
        else {
            var w = Net.CreateWriter(NetmsgId.CLIENT_EndingEvent);
            w.Put((byte)EndingEvent.Notice); w.Put(key);
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, w, p.playerId);
        }
    }
    static readonly Dictionary<string, float> noticeAt = new();
    static void ShowNotice(string key) {
        if (string.IsNullOrEmpty(key) || PlayerCamera.main == null) return;
        if (noticeAt.TryGetValue(key, out float next) && Time.unscaledTime < next) return;
        noticeAt[key] = Time.unscaledTime + 3f;
        PlayerCamera.main.DoAlert(Lang.Get(key, true) ?? key);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_EndingEvent)]
    static void ReceiveEvent(knetid _, NetDataReader reader) {
        var kind = (EndingEvent)reader.GetByte();
        if (kind == EndingEvent.Notice) ShowNotice(reader.GetString(256));
        else ReceiveFlowEvent(kind, reader);
    }
    static partial void ReceiveFlowEvent(EndingEvent kind, NetDataReader reader);

    internal static void Tick() {
        if (!Active) { initialized = false; session = null; return; }
        if (Net.IsServer && !ReferenceEquals(session, Net.ServerEntityManager)) {
            session = Net.ServerEntityManager; initialized = false; State = new();
        }
        if (!Stable) return;
        if (!initialized && Net.IsServer) { initialized = true; State = new(); }
        if (!initialized) return;
        if (Net.IsServer && (State.hasCargo || State.cargoAcquiring || DuringEscape) && !SettlementLocked && State.phase != EndingPhase.Failed) {
            var owner = KeyPlayer;
            if (owner?.body != null) lastCargoPosition = owner.body.transform.position;
            if (owner == null || (owner.body != null && !owner.IsAlive())) {
                if (owner != null && (State.phase == EndingPhase.Escape || State.phase == EndingPhase.Surgery || State.phase == EndingPhase.Escaping))
                    CargoFailedInEscape();
                else {
                    var next = Players().Where(p => p.IsAlive() && p != owner)
                        .OrderBy(p => (p.pos - lastCargoPosition).sqrMagnitude).ThenBy(p => Key(p), StringComparer.Ordinal).FirstOrDefault();
                    if (next != null) {
                        State.cargoOwner = Key(next);
                        Changed($"[cargo] transferred owner={State.cargoOwner} reason=death/disconnect");
                    } else { State.hasCargo = State.cargoAcquiring = false; State.phase = EndingPhase.Failed; Changed("[ending] party failed; no living cargo successor, no ending awarded"); }
                }
            }
        }
        ApplyCargo(); ApplyFlow(); ApplyMercWorld();
        if (Net.IsServer) {
            TickHeartPickup();
            TickFlow();
            if (Time.unscaledTime >= syncAt) { syncAt = Time.unscaledTime + 1f; Publish(); }
        }
    }
    static partial void CargoFailedInEscape();

    internal static bool HeartUpdate(BlueHeart heart) {
        if (!Active || WorldGeneration.GetRunSettingBool("endless")) return true;
        if (Stable) TickHeartVisuals(heart);
        if (Stable && !State.cargoTaken && ScavPlayer.LocalPlayer?.body != null &&
            ScavPlayer.LocalPlayer.body.standing && Vector2.Distance(ScavPlayer.LocalPlayer.pos, heart.transform.position) < 10f &&
            Time.unscaledTime >= heartRequestAt) {
            heartRequestAt = Time.unscaledTime + 1f;
            Request(EndingRequest.Heart, heart.transform.position);
        }
        // Visuals above preserve vanilla Update; only the server can reserve
        // pickup and commit EmbedCargo after the presentation.
        return false;
    }
}

[HarmonyPatch(typeof(BlueHeart), "Update")]
internal static class BlueHeart_EndingPatch {
    static bool Prefix(BlueHeart __instance) => EndingFlow.HeartUpdate(__instance);
}
#endif
