#if FULLGAME
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Together;

// Discrete cosmetic events are separate from the 10Hz guard pose snapshots.
// Never replay GuardScript.Fire/TurretScript.Shoot on a client: both do damage.
internal static class GuardShotVisual {
    static GameObject linePrefab;
    static bool warnedMissingLine;

    internal static void ServerSend(GuardScript guard) {
        if (!Net.IsRunning || !Net.IsServer || !EndingGuardSync.Active || guard == null || guard.barrel == null) return;
        var si = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(guard.gameObject);
        Vector2 origin = guard.barrel.position;
        Vector2 direction = guard.barrel.right * guard.scale; // same direction as vanilla Fire
        var writer = Net.CreateWriter(NetmsgId.CLIENT_GuardShotVisual);
        writer.Put(si?.syncId ?? (knetid)0);
        writer.Put(si?.Version ?? (byte)0);
        writer.Put(origin.x); writer.Put(origin.y);
        writer.Put(direction.x); writer.Put(direction.y);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, ServerMain.AllClientIdsExceptHost);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_GuardShotVisual)]
    static void Receive(knetid _, NetDataReader reader) {
        if (!Net.IsRunning || !Net.IsClient || !EndingGuardSync.Active || reader.AvailableBytes != 19) return;
        reader.Get(out knetid id);
        byte version = reader.GetByte();
        var origin = new Vector2(reader.GetFloat(), reader.GetFloat());
        var direction = new Vector2(reader.GetFloat(), reader.GetFloat());
        if (!Util.IsFinite(origin.x) || !Util.IsFinite(origin.y) || !Util.IsFinite(direction.x)
            || !Util.IsFinite(direction.y) || direction.sqrMagnitude < .0001f) return;
        direction.Normalize();

        GuardScript guard = null;
        if (NetObjectRegistry.TryGetSyncInfo(id, out var si) && si.Version == version && si.go != null)
            si.go.TryGetComponent(out guard);
        if (guard == null) {
            // The registry creation can lag behind a shot. Coordinates still
            // suffice to render it; a nearby guard only helps ignore its collider.
            float nearest = 36f;
            foreach (var candidate in Object.FindObjectsOfType<GuardScript>()) {
                float distance = ((Vector2)candidate.transform.position - origin).sqrMagnitude;
                if (distance >= nearest) continue;
                guard = candidate;
                nearest = distance;
            }
        }

        Vector2 end = origin + direction * 200f;
        foreach (var hit in Physics2D.RaycastAll(origin, direction, 200f, LayerMask.GetMask("Ground", "Body", "Limb", "Descriptor"))) {
            if (hit.collider == null || (guard != null && (hit.transform == guard.transform || hit.transform.IsChildOf(guard.transform)))) continue;
            end = hit.point;
            break;
        }
        Sound.Play("pistolshot", origin);
        if (linePrefab == null) linePrefab = Resources.Load<GameObject>("Special/TurretLine");
        if (linePrefab == null) {
            if (!warnedMissingLine) { warnedMissingLine = true; log.warn("[guard-shot] Special/TurretLine prefab was not found"); }
            return;
        }
        var line = Object.Instantiate(linePrefab, Vector3.zero, Quaternion.identity);
        if (line.TryGetComponent<LineRenderer>(out var renderer)) {
            renderer.positionCount = 2;
            renderer.SetPosition(0, origin);
            renderer.SetPosition(1, end);
        }
        Object.Destroy(line, .05f);
    }
}
#endif
