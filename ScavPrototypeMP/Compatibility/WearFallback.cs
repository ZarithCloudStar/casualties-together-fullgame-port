using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

// A client can wear a network-registered wearable locally while the host's
// ItemWearing handler misses it. Ask the host to force-wear that exact network
// item (never a same-name candidate), then let the normal item tracker sync it.
internal sealed class WearFallback : CasualtiesTogetherSingleton {
    const string MessageName = "TogetherWearFallback";
    const int Protocol = 1;
    enum Message : byte { Request = 1, Response = 2 }

    sealed class Pending {
        internal Item item;
        internal Body body;
        internal knetid syncId;
        internal Vector2 reported;
        internal float due;
        internal int attempts;
    }

    static readonly List<Pending> queue = new();
    static int adopted;
    const int MaxAdopted = 20;
    const float SearchRadius = 12f;

    void Awake() {
        Multiplayer.RegisterCustomClientReceiver(MessageName, ClientMessage);
        Multiplayer.RegisterCustomServerReceiver(MessageName, ServerMessage);
        Plugin.Logger?.LogInfo("[wear] fallback channel registered");
    }

    [HarmonyPatch(typeof(Body), "WearWearable", new[] { typeof(Item) })]
    static class Body_WearWearable_RecordPatch {
        static void Postfix(Body __instance, Item item) => Record(__instance, item);
    }

    static void Record(Body body, Item item) {
        if (!Net.IsRunning || !Net.IsClient || body == null || item == null || item.Stats == null || !item.Stats.wearable)
            return;
        if (!body.IsBodyLocal()) return;
        if (!ItemSync.TryGetItemSyncInfo(item, out var si) || si.syncId == 0) return;
        foreach (var p in queue) if (p.item == item) return;
        queue.Add(new Pending {
            item = item, body = body, syncId = si.syncId,
            reported = item.transform.position,
            due = Time.unscaledTime + 1.5f,
        });
    }

    void Update() {
        if (!Net.IsRunning || !Net.IsClient) { queue.Clear(); return; }
        for (int i = queue.Count - 1; i >= 0; i--) {
            var p = queue[i];
            if (p.item == null || p.body == null || !p.body.IsBodyLocal() || p.body.GetWearable(p.item.id) != p.item) {
                queue.RemoveAt(i);
                continue;
            }
            if (Time.unscaledTime < p.due) continue;
            if (p.attempts >= 3) { queue.RemoveAt(i); continue; }
            p.attempts++;
            p.due = Time.unscaledTime + 1.5f;
            SendRequest(p);
        }
    }

    static void SendRequest(Pending p) {
        var w = Multiplayer.CreateNamedWriter(MessageName);
        w.Put(Protocol); w.Put((byte)Message.Request); w.Put(p.syncId);
        w.Put(p.reported.x); w.Put(p.reported.y);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
        Plugin.Logger?.LogInfo($"[wear] request sync={p.syncId} id={p.item.id} attempt={p.attempts}");
    }

    static void ClientMessage(NetDataReader r) {
        try {
            if (r.GetInt() != Protocol) return;
            if ((Message)r.GetByte() != Message.Response) return;
            r.Get(out knetid syncId);
            bool ok = r.GetBool();
            string why = r.GetString(128);
            Pending found = null;
            foreach (var p in queue) if (p.syncId == syncId) { found = p; break; }
            if (ok && found != null) {
                queue.Remove(found);
                adopted++;
                Plugin.Logger?.LogInfo($"[wear] host accepted sync={syncId}");
            } else if (found != null) {
                Plugin.Logger?.LogInfo($"[wear] host declined sync={syncId}: {why}");
            }
        } catch (System.Exception ex) {
            Plugin.Logger?.LogWarning("[wear] response parse failed: " + ex.Message);
        }
    }

    static void ServerMessage(ScavPlayer player, NetDataReader r) {
        try {
            if (r.GetInt() != Protocol || (Message)r.GetByte() != Message.Request) return;
            r.Get(out knetid syncId);
            float x = r.GetFloat(), y = r.GetFloat();
            if (!Net.IsServer || player == null || player.body == null) return;
            var body = player.body;
            if (!ItemSync.TryGetItem(syncId, out var si, out var item) || item == null || si == null) {
                Reply(player, syncId, false, "item-not-found");
                return;
            }
            if (item.Stats == null || !item.Stats.wearable) {
                Reply(player, syncId, false, "not-wearable");
                return;
            }
            if (body.GetWearable(item.id) != null) {
                Reply(player, syncId, true, "already-worn");
                return;
            }
            if (adopted >= MaxAdopted) {
                Reply(player, syncId, false, "session-limit");
                return;
            }
            var owner = OwnerBody(item);
            if (owner != null && owner != body) {
                Reply(player, syncId, false, "owned-by-other");
                return;
            }
            if (owner == null && item.transform.parent != null) {
                Reply(player, syncId, false, "in-container");
                return;
            }
            var reported = new Vector2(x, y);
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) ||
                Vector2.Distance(item.transform.position, reported) > SearchRadius ||
                Vector2.Distance(item.transform.position, body.transform.position) > SearchRadius) {
                Reply(player, syncId, false, "too-far");
                return;
            }
            ItemSync.Body_ForceWearWearable(body, item);
            bool worn = body.GetWearable(item.id) == item;
            if (worn) {
                adopted++;
                NetObjectRegistry.SafeNewGO(item.gameObject);
                Plugin.Logger?.LogInfo($"[wear] host forced sync={syncId} id={item.id} player={player.playerName}");
            }
            Reply(player, syncId, worn, worn ? "worn" : "force-failed");
        } catch (System.Exception ex) {
            Plugin.Logger?.LogWarning("[wear] host request failed: " + ex.Message);
        }
    }

    static void Reply(ScavPlayer player, knetid syncId, bool ok, string why) {
        var w = Multiplayer.CreateNamedWriter(MessageName);
        w.Put(Protocol); w.Put((byte)Message.Response); w.Put(syncId); w.Put(ok); w.Put(why ?? "");
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, w, player.playerId);
    }

    static Body OwnerBody(Item item) {
        if (item == null || item.transform == null) return null;
        for (Transform t = item.transform.parent; t != null; t = t.parent)
            if (t.TryGetComponent(out Body body)) return body;
        return null;
    }
}
