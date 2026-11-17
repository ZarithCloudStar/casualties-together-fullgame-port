#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Unity.VisualScripting;

namespace Together;

// Effects are plain C# objects, not Components. The network prefab has no effect
// list and must never roll SetUpEffects independently on a second machine.
internal sealed class CrystalSync : MonoBehaviour {
    internal CrystalBehaviour crystal;
    SpriteRenderer sprite;
    UnityEngine.Rendering.Universal.Light2D light;
    float size;
    bool appearanceReady;
    bool sentAppearance;
    Color sentColor;
    float sentIntensity;
    float nextState, nextRequest, nextContact;
    uint revision, receivedRevision;
    bool received;
    static float nextWarning;
    internal static Body MeleeAttacker, TriggerBody;
    internal static bool UnstableExplosion { get; private set; }
    internal static CrystalBehaviour UnstableExplosionSource { get; private set; }
    static readonly string[] Fields = { "cooldown", "activated", "timerStarted", "timer", "forceMult", "enemyCount", "liquid" };

    internal sealed class EffectState {
        public int type;
        public float[] values;
    }
    internal sealed class Snapshot {
        public uint revision;
        public float size;
        public float[] color;
        public float lightIntensity;
        public List<EffectState> effects;
        public float heaterTemp, heaterStrength;
    }
    internal static void Warn(string message) {
        if (Time.unscaledTime < nextWarning) return;
        nextWarning = Time.unscaledTime + 10f;
        log.warn("[crystal] " + message);
    }
    internal static CrystalSync Get(CrystalBehaviour crystal) {
        var sync = crystal.GetOrAddComponent<CrystalSync>();
        sync.crystal = crystal;
        return sync;
    }
    internal static CrystalSync Get(Component owner) {
        // CrystalEnemy has no CrystalBehaviour. Reuse the appearance-only half
        // of the crystal state protocol (empty effects) for its renderer/light.
        return owner.GetOrAddComponent<CrystalSync>();
    }
    void Awake() {
        crystal = GetComponent<CrystalBehaviour>();
        sprite = GetComponent<SpriteRenderer>();
        light = GetComponent<UnityEngine.Rendering.Universal.Light2D>();
        // CrystalEnemy has no CrystalBehaviour; on the host it still needs the
        // appearance-only state path so its chosen color reaches clients.
        if (Net.IsServer && GetComponent<CrystalEnemy>() != null) appearanceReady = true;
    }
    // Native SetUpEffects removes CrystalBehaviour from ordinary crystals.
    // The GameObject, renderer and light survive and still need replication.
    internal void AppearanceInitialized() {
        if (!Net.IsServer || crystal == null || crystal.effects == null) return;
        size = crystal.crystalSize;
        appearanceReady = true;
        nextState = 0f;
    }
    // Call before native SetUpEffects destroys CrystalBehaviour on ordinary
    // crystals. The renderer/light survive; this keeps their appearance
    // requestable after network streaming creates them on a client.
    internal void MarkAppearance() {
        if (!Net.IsServer || sprite == null) return;
        if (crystal != null) size = crystal.crystalSize;
        appearanceReady = true;
        nextState = 0f;
    }
    bool HasState => appearanceReady || crystal != null && crystal.effects != null;
    void Update() {
        if (!Net.IsRunning || !SharedMain.LocalWorldIsGenerated || sprite == null || light == null) return;
        if (Net.IsServer) {
            // ChunkScript.Update only runs while the HOST's tile renderer is
            // enabled. Tick effects here for the union of all players' simulation
            // ranges, including restored crystals in client-first chunks. The
            // ChunkUpdate patch below suppresses the native tick in multiplayer.
            if (Ready(crystal) && !WorldGeneration.world.generatingWorld)
                foreach (var effect in crystal.effects) effect.Update();
            // Ordinary crystals are static: publish their initial appearance
            // and later changes; a newly joined replica requests its own copy.
            bool appearanceChanged = !sentAppearance || sprite.color != sentColor || light.intensity != sentIntensity;
            if (Time.unscaledTime >= nextState && HasState && (crystal != null || appearanceChanged) &&
                NetObjectRegistry.TryGetSyncInfo(this, out var _)) {
                nextState = Time.unscaledTime + 2f;
                SendState(); // late join, range re-entry and lost entity creation recover here
            }
        } else {
            if (!received && Time.unscaledTime >= nextRequest && NetObjectRegistry.TryGetSyncInfo(this, out var si)) {
                nextRequest = Time.unscaledTime + 2f;
                var w = Net.CreateWriter(NetmsgId.SERVER_CrystalState);
                w.Put(si.syncId); w.Put(si.Version);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
            }
            // Only presentation updates run on replicas. Never execute gameplay
            // Update (sepsis, fluids, explosions, gravity) a second time.
            if (crystal != null && crystal.effects != null) foreach (var e in crystal.effects) {
                if (e is CrystalEMP || e is CrystalHealing || e is CrystalTemperature || e is CrystalMimic) e.Update();
                if (e is CrystalGravity gravity) UpdateGravityReplica(gravity);
                if (e is CrystalUnstable u && u.timerStarted && crystal.light != null)
                    crystal.light.intensity = Mathf.Min(25f, crystal.light.intensity + Time.deltaTime * 4f);
            }
        }
    }
    // Continuous forces must be simulated on the physics owner. The server
    // still owns world items and never asks replicas to run gameplay Update.
    void UpdateGravityReplica(CrystalGravity gravity) {
        foreach (var hit in Physics2D.OverlapCircleAll(crystal.transform.position, 16f * crystal.crystalSize)) {
            if (hit.attachedRigidbody != null && hit.attachedRigidbody.bodyType == RigidbodyType2D.Dynamic &&
                Util.TryGetBodyFromObject(hit, out var body) && body.IsBodyLocal())
                hit.attachedRigidbody.velocity += (Vector2)(hit.transform.position - crystal.transform.position).normalized * Time.deltaTime * gravity.forceMult * 45f;
        }
        if (gravity.ring != null) {
            Vector3 target = gravity.forceMult > 0f ? Vector3.one * 32f * crystal.crystalSize : Vector3.zero;
            gravity.ring.transform.localScale = Vector3.Lerp(gravity.ring.transform.localScale, target, Time.deltaTime * 5f);
            var color = gravity.ring.color;
            color.a = Mathf.Lerp(color.a, gravity.ringTime < .5f ? 1f : 0f, Time.deltaTime * (gravity.ringTime < .5f ? 4f : 6f));
            gravity.ring.color = color;
        }
        gravity.ringTime += Time.deltaTime;
        if (gravity.ringTime <= 1f) return;
        if (gravity.ring == null) {
            var go = Utils.Create("Special/crystalring", crystal.transform.position, 0f);
            go.transform.SetParent(crystal.transform, true);
            go.transform.localScale = gravity.forceMult > 0f ? Vector3.zero : Vector3.one * 32f * crystal.crystalSize;
            gravity.ring = go.GetComponent<SpriteRenderer>();
            var color = crystal.sprite.color; color.a = 0f; gravity.ring.color = color;
        } else UnityEngine.Object.Destroy(gravity.ring);
        gravity.ringTime = 0f;
    }
    internal static bool Ready(CrystalBehaviour c) => c != null && SharedMain.LocalWorldIsGenerated &&
        c.effects != null && c.build != null && SharedMain.WorldInSimRange(c.transform.position);

    static bool ContactEnabled(CrystalBehaviour c) => c != null && c.gameObject.activeInHierarchy &&
        c.TryGetComponent<Collider2D>(out var collider) && collider.enabled;
    internal static void Contact(CrystalBehaviour c, GameObject touched) {
        // A replica may collide before its first state packet. The claim
        // needs only the registry identity; the server owns the effect list.
        if (!ContactEnabled(c) || touched == null || c.build == null || c.build.health <= 0f || !SharedMain.LocalWorldIsGenerated ||
            !SharedMain.WorldInSimRange(c.transform.position) || Net.IsServer && !Ready(c)) return;
#if TESTING
        if (CrystalTests.Enabled) CrystalTests.contacts++;
#endif
        Util.TryGetBodyFromObject(touched, out var body);
        if (Net.IsServer) { Dispatch(c, body, touched, null); return; }
        // Remote pose colliders cannot claim on behalf of another player.
        if (body == null || !body.IsBodyLocal()) return;
        var sync = Get(c);
        if (Time.unscaledTime < sync.nextContact || !NetObjectRegistry.TryGetSyncInfo(c, out var si)) return;
        sync.nextContact = Time.unscaledTime + .2f;
        var w = Net.CreateWriter(NetmsgId.SERVER_CrystalContact);
        w.Put(si.syncId); w.Put(si.Version);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
    }
    internal static bool Near(CrystalBehaviour c, Body body) {
        if (body == null || c == null) return false;
        var col = c.GetComponent<Collider2D>();
        Vector2 point = col == null ? (Vector2)c.transform.position : col.ClosestPoint(body.transform.position);
        return KM.DistanceCheck(point, body.transform.position, 32f);
    }
    [ServerReceiver((byte)NetmsgId.SERVER_CrystalContact)]
    static void ReceiveContact(knetid client, NetDataReader r) {
        r.Get(out knetid id); byte version = r.GetByte();
        if (!Net.IsServer || !TryCrystal(id, version, out var c) || !Ready(c) || !ContactEnabled(c) || c.build.health <= 0f ||
            !ScavPlayer.TryGetPlayerAndNetBodyFromClientId(client, out var _, out var nb) || nb.body == null || !Near(c, nb.body)) return;
#if TESTING
        if (CrystalTests.Enabled) CrystalTests.claims++;
#endif
        Dispatch(c, nb.body, nb.body.gameObject, null);
    }
    internal static void Hit(CrystalBehaviour c, Body attacker, AttackInfo atk) {
        if (Net.IsServer && Ready(c) && atk != null) Dispatch(c, attacker, null, atk);
    }
    internal static bool TryCrystal(knetid id, byte version, out CrystalBehaviour c) {
        c = null;
        return NetObjectRegistry.TryGetSyncInfo(id, out var si) && si.Version == version && si.go != null && si.go.TryGetComponent(out c);
    }
    static void Dispatch(CrystalBehaviour c, Body body, GameObject touched, AttackInfo attack) {
        if (!Net.IsServer || !Ready(c)) return;
        // An early host contact can precede the registry's normal discovery pass.
        if (!NetObjectRegistry.TryGetSyncInfo(c, out var _) && NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(c.gameObject) == null) {
            Warn("cannot register crystal before activation"); return;
        }
        var bgo = c.GetOrAddComponent<BodyGetterOverrider>();
        var data = c.GetOrAddComponent<AnyDataContainerComponent>();
        var previous = bgo.body; var previousTrigger = TriggerBody;
        bgo.body = body; TriggerBody = body;
        data["CrystalCollisionIsBody"] = body != null;
        data["CrystalBuildingHitAttacker"] = attack != null ? body : null;
        data["CrystalBuildingHitAtk"] = attack;
        bool changed = false;
        float buildingHealth = c.build.health;
        Vector3 bodyPosition = body != null ? body.transform.position : Vector3.zero;
        try {
            foreach (var e in c.effects.ToArray()) {
                var before = Fields.Select(f => Read(e, f)).ToArray();
                // World/turret hits may consume an EMP or start an unstable
                // crystal, but must not silently target a nearby/camera body.
                if (body == null && attack != null && (e is CrystalEMP || e is CrystalUnstable)) {
                    if (e is CrystalEMP emp && !emp.activated) {
                        emp.activated = true; emp.crystal.SetColor(Color.white);
                        if (emp.part != null) emp.part.SetActive(false);
                        Sound.Play("crystalemp", c.transform.position);
                        Emit(c, e, null); changed = true;
                    } else if (e is CrystalUnstable unstable && !unstable.timerStarted) {
                        unstable.timerStarted = true; changed = true;
                        Sound.Play("crystaltick", c.transform.position, true, false, c.transform);
                    }
                    continue;
                }
                if (body == null && attack != null && attack.unarmed && (e is CrystalBurning || e is CrystalElectric)) {
                    Warn("unarmed hit without a scoped attacker; body effect skipped"); continue;
                }
                float cooldown = Read(e, "cooldown");
                bool active = Read(e, "activated") != 0;
                if (attack == null) e.Touched(touched); else e.Hit(attack);
                bool stateChanged = !before.SequenceEqual(Fields.Select(f => Read(e, f)));
                changed |= stateChanged;
                if (((e is CrystalBurning || e is CrystalElectric || e is CrystalKinetic) && Read(e, "cooldown") > cooldown) ||
                    ((e is CrystalEMP || e is CrystalHealing) && !active && Read(e, "activated") != 0))
                    Emit(c, e, body);
                if (e is CrystalTeleport && body != null && body.transform.position != bodyPosition && body.TryGetNetBody(out var teleported)) {
                    teleported.plr?.Server_TeleportCharacter(body.transform.position);
                    Emit(c, e, body);
                }
            }
            changed |= c.build.health != buildingHealth || body != null && body.transform.position != bodyPosition;
            if (!changed) return; // idle limb/prop contacts do not flood health or state packets
            if (body != null && body.TryGetNetBody(out var nb)) MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            if (NetObjectRegistry.TryGetSyncInfo(c, out var si)) si.WriteObjectIntoPacket();
            Get(c).SendState();
        } finally {
            // A following bullet/prop cannot inherit the previous contact.
            bgo.body = previousTrigger != null ? previous : null; TriggerBody = previousTrigger;
            data["CrystalBuildingHitAttacker"] = null;
            data["CrystalBuildingHitAtk"] = null;
            data["CrystalCollisionIsBody"] = false;
        }
    }
    static readonly Dictionary<(Type, string), FieldInfo> stateFields = new();
    static FieldInfo StateField(Type type, string field) {
        var key = (type, field);
        if (!stateFields.TryGetValue(key, out var f)) {
            f = type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            stateFields[key] = f; // missing optional fields are expected; cache them without Harmony warnings
        }
        return f;
    }
    static float Read(CrystalEffect effect, string field) {
        var f = StateField(effect.GetType(), field);
        if (f == null) return 0;
        return Convert.ToSingle(f.GetValue(effect));
    }
    static void Write(CrystalEffect effect, string field, float value) {
        var f = StateField(effect.GetType(), field);
        if (f != null) f.SetValue(effect, Convert.ChangeType(value, f.FieldType));
    }
    internal Snapshot Capture() {
        if (crystal != null) size = crystal.crystalSize;
        var color = sprite.color;
        var heater = GetComponent<Heater>();
        var effects = crystal != null && crystal.effects != null ? crystal.effects : new List<CrystalEffect>();
        return new Snapshot { revision = ++revision, size = size,
            color = new[] { color.r, color.g, color.b, color.a },
            lightIntensity = light.intensity,
            heaterTemp = heater != null ? heater.desiredTemp : 0, heaterStrength = heater != null ? heater.tempLerpStrength : 0,
            effects = effects.Select(e => new EffectState {
                type = CrystalBehaviour.possibleEffects.FindIndex(f => f.factory().GetType() == e.GetType()),
                values = Fields.Select(f => Read(e, f)).ToArray()
            }).ToList() };
    }
    internal void SendState(knetid? target = null) {
        if (!Net.IsServer || !HasState || !NetObjectRegistry.TryGetSyncInfo(this, out var si)) return;
        var w = Net.CreateWriter(NetmsgId.CLIENT_CrystalState);
        w.Put(si.syncId); w.Put(si.Version); w.Put(JsonConvert.SerializeObject(Capture()));
        if (target.HasValue) Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, target.Value);
        else {
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, ServerMain.AllClientIdsExceptHost);
            sentAppearance = true; sentColor = sprite.color; sentIntensity = light.intensity;
        }
    }
    static bool TrySync(knetid id, byte version, out CrystalSync sync) {
        sync = null;
        return NetObjectRegistry.TryGetSyncInfo(id, out var si) && si.Version == version && si.go != null && si.go.TryGetComponent(out sync);
    }
    [ServerReceiver((byte)NetmsgId.SERVER_CrystalState)]
    static void RequestState(knetid client, NetDataReader r) {
        r.Get(out knetid id); byte version = r.GetByte();
        if (!Net.IsServer || !TrySync(id, version, out var sync) || !sync.HasState ||
            !ScavPlayer.TryGetPlayerAndNetBodyFromClientId(client, out var _, out var nb) ||
            nb.body == null || !KM.DistanceCheck(sync.transform.position, nb.body.transform.position, 128f)) return;
        // Registration searches 80 units plus collider extent, and simulation
        // covers a 3x3 chunk union. A 32-unit contact radius rejects visible
        // replicas whose first state preceded entity creation. Recovery must
        // cover that streaming range, rather than require touching the crystal.
        // Requests are recoveries, not an unbounded client-controlled broadcast.
        if (Time.unscaledTime < sync.nextState - 1.5f) return;
        sync.SendState(client);
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_CrystalState)]
    static void ReceiveState(knetid _, NetDataReader r) {
        r.Get(out knetid id); byte version = r.GetByte(); string json = r.GetString();
        if (Net.IsServer || !TrySync(id, version, out var sync)) return;
        try { sync.Apply(JsonConvert.DeserializeObject<Snapshot>(json)); }
        catch (Exception ex) { Warn("state rejected: " + ex.Message); }
    }
    internal void Apply(Snapshot s) {
        if (s == null || received && s.revision <= receivedRevision || s.effects == null || s.effects.Count > 17 ||
            s.color == null || s.color.Length != 4 || s.effects.Any(e => e.type < 0 || e.type >= CrystalBehaviour.possibleEffects.Count || e.values == null || e.values.Length != Fields.Length)) return;
        if (crystal == null) {
            if (s.effects.Count != 0) return;
            ApplyAppearance(s);
            return;
        }
        bool initialize = !received;
        if (!initialize && (crystal.effects.Count != s.effects.Count || crystal.effects.Where((e, i) =>
            e.GetType() != CrystalBehaviour.possibleEffects[s.effects[i].type].factory().GetType()).Any())) {
            Warn("effect list changed on a live crystal; refusing to duplicate Start side effects"); return;
        }
        crystal.crystalSize = s.size;
        if (initialize) {
            crystal.effects = new List<CrystalEffect>();
            float health = crystal.build.health; Vector3 scale = crystal.transform.localScale;
            var random = UnityEngine.Random.state;
            try {
                foreach (var state in s.effects) {
                    var e = CrystalBehaviour.possibleEffects[state.type].factory();
                    e.crystal = crystal; crystal.effects.Add(e); e.Start();
                }
            } finally {
                crystal.build.health = health; crystal.transform.localScale = scale;
                UnityEngine.Random.state = random;
            }
        }
        for (int i = 0; i < s.effects.Count; i++) {
            var effect = crystal.effects[i];
            for (int f = 0; f < Fields.Length; f++) Write(effect, Fields[f], s.effects[i].values[f]);
            if (effect is CrystalEMP emp && emp.part != null) emp.part.SetActive(!emp.activated);
        }
        var heater = crystal.GetComponent<Heater>();
        if (heater != null) { heater.desiredTemp = s.heaterTemp; heater.tempLerpStrength = s.heaterStrength; heater.enabled = Net.IsServer; }
        ApplyAppearance(s);
        if (s.effects.Count == 0) {
            // A streamed ordinary prefab still has these components, unlike
            // the generated/snapshot root. Mirror native SetUpEffects without
            // rolling effects or consuming the world's random sequence.
            UnityEngine.Object.Destroy(GetComponent<UsableObject>());
            UnityEngine.Object.Destroy(crystal);
        }
    }
    void ApplyAppearance(Snapshot s) {
        size = s.size;
        sprite.color = new Color(s.color[0], s.color[1], s.color[2], s.color[3]);
        light.color = sprite.color;
        light.intensity = s.lightIntensity;
        received = true; receivedRevision = s.revision;
    }
    // Only the authoritative activation emits this reliable event. Replicas play
    // feedback/physics, never Burn/Shock/Heal/DrainCharge a second time.
    static void Emit(CrystalBehaviour c, CrystalEffect effect, Body body) {
        if (!NetObjectRegistry.TryGetSyncInfo(c, out var si)) return;
        byte kind = effect is CrystalBurning ? (byte)1 : effect is CrystalElectric ? (byte)2 : effect is CrystalEMP ? (byte)3 : effect is CrystalHealing ? (byte)4 : effect is CrystalKinetic ? (byte)5 : (byte)6;
        var w = Net.CreateWriter(NetmsgId.CLIENT_CrystalEffect);
        w.Put((Vector2)c.transform.position); w.Put(kind);
        w.Put(body != null && body.TryGetNetBody(out var nb) ? nb.netId : (knetid)0);
        var limbs = kind == 2 && body != null ? body.limbs : Array.Empty<Limb>();
        w.Put(kind == 5 ? (byte)1 : (byte)limbs.Length);
        if (kind == 5) w.Put(body.GetVelocity());
        else foreach (var limb in limbs) w.Put(limb.rb != null ? limb.rb.velocity : Vector2.zero);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, ServerMain.AllClientIdsExceptHost);
        if (effect is CrystalEMP && body != null) foreach (var item in body.GetAllItemsThorough())
            if (NetObjectRegistry.TryGetSyncInfo(item, out var itemSi)) itemSi.WriteObjectIntoPacket();
#if TESTING
        if (CrystalTests.Enabled) {
            CrystalTests.activations++;
            CrystalTests.activationsByKind.TryGetValue(kind, out int total);
            CrystalTests.activationsByKind[kind] = total + 1;
        }
        log.l($"[crystal] activated kind={kind} id={si.syncId} target={(body != null ? body.name : "world")}");
#endif
    }
    // Called at the native CreateExplosion instruction, not from a timer guess
    // or object destruction (which may also mean unloading/despawning).
    internal static void ExplodeUnstable(ExplosionParams explosion, CrystalUnstable source) {
        if (!Net.IsRunning) { WorldGeneration.CreateExplosion(explosion); return; }
        if (!Net.IsServer) return;
        UnstableExplosion = true;
        UnstableExplosionSource = source.crystal;
        try {
            WorldGeneration.CreateExplosion(explosion);
            var clients = ScavPlayer.ClientIdToPlayerDict.Values.Where(p => !p.IsLocal && p.body != null &&
                KM.DistanceCheck(explosion.position, p.body.transform.position, Mathf.Max(60f, explosion.range * 2.5f)))
                .Select(p => p.playerId).ToList();
            var w = Net.CreateWriter(NetmsgId.CLIENT_CrystalEffect);
            w.Put(explosion.position); w.Put((byte)7); w.Put(explosion.range); w.Put(explosion.sound ?? "explosion");
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, clients);
        } finally { UnstableExplosion = false; UnstableExplosionSource = null; }
    }
    internal static void PrepareExplodedReplica(knetid id, byte version) {
        if (TryCrystal(id, version, out var crystal) && crystal.TryGetComponent<Collider2D>(out var collider))
            collider.enabled = false; // native Update disables this before calling CreateExplosion
    }
    internal static void PlayExplosion(Vector2 position, float range, string sound) {
        if (!SharedMain.LocalWorldIsGenerated || WorldGeneration.world == null) return;
        // Native CreateExplosion's presentation prelude only. Calling it with
        // zero damage still changes velocity, terrain, shock and consciousness.
        // Match Sound_Play_MultiplayerPatch's native explosion conversion:
        // spatial audio pulled toward the listener, with the same volume boost.
        Vector2 soundPosition = Camera.main != null ? Vector2.LerpUnclamped(position, Camera.main.transform.position, .8f) : position;
        Sound.Play(sound, soundPosition, twoDimensional: false, pitchShift: false, null,
            Sound_Play_MultiplayerPatch.nontwoDimensionalvolumemultiplier);
        UnityEngine.Object.Instantiate(Resources.Load("Special/ExplosionParticle"), position, Quaternion.identity);
        var chunk = WorldGeneration.world.GetClosestChunk(WorldGeneration.world.WorldToBlockPos(position));
        if (chunk != null) {
            var mark = UnityEngine.Object.Instantiate(Resources.Load("Special/blastmark"), chunk.transform) as GameObject;
            mark.transform.position = position;
            mark.transform.eulerAngles = new Vector3(0f, 0f, UnityEngine.Random.value * 360f);
        }
        var camera = PlayerCamera.main;
        if (camera == null) return;
        camera.shaker.Shake(range * 20f);
        if (camera.body != null && KM.DistanceCheck(position, camera.body.transform.position, range * 2.5f) && !camera.body.HasHearingProtection()) {
            Sound.Play("tinnitus", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            camera.shaker.Shake(range * 20f);
        }
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_CrystalEffect)]
    static void ReceiveEffect(knetid _, NetDataReader r) {
        r.Get(out Vector2 pos); byte kind = r.GetByte();
        if (kind == 7) {
            float range = r.GetFloat(); string sound = r.GetString();
            if (Net.IsClient) PlayExplosion(pos, range, sound);
            return;
        }
        r.Get(out knetid victim);
        int count = r.GetByte(); var velocities = new Vector2[count];
        for (int i = 0; i < count; i++) r.Get(out velocities[i]);
        if (Net.IsServer || kind < 1 || kind > 6 || !SharedMain.LocalWorldIsGenerated) return;
        Sound.Play(kind == 1 ? "Scald" : kind == 2 ? "zap" : kind == 3 ? "crystalemp" : kind == 4 ? "crystalheal" : kind == 5 ? "mushroombounce" : "observerlaugh", pos);
        if (!NetBody.TryGetNetBodyFromId(victim, out var nb) || nb.body == null || !nb.IsLocal) return;
        if (kind <= 2) nb.body.Ragdoll();
        if (kind == 2) for (int i = 0; i < Math.Min(count, nb.body.limbs.Length); i++)
            if (nb.body.limbs[i].rb != null) nb.body.limbs[i].rb.velocity = velocities[i];
        if (kind == 5 && count == 1) nb.body.SetVelocity(velocities[0]);
        if (kind == 4 || kind == 6) PlayerCamera.main.StartCoroutine("FlashBrief");
        else PlayerCamera.main.shaker.Shake(kind == 1 ? 50f : kind == 5 ? 100f : 1f);
#if TESTING
        if (CrystalTests.Enabled) { CrystalTests.feedback++; CrystalTests.feedbackKinds.Add(kind); }
        log.l($"[crystal] feedback kind={kind} victim={victim}");
#endif
    }
    internal static Body EnemyTarget(CrystalEnemy enemy) {
        var bgo = enemy.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body == null || !bgo.body.alive) bgo.body = NetBody.GetNearestBodyPrioritizeAlive(enemy.transform.position).Item1?.body;
        return bgo.body;
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), "Awake")]
internal static class CrystalSync_AwakePatch { static void Postfix(CrystalBehaviour __instance) { if (Net.IsRunning) CrystalSync.Get(__instance); } }
[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.SetUpEffects))]
internal static class CrystalSync_SetupPatch {
    static bool Prefix(CrystalBehaviour __instance) {
        if (Net.IsServer && __instance != null) CrystalSync.Get(__instance).MarkAppearance();
        return !Net.IsRunning || Net.IsServer;
    }
    static void Postfix(CrystalBehaviour __instance) {
        if (Net.IsServer) CrystalSync.Get(__instance).AppearanceInitialized();
    }
}
[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.ChunkUpdate))]
internal static class CrystalSync_UpdatePatch { static bool Prefix() => !Net.IsRunning; }
[HarmonyPatch(typeof(CrystalUnstable), nameof(CrystalUnstable.Update))]
internal static class CrystalSync_UnstableExplosionPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        foreach (var code in instructions) {
            if (code.Calls(AccessTools.Method(typeof(WorldGeneration), nameof(WorldGeneration.CreateExplosion)))) {
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code);
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(CrystalSync), nameof(CrystalSync.ExplodeUnstable));
            }
            yield return code;
        }
    }
}
[HarmonyPatch(typeof(CrystalTemperature), nameof(CrystalTemperature.Start))]
internal static class CrystalSync_HeaterPatch {
    static void Postfix(CrystalTemperature __instance) {
        if (Net.IsServer && __instance.crystal != null) __instance.crystal.GetOrAddComponent<HeaterMultiplayerRewrite>();
    }
}
[HarmonyPatch(typeof(CrystalEnemy), "FixedUpdate")]
internal static class CrystalSync_EnemyPhysicsPatch { static bool Prefix(CrystalEnemy __instance) => !Net.IsRunning || Net.IsServer && CrystalSync.EnemyTarget(__instance) != null; }

// Scope LastAttacker's lifetime without changing the shared combat implementation.
[HarmonyPatch(typeof(Body), nameof(Body.Attack))]
internal static class CrystalSync_MeleeScopePatch {
    [HarmonyPriority(Priority.First)]
    static void Prefix(Body __instance, out Body __state) { __state = CrystalSync.MeleeAttacker; CrystalSync.MeleeAttacker = __instance; }
    static Exception Finalizer(Body __state, Exception __exception) { CrystalSync.MeleeAttacker = __state; return __exception; }
}
// The MP combat replacements bypass vanilla SendMessage("BuildingHit").
[HarmonyPatch(typeof(Body_Attack_MultiplayerPatch), nameof(Body_Attack_MultiplayerPatch.DamageBuilding))]
internal static class CrystalSync_MeleeHitPatch {
    static void Postfix(Body attacker, AttackInfo atk, BuildingEntity be) {
        if (Net.IsServer && be != null && be.TryGetComponent<CrystalBehaviour>(out var c)) CrystalSync.Hit(c, attacker, atk);
    }
}
[HarmonyPatch(typeof(TurretScript_Shoot_MultiplayerPatch), nameof(TurretScript_Shoot_MultiplayerPatch.Stolen_ShootApplyDamageBuilding))]
internal static class CrystalSync_GunHitPatch {
    static void Postfix(BuildingEntity b, TrackerGunScript gst) {
        if (Net.IsServer && b != null && b.TryGetComponent<CrystalBehaviour>(out var c)) CrystalSync.Hit(c, gst.pbody?.body, new AttackInfo { unarmed = false });
    }
}
[HarmonyPatch(typeof(TurretScript_Shoot_MultiplayerPatch), nameof(TurretScript_Shoot_MultiplayerPatch.Shoot_DidHitABuilding))]
internal static class CrystalSync_WorldShotPatch {
    static void Postfix(BuildingEntity building, bool __result) {
        if (Net.IsServer && !__result && building != null && building.TryGetComponent<CrystalBehaviour>(out var c))
            CrystalSync.Hit(c, TurretScript_Shoot_MultiplayerPatch.LastActiveShooter?.body, new AttackInfo { unarmed = false });
    }
}
#endif
