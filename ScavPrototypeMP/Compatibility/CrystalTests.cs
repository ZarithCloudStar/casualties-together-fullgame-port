#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Unity.VisualScripting;

namespace Together;

// Opt-in integration fixture. Uses real Unity prefabs, colliders, registry,
// LiteNetLib receivers and MedicalSync. No fixture is compiled into ShipFullgame.
internal static class CrystalTests {
    internal static bool Enabled => Environment.GetCommandLineArgs().Contains("--mp-crystal-test");
    static bool starting, started;
    static string Folder => Environment.GetEnvironmentVariable("CT_CRYSTAL_TEST_DIR");
    internal static int contacts, collisions, claims, activations, feedback;
    internal static readonly Dictionary<byte, int> activationsByKind = new();
    internal static readonly HashSet<byte> feedbackKinds = new();
    static int lastStage;
    internal sealed class Stage { public int number; public ushort id; public byte version; public string action; public ushort item; }
    internal static void Tick() {
        if (!Enabled || string.IsNullOrEmpty(Folder)) return;
        SteamManager.disableAchievements = true;
        if (!Net.IsRunning) return;
        if (!Util.IsInWorld()) {
            if (Net.IsClient && CharacterLobby.ShouldShowPanel) CharacterLobby.ConfirmPanel();
            if (Net.IsServer && !starting && !CharacterReadyGate.Blocked && ScavPlayer.ClientIdToPlayerDict.Count >= 2 && PreRunScript.instance != null) {
                starting = true; PreRunScript.instance.StartRun();
            }
        }
        if (started || !Util.IsWorldGenerated() || ScavPlayer.LocalPlayer?.body == null) return;
        started = true;
        Util.StartCoroutine(Net.IsServer ? Server() : Client());
    }
    static void Check(bool result, string message) {
        if (!result) throw new Exception("[crystal-test] FAIL " + message);
        log.l("[crystal-test] PASS " + message);
    }
    static CrystalBehaviour Spawn(CrystalEffect effect, Vector2 position) {
        var go = Utils.Create("crystalmedium", position, 0f);
        var c = go.GetComponent<CrystalBehaviour>();
        Check(c != null, "Utils.Create crystalmedium contains CrystalBehaviour");
        c.crystalSize = 1f;
        c.effects = new List<CrystalEffect> { effect };
        effect.crystal = c; effect.Start();
        c.build.health = 2000f;
        var rb = go.GetComponent<Rigidbody2D>();
        if (rb != null) { rb.bodyType = RigidbodyType2D.Static; c.build.ignoreBodyOptimize = true; }
        c.CheckCurrentChunk(WorldGeneration.world.GetClosestChunkScript(position));
        NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(go);
        CrystalSync.Get(c).SendState();
        return c;
    }
    static void StageClient(int number, CrystalBehaviour c, string action, ushort item = 0) {
        NetObjectRegistry.TryGetSyncInfo(c, out var si);
        // Each stage is immutable; a ready marker closes the writer/reader
        // race without requiring File.Replace support in Unity's Mono runtime.
        string path = Path.Combine(Folder, "stage-" + number + ".json");
        File.WriteAllText(path, JsonConvert.SerializeObject(new Stage { number = number, id = si.syncId.id, version = si.Version, action = action, item = item }));
        File.WriteAllText(path + ".ready", "ready");
    }
    static IEnumerator Server() {
        yield return new WaitForSecondsRealtime(5);
        var host = ScavPlayer.LocalPlayer;
        var guest = ScavPlayer.ClientIdToPlayerDict.Values.First(p => !p.IsLocal && p.body != null);
        Vector2 origin = host.pos + Vector2.right * 18f;
        guest.pos = host.pos + Vector2.right * 4f;
        yield return new WaitForSecondsRealtime(2);
        // Immediate duplicate-entry assertions avoid physiology drift and prove
        // shared cooldowns/one-shot flags serialize simultaneous bodies.
        var burning = Spawn(new CrystalBurning(), origin);
        yield return new WaitForSecondsRealtime(2);
        float temp = host.body.temperature;
        CrystalSync.Contact(burning, host.body.gameObject);
        CrystalSync.Contact(burning, host.body.gameObject);
        Check(Mathf.Abs(host.body.temperature - temp - .2f) < .001f, "burn host duplicate contact settles once (+0.2 temperature)");
        Check(burning.GetComponent<BodyGetterOverrider>().body == null && CrystalSync.TriggerBody == null, "contact restores trigger scope");
        burning.GetComponent<Collider2D>().enabled = false;
        var remoteBurn = Spawn(new CrystalBurning(), origin + Vector2.right * 4f);
        StageClient(1, remoteBurn, "collision");
        float remoteTemperature = guest.body.temperature;
        float burnDeadline = Time.unscaledTime + 20;
        while (((CrystalBurning)remoteBurn.effects[0]).cooldown <= 0f && Time.unscaledTime < burnDeadline) yield return null;
        Check(((CrystalBurning)remoteBurn.effects[0]).cooldown > 0f && guest.body.temperature > remoteTemperature + .1f,
            "guest real collision burns the guest body, not the host camera body");
        remoteBurn.GetComponent<Collider2D>().enabled = false;
        guest.Server_HealCharacter();
        // Client moves into the actual prefab collider. Both local and stamped
        // server contacts can arrive; a single activation must win the cooldown.
        var electric = Spawn(new CrystalElectric(), origin + Vector2.right * 8f);
        StageClient(2, electric, "collision");
        activationsByKind.TryGetValue(2, out int initial);
        int initialClaims = claims;
        float deadline = Time.unscaledTime + 20;
        while (((CrystalElectric)electric.effects[0]).cooldown <= 0f && Time.unscaledTime < deadline) yield return null;
        Check(activationsByKind.TryGetValue(2, out var electricCount) && electricCount == initial + 1 && ((CrystalElectric)electric.effects[0]).cooldown > 0, "electric remote body collision activates once");
        yield return new WaitForSecondsRealtime(.3f);
        Check(activationsByKind[2] == initial + 1, "electric host/client collision reports do not double settle");
        Check(collisions > 0, "Unity OnCollisionEnter2D entered crystal dispatch");
        // A fresh healing crystal arbitrates two users in the same frame.
        var healing = Spawn(new CrystalHealing(), origin + Vector2.right * 16f);
        host.body.brainHealth = 50; guest.body.brainHealth = 50;
        CrystalSync.Contact(healing, host.body.gameObject);
        CrystalSync.Contact(healing, guest.body.gameObject);
        Check(Mathf.Abs(host.body.brainHealth - 55) < .001f && guest.body.brainHealth == 50,
            "healing simultaneous host/guest contact chooses first body once");
        Check(((CrystalHealing)healing.effects[0]).activated, "healing one-shot consumed");
        // A second crystal exercises remote healing and client feedback.
        var remoteHeal = Spawn(new CrystalHealing(), origin + Vector2.right * 24f);
        StageClient(3, remoteHeal, "claims");
        deadline = Time.unscaledTime + 20;
        while (!((CrystalHealing)remoteHeal.effects[0]).activated && Time.unscaledTime < deadline) yield return null;
        Check(((CrystalHealing)remoteHeal.effects[0]).activated && claims > initialClaims, "healing remote contact traversed LiteNetLib receiver");
        StageClient(4, remoteHeal, "verify-heal");
        deadline = Time.unscaledTime + 10;
        while (!File.Exists(Path.Combine(Folder, "client-heal.done")) && Time.unscaledTime < deadline) yield return null;
        Check(File.Exists(Path.Combine(Folder, "client-heal.done")), "guest confirmed medical healing and one-shot presentation state");
        // Melee replacement must notify crystal even though vanilla BuildingHit
        // was skipped. Directly exercise that real replacement with explicit owner.
        var emp = Spawn(new CrystalEMP(), origin + Vector2.right * 32f);
        // Reuse the console helper directly; the user-facing command requires
        // CheatsEnabled and may decline to run in the default test lobby.
        guest.Server_HealCharacter();
        guest.body.ForceStand();
        Con.SpawnThingOnPlayer("smallbattery", guest.body, true);
        var batteries = guest.body.GetAllItemsThorough().Where(i => i.Stats is BatteryInfo).ToArray();
        Check(batteries.Length > 0, "guest inventory contains battery for EMP assertion");
        foreach (var item in batteries) item.condition = 1f;
        Body_Attack_MultiplayerPatch.DamageBuilding(guest.body, new AttackInfo { unarmed = true, structuralDamage = 0 }, emp.build, 1f, false, emp.transform.position);
        Body_Attack_MultiplayerPatch.DamageBuilding(host.body, new AttackInfo { unarmed = true, structuralDamage = 0 }, emp.build, 1f, false, emp.transform.position);
        Check(((CrystalEMP)emp.effects[0]).activated && batteries.All(i => Mathf.Abs(i.condition - .67f) < .001f), "EMP melee replacement drains guest once (0.33), second attacker cannot drain again");
        var hostEMP = Spawn(new CrystalEMP(), origin + Vector2.right * 36f);
        Body_Attack_MultiplayerPatch.DamageBuilding(host.body, new AttackInfo { unarmed = true, structuralDamage = 4000f }, hostEMP.build, 1f, false, hostEMP.transform.position);
        Check(((CrystalEMP)hostEMP.effects[0]).activated, "host lethal melee hit still dispatches crystal effects");
        // Prop/world hit must not inherit LastAttacker or a cached contact body.
        var worldEMP = Spawn(new CrystalEMP(), origin + Vector2.right * 40f);
        float condition = batteries[0].condition;
        CrystalSync.Hit(worldEMP, null, new AttackInfo { unarmed = false });
        Check(((CrystalEMP)worldEMP.effects[0]).activated && batteries[0].condition == condition, "ownerless world hit consumes EMP without draining stale attacker");
        Check(CrystalSync.TriggerBody == null && CrystalSync.MeleeAttacker == null, "trigger and melee contexts clear after events");
        NetObjectRegistry.TryGetSyncInfo(batteries[0], out var batterySi);
        StageClient(5, emp, "verify-emp", batterySi.syncId.id);
        yield return new WaitForSecondsRealtime(5);
        log.l($"[crystal-test] server complete contacts={contacts} collisions={collisions} claims={claims} activations={activations}");
        File.WriteAllText(Path.Combine(Folder, "server.done"), "PASS");
    }
    static IEnumerator Client() {
        float deadline = Time.unscaledTime + 120;
        while (Time.unscaledTime < deadline) {
            string path = Path.Combine(Folder, "stage-" + (lastStage + 1) + ".json");
            if (!File.Exists(path + ".ready")) { yield return null; continue; }
            var stage = JsonConvert.DeserializeObject<Stage>(File.ReadAllText(path));
            if (stage.number <= lastStage || !CrystalSync.TryCrystal((knetid)stage.id, stage.version, out var c) || c.effects == null || c.effects.Count == 0) { yield return null; continue; }
            lastStage = stage.number;
            Check(c.effects.Count == 1, "client effect configuration initialized from host stage=" + stage.number);
            if (stage.action == "collision") {
                var col = c.GetComponent<Collider2D>();
                var target = (Vector2)col.bounds.center;
                ScavPlayer.LocalPlayer.pos = target + Vector2.up * 5f;
                ScavPlayer.LocalPlayer.body.Stand();
                yield return new WaitForSecondsRealtime(.5f);
                ScavPlayer.LocalPlayer.pos = target;
            } else if (stage.action == "claims") {
                ScavPlayer.LocalPlayer.pos = c.transform.position;
                yield return new WaitForSecondsRealtime(.3f);
                // Duplicated network claims also exercise server-side guards.
                for (int n = 0; n < 2; n++) {
                    var w = Net.CreateWriter(NetmsgId.SERVER_CrystalContact); w.Put((knetid)stage.id); w.Put(stage.version);
                    Net.Client_Send(LiteNetLib.DeliveryMethod.ReliableOrdered, w);
                }
            } else if (stage.action == "verify-heal") {
                yield return new WaitForSecondsRealtime(1);
                Check(((CrystalHealing)c.effects[0]).activated && c.sprite.color.maxColorComponent < .01f,
                    "client received consumed healing state and black crystal renderer");
                Check(ScavPlayer.LocalPlayer.body.brainHealth > 53f && ScavPlayer.LocalPlayer.body.brainHealth < 57f,
                    "guest medical sync received one heal (brainHealth approximately 55, not 59.5)");
                Check(feedbackKinds.Contains(2) && feedbackKinds.Contains(4), "client received electric and healing feedback");
                File.WriteAllText(Path.Combine(Folder, "client-heal.done"), "PASS");
            } else {
                yield return new WaitForSecondsRealtime(2);
                Check(((CrystalEMP)c.effects[0]).activated && !((CrystalEMP)c.effects[0]).part.activeSelf,
                    "client received consumed EMP state and hidden Tesla visual");
                Check(NetObjectRegistry.TryGetSyncInfo((knetid)stage.item, out var itemSi) && itemSi.item != null && Mathf.Abs(itemSi.item.condition - .67f) < .02f,
                    "client received authoritative EMP battery condition approximately 0.67");
                Check(feedbackKinds.Contains(1) && feedbackKinds.Contains(3), "client received burning and EMP feedback");
                int children = c.transform.childCount;
                yield return new WaitForSecondsRealtime(4.2f);
                Check(c.transform.childCount == children && ((CrystalEMP)c.effects[0]).activated,
                    "repeated authoritative snapshots preserve spent state without replaying Start/duplicating visuals");
                log.l($"[crystal-test] client complete feedback={feedback} contacts={contacts} collisions={collisions}");
                File.WriteAllText(Path.Combine(Folder, "client.done"), "PASS");
                yield break;
            }
        }
        Check(false, "client fixture timeout");
    }
}
[HarmonyPatch(typeof(Plugin), "Update")]
internal static class CrystalTests_TickPatch { static void Postfix() => CrystalTests.Tick(); }
[HarmonyPatch(typeof(EndingFlowTests), nameof(EndingFlowTests.Tick))]
internal static class CrystalTests_SkipEndingFixture { static bool Prefix() => !CrystalTests.Enabled; }
#endif
