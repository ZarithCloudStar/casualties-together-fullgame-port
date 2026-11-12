#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using static Together.DropChipRegression;

namespace Together;

// Real loot producers and keypad RPCs, not a hand-added FreshItemDrop test.
internal static class DropSourceRegression {
    static ScavPlayer heldGuest;
    static Vector2 heldAt;
    static ScavPlayer heldHost;
    static Vector2 hostAt;
    internal static void Tick() {
        if (Net.IsServer && heldGuest?.body != null && Util.IsInWorld()) {
            if (!heldGuest.body.standing) heldGuest.body.Stand(true);
            heldGuest.netBody.SetBodyPosition(heldAt);
            heldGuest.body.SetVelocity(Vector2.zero);
        }
        if (Net.IsServer && heldHost?.body != null && Util.IsInWorld()) {
            if (!heldHost.body.standing) heldHost.body.Stand(true);
            heldHost.netBody.SetBodyPosition(hostAt);
            heldHost.body.SetVelocity(Vector2.zero);
        }
        if (Net.IsClient && heldGuest?.body != null && Util.IsInWorld()) {
            if (!heldGuest.body.standing) heldGuest.body.Stand(true);
            heldGuest.netBody.SetBodyPosition(heldAt);
            heldGuest.body.SetVelocity(Vector2.zero);
        }
    }
    static string Side => Net.IsServer ? "host" : "client";
    static void Mark(string name) => File.WriteAllText(FileFor(name), "PASS");
    static void Announce(string name, IEnumerable<SyncInfo> items) {
        string path = FileFor(name + ".ids");
        // A receiver can see File.Exists before WriteAllLines releases its
        // Windows handle. Publish only a completed file between processes.
        File.WriteAllLines(path + ".tmp", items.Select(s => s.syncId.id.ToString()));
        File.Move(path + ".tmp", path);
    }
    static SyncInfo[] ReadItems(string name) => File.ReadAllLines(FileFor(name + ".ids")).Select(s => {
        NetObjectRegistry.TryGetSyncInfo((knetid)ushort.Parse(s), out var si); return si;
    }).ToArray();

    internal static IEnumerator Run() {
        yield return Wait(() => ScavPlayer.ClientIdToPlayerDict.Values.Count(p => p?.body != null) == 2 && !DeferredPlayerSaveRestore.InProgress, "two ready restored bodies");
        yield return new WaitForSecondsRealtime(2f);
        var guest = ScavPlayer.ClientIdToPlayerDict.Values.First(p => !p.IsHost);
        bool expectedChip = !(Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") ?? "").Contains("legacy");
        Check(guest.modUnchipped == expectedChip && guest.characterSelected == 2, Side + " authoritative guest chip=" + expectedChip + " character=2");
        bool hostUnchipped = (Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") ?? "").Contains("host-unchipped");
        Check(ScavPlayer.ClientIdToPlayerDict.Values.First(p => p.IsHost).modUnchipped == hostUnchipped, Side + " host chip remains independent");
        Check(Multiplayer.rules.UnchippedIsIndividual, Side + " per-player chip rule enabled");
        Check(ScavPlayer.ClientIdToPlayerDict.Values.All(p => p.unchipped == p.modUnchipped && p.netBody.unchipped == p.modUnchipped), Side + " gameplay and NetBody chip states match choices");
        var local = ScavPlayer.LocalPlayer;
        Check(WorldGeneration.unchipped == local.modUnchipped, Side + " local vanilla world chip matches choice");
        Check(PlayerCamera.main.depthMeter.activeSelf == !local.modUnchipped, Side + " visible depth meter matches choice unchipped=" + local.modUnchipped);
        ScreenCapture.CaptureScreenshot(FileFor(Side + "-chip-gameplay.png"));
        if (Net.IsServer) {
            Check(IsServerChip(guest) == expectedChip && ChipState.HostIsUnchipped == hostUnchipped, "server query reads per-player saved chips");
            Check(CharacterRunMemory.GetChip(CharacterRunMemory.PlayerKey(guest.SteamId, guest.playerName)) == expectedChip, "chip is stored per save");
        } else {
            Check(ChipState.LocalUnchipped == expectedChip, "client follows restored save chip");
            // The receiver must override this local preference at admission, too.
            if (Reloading) Check(!CharacterLobby.ShouldShowPanel, "saved reconnect admitted without panel");
            Item.itemFloating = false; // a differing local setting cannot cancel server floating
        }
        if (Reloading || hostUnchipped) {
            Mark(Side + ".done");
            yield break;
        }
        if (Net.IsClient) {
            // Exercise old payloads and a malformed chip over the real receiver.
            var bad = Net.CreateWriter(NetmsgId.SERVER_SetCharacter);
            bad.Put((byte)2); bad.Put(true); bad.Put((byte)2);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, bad);
            var old = Net.CreateWriter(NetmsgId.SERVER_SetCharacter);
            old.Put((byte)2);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, old);
            var replay = Net.CreateWriter(NetmsgId.SERVER_SetCharacter);
            replay.Put((byte)2); replay.Put(false);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, replay);
            yield return new WaitForSecondsRealtime(.5f);
            Mark("chip-packets.sent");
        } else {
            yield return Wait(() => File.Exists(FileFor("chip-packets.sent")), "legacy and malformed chip packets");
            Check(guest.characterChosen && guest.modUnchipped && CharacterRunMemory.GetChip(CharacterRunMemory.PlayerKey(guest.SteamId, guest.playerName)),
                "malformed chip rejected; old choice and delayed unconfirmed replay preserve saved chip");
        }
        if (Net.IsServer) yield return ServerSources(guest);
        else yield return ClientSources();
    }
    static bool IsServerChip(ScavPlayer p) => ChipState.IsUnchipped(p);

    static IEnumerator Observe(string name, SyncInfo[] items) {
        Check(items.Length > 0 && items.All(s => s?.go != null), Side + " " + name + " real replicated loot");
        var probes = items.Select(s => s.go.GetComponent<DropFrameProbe>() ?? s.go.AddComponent<DropFrameProbe>()).ToArray();
        float start = Time.time;
        var records = new List<string> { "elapsed,id,x,y,serverX,serverY,ttl,gravity,component" };
        var stable = new Dictionary<ushort, Vector2>();
        float maxDrift = 0f, maxGap = 0f;
        var componentIds = items.ToDictionary(s => s.syncId.id, s => new HashSet<int>());
        while (Time.time - start < 12f) {
            float age = Time.time - start;
            foreach (var si in items) {
                var fresh = si.go.GetComponent<FreshItemDrop>();
                if (fresh != null) componentIds[si.syncId.id].Add(fresh.GetInstanceID());
                var rb = si.go.GetComponent<Rigidbody2D>();
                var pos = (Vector2)si.go.transform.position;
                var server = si._position.Value;
                records.Add(string.Join(",", new[] { age.ToString("F3", CultureInfo.InvariantCulture), si.syncId.id.ToString(), pos.x.ToString("F3", CultureInfo.InvariantCulture), pos.y.ToString("F3", CultureInfo.InvariantCulture), server.x.ToString("F3", CultureInfo.InvariantCulture), server.y.ToString("F3", CultureInfo.InvariantCulture), (fresh?.timeLeft ?? -1).ToString("F3", CultureInfo.InvariantCulture), rb.gravityScale.ToString("F3", CultureInfo.InvariantCulture), (fresh?.GetInstanceID() ?? 0).ToString() }));
                if (age > 2f && age < 6f) {
                    CheckQuiet(fresh != null && fresh.timeLeft > 2f && rb.gravityScale == 0f, name + " lost hover before native ramp");
                    if (!stable.ContainsKey(si.syncId.id)) stable[si.syncId.id] = pos;
                    maxDrift = Mathf.Max(maxDrift, Vector2.Distance(stable[si.syncId.id], pos));
                    maxGap = Mathf.Max(maxGap, Vector2.Distance(server, pos));
                }
            }
            yield return new WaitForSeconds(.1f);
        }
        File.WriteAllLines(FileFor(Side + "-" + name + ".csv"), records);
        log.l($"[drop-test] {Side} {name} stableDrift={maxDrift:0.000} authorityGap={maxGap:0.000}");
        Check(maxDrift < .65f && maxGap < .65f, Side + " " + name + " positions stay suspended and agree with authority");
        Check(items.All(si => si.go.GetComponent<FreshItemDrop>() == null && Mathf.Abs(si.go.GetComponent<Rigidbody2D>().gravityScale - 1f) < .01f), Side + " " + name + " expired once at native lifetime");
        Check(items.All(si => !si.freshitemdrop.Value && si.freshDropTenths.Value == 0), Side + " " + name + " terminal snapshot false/0");
        Check(componentIds.Values.All(ids => ids.Count == 1), Side + " " + name + " exactly one component through expiry and post-expiry observation");
        foreach (var probe in probes) UnityEngine.Object.Destroy(probe);
    }
    static void CheckQuiet(bool value, string message) {
        if (!value) throw new Exception("[drop-test] FAIL " + Side + " " + message);
    }
    static IEnumerator ServerSources(ScavPlayer guest) {
        var host = ScavPlayer.LocalPlayer;
        Item.itemFloating = true;
        host.body.forceNoMovement = true;
        heldHost = host; hostAt = new Vector2(0f, WorldGeneration.world.halfHeight - 40f);
        host.Server_TeleportCharacter(hostAt);
        yield return new WaitForSecondsRealtime(.5f);
        foreach (var name in new[] { "dropcapsule", "medcrate", "leadbush", "host-control" }) {
            var point = hostAt + Vector2.right * (name == "host-control" ? 3f : 40f) + Vector2.up * 3f;
            // Generated rock can eject a teleported test body between physics
            // and Start. Make a real empty arena instead of testing inside rock.
            var center = WorldGeneration.world.WorldToBlockPos(point);
            for (int x = -12; x <= 12; x++)
                for (int y = -12; y <= 12; y++)
                    WorldGeneration.world.SetBlock(center + new Vector2Int(x, y), 0);
            guest.body.Heal(); guest.body.forceNoMovement = true;
            guest.Server_TeleportCharacter(point + Vector2.left * 2f);
            heldGuest = guest; heldAt = point + Vector2.left * 2f;
            guest.body.SetVelocity(Vector2.zero);
            yield return new WaitForSecondsRealtime(.5f);
            var source = Utils.Create(name == "host-control" ? "medcrate" : name, point, 0f);
            NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(source);
            yield return new WaitForSecondsRealtime(.5f);
            var before = UnityEngine.Object.FindObjectsOfType<Item>().Select(i => i.GetInstanceID()).ToHashSet();
            Check(NetObjectRegistry.TryGetSyncInfo(source, out var building), name + " source registered");
            if (name == "dropcapsule") {
                File.WriteAllText(FileFor("keypad.id.tmp"), building.syncId.id.ToString());
                File.Move(FileFor("keypad.id.tmp"), FileFor("keypad.id"));
                yield return Wait(() => source == null, "guest completes keypad via network");
            } else {
                // Same authoritative damage/death entry used by opening and mining.
                source.GetComponent<BuildingEntity>().SetHealth(0f);
            }
            yield return null;
            var loot = UnityEngine.Object.FindObjectsOfType<Item>().Where(i => !before.Contains(i.GetInstanceID()) && i.transform.parent == null && Vector2.Distance(i.transform.position, point) < 12f).ToArray();
            log.l($"[drop-test] {name} loose loot={string.Join(",", loot.Select(i => i.id + ":" + (i.GetComponent<FreshItemDrop>() != null)))}");
            Check(loot.Length > 0 && loot.All(i => i.GetComponent<FreshItemDrop>() != null), name + " server producer created native float components");
            foreach (var item in loot) NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(item.gameObject);
            var items = loot.Select(i => { NetObjectRegistry.TryGetSyncInfo(i, out var si); return si; }).ToArray();
            Announce(name, items);
            yield return Observe(name, items);
            yield return Wait(() => File.Exists(FileFor(name + ".client")), name + " client observations");
        }
        // Pick up during floating: the native parent rule removes the component.
        var pickup = Utils.Create("waterbottle", guest.pos + Vector2.up * 2f, 0f).GetComponent<Item>();
        pickup.gameObject.AddComponent<FreshItemDrop>();
        NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(pickup.gameObject);
        NetObjectRegistry.TryGetSyncInfo(pickup, out var pickInfo);
        Announce("pickup", new[] { pickInfo });
        yield return new WaitForSecondsRealtime(1f);
        guest.body.PickUpItem(pickup, 0, true);
        yield return new WaitForSecondsRealtime(1f);
        Check(pickup.GetComponent<FreshItemDrop>() == null, "pickup ends host float");
        Check(!pickInfo.freshitemdrop.Value && pickInfo.freshDropTenths.Value == 0, "pickup publishes false/0 on host");
        Mark("pickup.ready");
        yield return Wait(() => File.Exists(FileFor("pickup.client")), "client pickup");
        yield return Wait(() => File.Exists(FileFor("stale.client")), "client stale terminal snapshot regression");
        var sleepy = Utils.Create("waterbottle", guest.pos + Vector2.up * 2f, 0f);
        var lifetime = sleepy.AddComponent<FreshItemDrop>();
        lifetime.timeLeft = 1f;
        NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(sleepy);
        NetObjectRegistry.TryGetSyncInfo(sleepy, out var sleepyInfo);
        sleepyInfo.WriteObjectIntoPacket();
        sleepyInfo.tracker.enabled = false;
        Announce("no-tracker", new[] { sleepyInfo });
        yield return new WaitForSeconds(1.5f);
        Check(sleepy.GetComponent<FreshItemDrop>() == null && !sleepyInfo.freshitemdrop.Value && sleepyInfo.freshDropTenths.Value == 0,
            "server publishes expiry false/0 with snapshot tracker disabled");
        yield return Wait(() => File.Exists(FileFor("no-tracker.client")), "disabled tracker terminal reaches client");
        sleepyInfo.tracker.enabled = true;
        Check(EndingFlow.EnableEscape, "Escape is enabled for Release 14");
        Mark("save.ready");
        heldGuest = null;
        heldHost = null;
        Check(SaveCurrentLayerExit.Request(), "save and exit request");
        yield return Wait(() => Util.IsInMainMenu() && !SaveCurrentLayerExit.IsSaving, "save and exit completes");
        Check(File.Exists(Path.Combine(SavesystemPatch.mpsavefolder, "save.sv")), "world save written for process restart");
        Mark("host.done");
    }
    static IEnumerator ClientSources() {
        yield return Wait(() => File.Exists(FileFor("keypad.id")), "keypad target");
        var target = (knetid)ushort.Parse(File.ReadAllText(FileFor("keypad.id")));
        yield return Wait(() => NetObjectRegistry.TryGetSyncInfo(target, out var si) && si.go != null, "keypad mesh");
        NetObjectRegistry.TryGetSyncInfo(target, out var building);
        // Pin the local body too: server teleporting a remote body does not stop
        // its owner simulating gravity and leaving the minigame during typing.
        heldGuest = ScavPlayer.LocalPlayer;
        heldAt = (Vector2)building.go.transform.position + Vector2.left * 2f;
        building.go.GetComponent<Openable>().OnUse();
        yield return Wait(() => MinigameBase.main.currentMinigame is KeypadMinigame keypad && !string.IsNullOrEmpty(keypad.match) && keypad.match.All(char.IsDigit), "server keypad code");
        var game = (KeypadMinigame)MinigameBase.main.currentMinigame;
        foreach (char digit in game.match) {
            var writer = Net.CreateWriter(NetmsgId.SERVER_KeypadMinigame_Keypress);
            writer.Put(target); writer.Put((byte)(digit - '0'));
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            yield return new WaitForSecondsRealtime(.06f);
        }
        yield return Wait(() => building.go == null, "keypad opened by authoritative RPC");
        heldGuest = null;
        foreach (var name in new[] { "dropcapsule", "medcrate", "leadbush", "host-control" }) {
            yield return Wait(() => File.Exists(FileFor(name + ".ids")) && ReadItems(name).All(s => s?.go != null && s.go.GetComponent<FreshItemDrop>() != null), name + " synced floats");
            yield return Observe(name, ReadItems(name));
            Mark(name + ".client");
        }
        yield return Wait(() => File.Exists(FileFor("pickup.ready")), "pickup state");
        var items = ReadItems("pickup");
        Check(items.All(si => si?.go != null && si.go.GetComponent<FreshItemDrop>() == null && si.container_data1.Value != 0), "pickup ends client float and keeps inventory sync");
        Mark("pickup.client");
        // Freeze the exact reported true/1 input at the real replica receiver.
        // Repeated input must expire locally even if the terminal packet is late.
        var stale = ReadItems("host-control")[0];
        var update = AccessTools.Method(typeof(SyncInfo), "UpdateItem");
        var ids = new HashSet<int>();
        float until = Time.time + 2f;
        while (Time.time < until) {
            stale.freshitemdrop.Value = true;
            stale.freshDropTenths.Value = 1;
            update.Invoke(stale, null);
            var fresh = stale.go.GetComponent<FreshItemDrop>();
            if (fresh != null) ids.Add(fresh.GetInstanceID());
            yield return null;
        }
        Check(ids.Count == 1 && stale.go.GetComponent<FreshItemDrop>() == null, "stale true/0.1s expires locally and never rebuilds for two seconds");
        stale.freshitemdrop.Value = false; stale.freshDropTenths.Value = 0;
        update.Invoke(stale, null);
        stale.freshitemdrop.Value = true; stale.freshDropTenths.Value = 100;
        update.Invoke(stale, null);
        Check(stale.go.GetComponent<FreshItemDrop>() != null, "new false-to-true episode can float the same sync item again");
        stale.freshitemdrop.Value = false; stale.freshDropTenths.Value = 0;
        update.Invoke(stale, null);
        Mark("stale.client");
        yield return Wait(() => File.Exists(FileFor("no-tracker.ids")) && ReadItems("no-tracker").All(si => si?.go != null), "disabled tracker drop replica");
        yield return new WaitForSeconds(2f);
        Check(ReadItems("no-tracker").All(si => !si.freshitemdrop.Value && si.freshDropTenths.Value == 0 && si.go.GetComponent<FreshItemDrop>() == null),
            "client receives false/0 and stays expired with host snapshot tracker disabled");
        Mark("no-tracker.client");
        yield return Wait(() => File.Exists(FileFor("save.ready")) && Util.IsInMainMenu(), "client follows save and exit");
        Mark("client.done");
    }
}

// NetBody applies remote poses between Update and physics. Pin at the exact
// validation step too, so the fixture tests reach checks at a stable position.
[HarmonyPatch(typeof(MinigameMPManager), "FixedUpdate")]
internal static class DropSourcePinBeforeValidation {
    [HarmonyPriority(Priority.First)]
    static void Prefix() {
        if (DropChipRegression.Active) DropSourceRegression.Tick();
    }
}

[HarmonyPatch(typeof(KeypadMinigame), nameof(KeypadMinigame.CheckCasts))]
internal static class DropSourcePinBeforeClientKeypad {
    [HarmonyPriority(Priority.First)]
    static void Prefix() {
        if (DropChipRegression.Active) DropSourceRegression.Tick();
    }
}
#endif
