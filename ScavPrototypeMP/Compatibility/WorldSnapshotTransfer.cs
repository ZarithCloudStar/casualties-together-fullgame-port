#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace Together;

// WS1: explicit generation mode, bounded application fragments, whole-transfer
// digest and an application ACK. Transport retransmission is ReliableOrdered.
internal static class WorldSnapshotTransfer {
    internal const int Protocol = 1, FragmentBytes = 1000, MaxBytes = 32 * 1024 * 1024;
    const float Timeout = 120;
    internal static bool SnapshotMode { get; private set; }
    internal static bool Applied { get; private set; }
    internal static bool Failed { get; private set; }
    internal static bool Applying { get; private set; }
    internal static bool BaselineEntities;
    static bool awaitingMode;
    internal static bool BlockClientObjects => Net.IsClient && (awaitingMode || (SnapshotMode && !Applied));
    internal static void AwaitGenerationMode() { awaitingMode = true; }
    internal static string Token { get; private set; }
    static byte[][] published, received;
    static byte[] incoming;
    static int sequence, offset, expectedChunks, expectedBytes, saveId;
    static uint width, height;
    static int biome;
    static string digest;
    static float lastProgress, began;
    static object session;
    static WorldGeneration loadingWorld;
    static object loadingOwner;
    static string loadingToken, loadingPhase;
    static float phaseStarted, nextLoadingText;
    static bool clientLoading, requestedAgain;
    static readonly Dictionary<knetid, string> acknowledged = new();
    static readonly HashSet<knetid> sending = new();
    static readonly HashSet<knetid> rejected = new();
    static readonly Dictionary<knetid, string> expectedAck = new();
    static readonly Dictionary<int, GameObject> clientObjects = new();
    static int nextKey = 60000;

    internal static object Session => Net.IsServer ? (object)Net.ServerEntityManager : Net.ClientEntityManager;
    internal static void Reset(bool snapshot, string token = null) {
        SnapshotMode = snapshot; Token = token ?? Guid.NewGuid().ToString("N");
        awaitingMode = false;
        Applied = Failed = Applying = BaselineEntities = false;
        clientLoading = requestedAgain = false;
        loadingWorld = null; loadingOwner = null; loadingToken = loadingPhase = null;
        published = received = null; incoming = null;
        sequence = offset = 0; nextKey = 60000;
        lastProgress = began = Time.realtimeSinceStartup; session = Session;
        acknowledged.Clear(); sending.Clear(); rejected.Clear(); expectedAck.Clear(); clientObjects.Clear();
    }
    internal static int Key(GameObject go) {
        var marker = go.GetComponent<WorldSnapshotRestored>() ?? go.AddComponent<WorldSnapshotRestored>();
        if (marker.networkKey == 0) marker.networkKey = ++nextKey;
        return marker.networkKey;
    }
    internal static void Track(GameObject go, int key) {
        (go.GetComponent<WorldSnapshotRestored>() ?? go.AddComponent<WorldSnapshotRestored>()).networkKey = key;
        if (Net.IsClient && !clientObjects.TryAdd(key, go)) throw new InvalidDataException("Duplicate checkpoint network key.");
    }
    internal static GameObject Take(int key, string resource) {
        if (!Net.IsClient || key == 0 || !clientObjects.TryGetValue(key, out var go)) return null;
        clientObjects.Remove(key);
        if (go == null) return null;
        if (SyncInfo.GetItemID(go) != resource) { Fail("Checkpoint entity resource mismatch."); return null; }
        return go;
    }
    internal static void MarkNetworkInstance(GameObject go, int key) {
        if (key != 0) (go.GetComponent<WorldSnapshotRestored>() ?? go.AddComponent<WorldSnapshotRestored>()).networkKey = key;
    }
    internal static void Adopt(SyncInfo sync) {
        if (!Net.IsClient || !SnapshotMode || !Applied || sync.checkpointKey.Value == 0 ||
            !clientObjects.TryGetValue(sync.checkpointKey.Value, out var saved) || saved == null) return;
        var prior = sync.go;
        if (prior == saved) { clientObjects.Remove(sync.checkpointKey.Value); return; }
        var adopted = Take(sync.checkpointKey.Value, sync.resourceId.Value);
        if (adopted == null) return;
        // The key can arrive after an early LES construct or a baseline. Replace
        // that provisional mesh, retaining the entity and the exact saved root.
        if (prior != null) {
            if (prior.TryGetComponent<SyncInfoGameObjectTracker>(out var tracker)) tracker.syncInfo = null;
            NetObjectRegistry.SyncRegistry.Remove(prior);
            prior.SetActive(false); UnityEngine.Object.Destroy(prior);
        }
        sync.go = adopted; sync.tracker = null;
    }
    internal static bool SpawnAllowed(knetid id) => !Failed && (!SnapshotMode || (Applied && acknowledged.TryGetValue(id, out var token) && token == Token));
    internal static void Fail(string reason) {
        SessionLog.Event("WORLD SNAPSHOT FAILED", reason);
        if (Failed) return;
        Failed = true; Applying = false; incoming = null; received = null;
        clientLoading = false;
#if TESTING
        WorldCheckpointRegression.RecordFailure(reason);
#endif
        string message = "当前层快照同步失败，已中止进入世界；请重新连接重试，并确认双方使用同一个安装包。 / Checkpoint sync aborted; reconnect to retry: " + reason;
        SessionLogGame.WorldSnapshot("WORLD-SNAPSHOT: " + message, true);
        if (WorldGeneration.world != null && WorldGeneration.world.loadingText != null) WorldGeneration.world.loadingText.text = message;
        if (Net.IsClient) {
            try {
                var w = Net.CreateWriter(NetmsgId.SERVER_WorldSnapshot);
                w.Put((byte)2); w.Put(Token ?? ""); w.Put(reason.Length > 400 ? reason.Substring(0, 400) : reason);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
            } catch (Exception ex) { log.warn("WORLD-SNAPSHOT: could not send rejection: " + ex.Message); }
        } else if (Net.IsServer && SnapshotMode) {
            foreach (var id in ServerMain.AllClientIdsExceptHost) AbortPeer(id, reason);
        }
        try { if (PlayerCamera.main != null) PlayerCamera.main.ToMainMenu(); }
        catch (Exception ex) { log.warn("WORLD-SNAPSHOT: failed to leave loading scene: " + ex); }
        // ToMainMenu/disconnect also write a status; keep the actual failure visible.
        Multiplayer.DoMultiplayerStatusMessageError(message);
    }
    static void AbortPeer(knetid id, string reason) {
        acknowledged.Remove(id);
        var w = Net.CreateWriter(NetmsgId.CLIENT_WorldSnapshot);
        w.Put((byte)3); w.Put(Token ?? ""); w.Put(reason.Length > 400 ? reason.Substring(0, 400) : reason);
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, w, id);
    }
    internal static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
    internal static byte[] Inflate(byte[] compressed, int cells) {
        if (compressed == null || compressed.Length == 0 || compressed.Length > MaxBytes) throw new InvalidDataException("Missing/oversized terrain snapshot.");
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var bytes = new byte[cells]; int count = 0, n;
        while (count < cells && (n = gzip.Read(bytes, count, cells - count)) > 0) count += n;
        if (count != cells || gzip.ReadByte() != -1) throw new InvalidDataException("Terrain snapshot length mismatch.");
        return bytes;
    }
    internal static void Preflight(byte[][] parts) {
        var world = WorldGeneration.world;
        int cells = checked((int)(world.width * world.height));
        var tiles = Inflate(parts[0], cells); var fluids = Inflate(parts[1], cells);
        if (tiles.Any(t => t >= world.tiles.Length)) throw new InvalidDataException("Unknown terrain tile template.");
        if (fluids.Any(f => !FluidManager.WorldFluidToLiquidID.ContainsKey(f))) throw new InvalidDataException("Unknown world fluid template.");
        WorldObjectSnapshot.PreflightRestore(parts[2]);
        if (Net.IsClient) {
            var doc = WorldObjectSnapshotData.Decode(parts[2], world.biomeDepth, world.width, world.height, WoundView.view.cInfo[2]);
            var keys = doc.objects.Select(o => o.networkKey).Concat(doc.backgrounds.Select(b => b.networkKey)).ToArray();
            if (doc.version != 3 || keys.Any(k => k == 0) || keys.Distinct().Count() != keys.Length)
                throw new InvalidDataException("WS1 requires a complete v3 checkpoint with unique entity identities.");
        }
    }
    internal static void RefreshForLateJoin() {
        if (!SnapshotMode || !Applied || !Util.IsWorldGenerated()) return;
        published = new[] { SavesystemPatch.SerializeWorldBlocks(), SavesystemPatch.SerializeWorldFluids(), WorldObjectSnapshot.Serialize() };
        Preflight(published);
#if TESTING
        WorldCheckpointRegression.RecordApplied(published);
#endif
        // Newly created gameplay objects also need their fresh key in LES.
        foreach (var pair in NetObjectRegistry.SyncRegistry.ToArray())
            if (pair.Key != null && pair.Value != null && !pair.Value.IsDestroyed) pair.Value.WriteObjectIntoPacket();
        SessionLogGame.WorldSnapshot("WORLD-SNAPSHOT: refreshed live checkpoint for late join");
    }
    internal static void Publish() {
        // The staged records now carry stable IDs; recapture normalises v2 -> v3
        // and represents exactly the successfully restored server world.
        published = new[] { SavesystemPatch.SerializeWorldBlocks(), SavesystemPatch.SerializeWorldFluids(), WorldObjectSnapshot.Serialize() };
        Preflight(published); Applied = true;
#if TESTING
        WorldCheckpointRegression.RecordApplied(published);
#endif
        SessionLogGame.WorldSnapshot($"WORLD-SNAPSHOT: publish token={Token} tiles={published[0].Length} fluids={published[1].Length} objects={published[2].Length} before WorldPlacePlayer");
        foreach (var id in ServerMain.AllClientIdsExceptHost) Send(id);
    }
    internal static void Send(knetid id) {
        SessionLog.Event("WORLD SNAPSHOT SEND BEGIN", $"client={id} token={Token}");
        if (published == null || Failed || !sending.Add(id)) return;
        rejected.Remove(id);
        acknowledged.Remove(id);
        Util.StartCoroutine(SendFragments(id, Token, published, Session));
    }
    static byte[] Pack(byte[][] parts) {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        foreach (var p in parts) { w.Write(p.Length); w.Write(p); }
        if (stream.Length > MaxBytes) throw new InvalidDataException("Checkpoint transfer too large.");
        return stream.ToArray();
    }
    static IEnumerator SendFragments(knetid id, string token, byte[][] parts, object owner) {
        byte[] bytes = null;
#if TESTING
        string fault = WorldCheckpointRegression.Enabled ? WorldCheckpointRegression.Fault : "";
        if (fault == "template") {
            var worldForFault = WorldGeneration.world;
            var doc = WorldObjectSnapshotData.Decode(parts[2], worldForFault.biomeDepth, worldForFault.width, worldForFault.height, WoundView.view.cInfo[2]);
            doc.backgrounds[0].template = "KMPSR_SNAP:MissingWireTemplate|0";
            parts = new[] { parts[0], parts[1], WorldObjectSnapshotData.Encode(doc) };
        }
#endif
        try { bytes = Pack(parts); } catch (Exception ex) { Fail(ex.Message); }
        if (bytes == null) yield break;
        int chunks = (bytes.Length + FragmentBytes - 1) / FragmentBytes;
        expectedAck[id] = Hash(bytes);
        var world = WorldGeneration.world;
        var w = Net.CreateWriter(NetmsgId.CLIENT_WorldSnapshot);
        int protocol = Protocol;
#if TESTING
        if (fault == "version") protocol++;
#endif
        w.Put((byte)0); w.Put(protocol); w.Put(token); w.Put(world.width); w.Put(world.height); w.Put(world.biomeDepth);
        w.Put(WoundView.view.cInfo[2]); w.Put(bytes.Length); w.Put(chunks); w.Put(Hash(bytes));
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, w, id);
#if TESTING
        if (fault == "timeout") yield break;
        if (fault == "checksum") bytes[bytes.Length - 1] ^= 1;
#endif
        float start = Time.realtimeSinceStartup;
        for (int i = 0; i < chunks; i++) {
            if (!Net.IsServer || !ReferenceEquals(owner, Session) || Token != token || Failed || world != WorldGeneration.world) yield break;
            w = Net.CreateWriter(NetmsgId.CLIENT_WorldSnapshot);
            int index = i;
#if TESTING
            if (fault == "truncate" && i == chunks - 1) break;
            if (fault == "sequence" && i == 0) index++;
#endif
            w.Put((byte)1); w.Put(token); w.Put(index);
            int length = Math.Min(FragmentBytes, bytes.Length - i * FragmentBytes);
            w.Put(length); w.Put(bytes, i * FragmentBytes, length);
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, w, id);
            if (i % 16 == 15) yield return null;
        }
        w = Net.CreateWriter(NetmsgId.CLIENT_WorldSnapshot); w.Put((byte)2); w.Put(token); w.Put(chunks); w.Put(expectedAck[id]);
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, w, id);
        SessionLogGame.WorldSnapshot($"WORLD-SNAPSHOT: sent client={id} token={token} bytes={bytes.Length} chunks={chunks} sha256={Hash(bytes)}");
        while (Net.IsServer && ReferenceEquals(owner, Session) && Token == token && !acknowledged.ContainsKey(id) && !rejected.Contains(id) &&
            ServerMain.AllClientIdsExceptHost.Contains(id) && Time.realtimeSinceStartup - start < Timeout) yield return null;
        if (Token != token || !ReferenceEquals(owner, Session)) yield break;
        sending.Remove(id);
        expectedAck.Remove(id);
        if (Net.IsServer && Token == token && !acknowledged.ContainsKey(id) && !rejected.Contains(id) && ServerMain.AllClientIdsExceptHost.Contains(id)) {
            SessionLogGame.WorldSnapshot($"WORLD-SNAPSHOT: client={id} apply ACK timeout; spawn denied", true);
            AbortPeer(id, "Checkpoint apply ACK timeout / 快照应用确认超时，未允许出生。");
        }
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_WorldSnapshot)]
    static void Receive(knetid _, NetDataReader r) {
        if (!Net.IsClient || Failed) return;
        try {
            byte op = r.GetByte();
            if (op == 3) {
                string token = r.GetString(), reason = r.GetString();
                if (token == Token) Fail(reason);
                return;
            }
            if (op == 0) {
                if (r.GetInt() != Protocol) throw new InvalidDataException("Snapshot protocol version mismatch.");
                string token = r.GetString();
                if (!SnapshotMode || token != Token || incoming != null || received != null || Applied) throw new InvalidDataException("Unexpected/stale snapshot manifest.");
                width = r.GetUInt(); height = r.GetUInt(); biome = r.GetInt(); saveId = r.GetInt();
                expectedBytes = r.GetInt(); expectedChunks = r.GetInt(); digest = r.GetString();
                if (width == 0 || height == 0 || (long)width * height > 16 * 1024 * 1024 ||
                    expectedBytes < 12 || expectedBytes > MaxBytes || expectedChunks != (expectedBytes + FragmentBytes - 1) / FragmentBytes || digest.Length != 64)
                    throw new InvalidDataException("Invalid snapshot manifest bounds.");
                SessionLog.Event("WORLD SNAPSHOT RECEIVE BEGIN", $"token={token} bytes={expectedBytes} chunks={expectedChunks} layer={biome}");
                incoming = new byte[expectedBytes]; offset = sequence = 0;
            } else {
                if (r.GetString() != Token) return; // delayed frame from a previous layer
                if (incoming == null) throw new InvalidDataException("Snapshot data without a manifest.");
                if (op == 1) {
                    int index = r.GetInt(), length = r.GetInt();
                    if (index != sequence || length != Math.Min(FragmentBytes, expectedBytes - offset) || length <= 0 || r.AvailableBytes != length)
                        throw new InvalidDataException("Snapshot fragment sequence/length mismatch.");
                    r.GetBytes(incoming, offset, length); offset += length; sequence++;
                } else if (op == 2) {
                    if (r.GetInt() != expectedChunks || r.GetString() != digest || offset != expectedBytes || sequence != expectedChunks || Hash(incoming) != digest)
                        throw new InvalidDataException("Incomplete/corrupt snapshot digest.");
                    using var stream = new MemoryStream(incoming); using var reader = new BinaryReader(stream);
                    var parts = new byte[3][];
                    for (int i = 0; i < 3; i++) {
                        int length = reader.ReadInt32();
                        if (length <= 0 || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid checkpoint part length.");
                        parts[i] = reader.ReadBytes(length);
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing checkpoint data.");
                    received = parts; incoming = null;
                    SessionLogGame.WorldSnapshot($"WORLD-SNAPSHOT: received token={Token} bytes={expectedBytes} chunks={sequence} sha256={digest}");
                } else throw new InvalidDataException("Unknown checkpoint operation.");
            }
            if (r.AvailableBytes != 0) throw new InvalidDataException("Trailing snapshot frame data.");
            lastProgress = Time.realtimeSinceStartup;
        } catch (Exception ex) { Fail(ex.Message); }
    }
    [ServerReceiver((byte)NetmsgId.SERVER_WorldSnapshot)]
    static void ReceiveAck(knetid id, NetDataReader r) {
        if (!Net.IsServer || !SnapshotMode) return;
        try {
            byte op = r.GetByte(); string token = r.GetString();
            if (token != Token) return;
            if (op == 0) { if (published != null) Send(id); }
            else if (op == 1 && sending.Contains(id) && !rejected.Contains(id)) {
                string hash = r.GetString();
                if (!expectedAck.TryGetValue(id, out var expected) || hash != expected) throw new InvalidDataException("Checkpoint ACK digest mismatch.");
                acknowledged[id] = token;
                SessionLogGame.WorldSnapshot($"WORLD-SNAPSHOT: client={id} applied ACK token={token} sha256={hash}; spawn unlocked");
            } else if (op == 2) {
                rejected.Add(id); acknowledged.Remove(id);
                SessionLogGame.WorldSnapshot($"WORLD-SNAPSHOT: client={id} rejected snapshot: {r.GetString()}; spawn denied", true);
            }
        } catch (Exception ex) {
            SessionLogGame.WorldSnapshot("WORLD-SNAPSHOT: invalid client ACK: " + ex.Message, true);
            AbortPeer(id, ex.Message);
        }
    }
    static bool CurrentLoad(WorldGeneration world, object owner, string token) =>
        Net.IsClient && world != null && world == WorldGeneration.world && ReferenceEquals(owner, Session) && token == Token;

    static void ClientPhase(string phase) {
        loadingPhase = phase; phaseStarted = Time.realtimeSinceStartup; nextLoadingText = 0;
        SessionLog.Event("WORLD SNAPSHOT PHASE", phase);
    }

    static void RequestSnapshot() {
        var request = Net.CreateWriter(NetmsgId.SERVER_WorldSnapshot); request.Put((byte)0); request.Put(Token);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, request);
    }

    // Independent of the loading coroutine: a failed nested iterator must not
    // strand the screen forever. Plugin.Update keeps this deadline running.
    internal static void TickClientLoad() {
        if (!clientLoading) return;
        if (Failed || !CurrentLoad(loadingWorld, loadingOwner, loadingToken)) { clientLoading = false; return; }
        float now = Time.realtimeSinceStartup;
        bool receiving = loadingPhase == "等待主机完整地形快照";
        float stalled = now - (receiving ? lastProgress : phaseStarted), timeout = Timeout;
#if TESTING
        if (receiving && incoming != null && WorldCheckpointRegression.Enabled && WorldCheckpointRegression.Fault == "timeout") timeout = 3;
#endif
        if (stalled > timeout || now - began > Timeout * 3) {
            Fail($"快照加载超时 / Checkpoint timeout: phase={loadingPhase}, stalled={stalled:F0}s, received={offset}/{expectedBytes} bytes. 请重新连接重试。");
            return;
        }
        if (receiving && received == null && incoming == null && !requestedAgain && now - phaseStarted >= 15f) {
            requestedAgain = true;
            try {
                RequestSnapshot();
                SessionLogGame.WorldSnapshot("WORLD-SNAPSHOT: no manifest yet; requested checkpoint again (1/1)");
            } catch (Exception ex) { Fail("Snapshot retry request failed: " + ex.Message); return; }
        }
        if (now >= nextLoadingText) {
            nextLoadingText = now + 1f;
            if (loadingWorld.loadingText != null) loadingWorld.loadingText.text = loadingPhase + $" / Checkpoint ({now - phaseStarted:F0}s)" +
                (receiving ? $"\n已接收 {offset}/{expectedBytes} bytes" + (requestedAgain ? "；已重新请求一次" : "") : "");
        }
    }

    static IEnumerator RunClientPhase(Func<IEnumerator> create, string phase, WorldGeneration world, object owner, string token) {
        if (Failed || !CurrentLoad(world, owner, token)) yield break;
        ClientPhase(phase);
        var stack = new Stack<IEnumerator>();
        try { stack.Push(create()); }
        catch (Exception ex) { Fail($"{phase}: {ex.Message}"); }
        try {
            while (stack.Count != 0 && !Failed && CurrentLoad(world, owner, token)) {
                bool more = false; object current = null;
                try {
                    more = stack.Peek().MoveNext();
                    if (more) current = stack.Peek().Current;
                    else (stack.Pop() as IDisposable)?.Dispose();
                } catch (Exception ex) { Fail($"{phase}: {ex.Message}"); }
                if (Failed) yield break;
                if (!more) continue;
                if (current is IEnumerator nested) stack.Push(nested);
                else yield return current;
            }
        } finally {
            while (stack.Count != 0) {
                try { (stack.Pop() as IDisposable)?.Dispose(); }
                catch (Exception ex) { log.warn("WORLD-SNAPSHOT: iterator cleanup failed: " + ex.Message); }
            }
        }
    }

    internal static IEnumerator ClientLoad() {
        var world = WorldGeneration.world;
        var owner = Session; string token = Token;
        if (Failed || !CurrentLoad(world, owner, token)) yield break;
        loadingWorld = world; loadingOwner = owner; loadingToken = token; clientLoading = true; requestedAgain = false;
        ClientPhase("等待主机完整地形快照");
        try {
            SessionLogGame.WorldSnapshot("WORLD-SNAPSHOT: client waiting for authoritative checkpoint; seed terrain generation bypassed");
            try { RequestSnapshot(); } catch (Exception ex) { Fail("Snapshot request failed: " + ex.Message); }
            while (received == null && !Failed && CurrentLoad(world, owner, token)) yield return null;
            if (Failed || !CurrentLoad(world, owner, token)) yield break;
            bool valid = false;
            try {
                if (world.width != width || world.height != height || world.biomeDepth != biome) throw new InvalidDataException("Snapshot world dimensions/layer mismatch.");
                WoundView.view.cInfo[2] = saveId;
                Preflight(received); valid = true;
            } catch (Exception ex) { Fail(ex.Message); }
            if (!valid) yield break;
            yield return RunClientPhase(() => world.WorldPreprocess(), "准备地形", world, owner, token);
            if (Failed || !CurrentLoad(world, owner, token)) yield break;
            yield return RunClientPhase(() => world.WorldCreateBackground(), "创建背景", world, owner, token);
            if (Failed || !CurrentLoad(world, owner, token)) yield break;
            yield return RunClientPhase(() => Restore(received), "恢复世界对象", world, owner, token);
            if (Failed || !CurrentLoad(world, owner, token)) yield break;
#if TESTING
            WorldCheckpointRegression.RecordApplied(received);
#endif
            Applied = true;
            try {
                var ack = Net.CreateWriter(NetmsgId.SERVER_WorldSnapshot); ack.Put((byte)1); ack.Put(Token); ack.Put(digest);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, ack);
            } catch (Exception ex) { Fail("Snapshot apply ACK failed: " + ex.Message); }
            if (Failed) yield break;
            SessionLogGame.WorldSnapshot("WORLD-SNAPSHOT: client applied checkpoint before WorldPlacePlayer");
            yield return RunClientPhase(() => WorldMP.Patched_WorldPlacePlayer(), "等待出生位置", world, owner, token);
            if (!Failed && CurrentLoad(world, owner, token)) yield return RunClientPhase(() => WorldMP.Patched_FinishWorldGeneration(), "完成世界加载", world, owner, token);
        } finally {
            if (loadingWorld == world && loadingToken == token && ReferenceEquals(loadingOwner, owner)) clientLoading = false;
        }
    }
    internal static IEnumerator Restore(byte[][] parts) {
        var owner = Session;
        var world = WorldGeneration.world;
        bool valid = false;
        try { Preflight(parts); valid = true; } catch (Exception ex) { Fail(ex.Message); }
        if (!valid) yield break;
        Applying = true;
        WorldMP.aaaaaaaaaaaaaaaaaaaaaaaaaaa = parts[0]; WorldMP.ccccccccccccccccccccccccccc = parts[1]; WorldMP.bbbbbbbbbbbbbbbbbbbbbbbbbbb = parts[2];
        try {
            SavesystemPatch.DeserializeWorldBlocks(parts[0]); SavesystemPatch.DeserializeWorldFluids(parts[1]);
            WorldObjectSnapshot.Deserialize(parts[2]);
        } catch (Exception ex) { Fail(ex.Message); }
        if (Failed) yield break;
        // Drive the restoration iterator here so application errors cannot strand
        // Unity inside a child coroutine and later accidentally release spawn.
        var finish = WorldObjectSnapshot.FinishRestore();
        while (true) {
            bool more = false; object current = null;
            try { more = finish.MoveNext(); if (more) current = finish.Current; } catch (Exception ex) { Fail(ex.Message); }
            if (Failed || !more) break;
            yield return current;
        }
        if (!ReferenceEquals(owner, Session) || world != WorldGeneration.world) Fail("World/session changed during checkpoint application.");
        if (!Failed) {
            // Start callbacks/prediction can edit fluid even while a world is
            // generating. The application barrier includes their initial frame.
            try { SavesystemPatch.DeserializeWorldFluids(parts[1]); } catch (Exception ex) { Fail(ex.Message); }
        }
        Applying = false;
    }
}
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.WorldPlaceEntities))]
internal static class CheckpointSkipEntityPlacement {
    static bool Prefix(ref IEnumerator __result) {
        if (!WorldSnapshotTransfer.SnapshotMode || WorldSnapshotTransfer.BaselineEntities) return true;
        __result = Empty(); return false;
    }
    static IEnumerator Empty() { yield break; }
}
[HarmonyPatch(typeof(FluidManager), nameof(FluidManager.SimulationStep))]
internal static class CheckpointPauseFluidApplication {
    static bool Prefix() => !WorldSnapshotTransfer.Applying;
}
[HarmonyPatch(typeof(ChunkScript), "RandomizeTiles")]
internal static class CheckpointTileVariation {
    static bool Prefix(ChunkScript __instance, ref IEnumerator __result) {
        if (!Net.IsRunning || !WorldSnapshotTransfer.SnapshotMode) return true;
        __result = Randomize(__instance); return false;
    }
    static IEnumerator Randomize(ChunkScript chunk) {
        var world = WorldGeneration.world;
        // Cosmetic variation must not depend on which camera rendered a chunk
        // first, nor consume the gameplay RNG. Both peers know these inputs.
        ulong tile = unchecked((uint)WoundView.view.cInfo[2] * 2654435761UL + (uint)world.biomeDepth * 2246822519UL + (uint)chunk.pos.x * 3266489917UL + (uint)chunk.pos.y * 668265263UL + 1);
        var map = chunk.GetComponent<UnityEngine.Tilemaps.Tilemap>();
        for (int i = 0; i < WorldGeneration.CHUNKSIZE; i++) {
            if (world != WorldGeneration.world || map == null) yield break;
            for (int j = 0; j < WorldGeneration.CHUNKSIZE; j++) {
                if (world.GetBlockInfo(world.GetBlock(new Vector2Int(i + chunk.pos.x * WorldGeneration.CHUNKSIZE, j + chunk.pos.y * WorldGeneration.CHUNKSIZE))).noVariation) continue;
                tile = WorldGeneration.lehmer64(tile); long sx = Math.Abs((long)tile % 2L) * 2 - 1;
                tile = WorldGeneration.lehmer64(tile); long sy = Math.Abs((long)tile % 2L) * 2 - 1;
                map.SetTransformMatrix(new Vector3Int(i - world.HALFCHUNKSIZE, j - world.HALFCHUNKSIZE), Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0, 0, tile % 5 * 90), new Vector3(sx, sy, 1)));
            }
            yield return null;
        }
    }
}
#endif
