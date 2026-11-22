#if FULLGAME && TESTING
using System;
using System.Linq;
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;

namespace Together;

// TESTING only: fill the joining client with max skills, backpacks and the
// belt loadout used by the item-drop regression test.
internal static class MultiUserSetup {
    static readonly bool Enabled = Environment.GetEnvironmentVariable("CT_MULTI_SETUP") == "1";
    static int stage;
    static bool clientSkilled;
    static bool hostSkilled;
    static bool clientConfirmed;
    static bool hostConfirmed;
    static void Log(string text) => Plugin.log.LogInfo("[multi-setup] " + text);

    internal static void Tick() {
        if (!Enabled || !Net.IsRunning || stage >= 99) return;
        if (Net.IsClient) {
            if (!clientConfirmed && CharacterLobby.ShouldShowPanel && CharacterLobby.ConfirmPanel())
                clientConfirmed = true;
            var local = ScavPlayer.LocalPlayer;
            if (!clientSkilled && local?.body != null) {
                SetSkills(local.body, "client");
                clientSkilled = true;
            }
            return;
        }
        if (!Net.IsServer || !Util.IsWorldGenerated()) return;
        if (!hostConfirmed && CharacterLobby.ShouldShowPanel && CharacterLobby.ConfirmPanel())
            hostConfirmed = true;
        Multiplayer.rules.sv_cheats = true;
        var host = ScavPlayer.LocalPlayer;
        if (!hostSkilled && host?.body != null) {
            SetSkills(host.body, "host");
            hostSkilled = true;
        }
        if (stage != 0) return;
        if (ScavPlayer.ClientIdToPlayerDict.Count < 2) return;
        var guest = ScavPlayer.ClientIdToPlayerDict.Values.FirstOrDefault(p => p != null && !p.IsHost && p.body != null);
        if (guest == null) return;
        SetSkills(guest.body, "guest");
        FillBackpacks(guest.body);
        FillBelt(guest.body);
        Log($"skills=30/30/30 guest={guest.playerName} containers={guest.body.GetComponentsInChildren<Container>(true).Length}");
        stage = 99;
    }

    static void SetSkills(Body body, string who) {
        if (body?.skills == null) return;
        body.skills.STR = 30;
        body.skills.RES = 30;
        body.skills.INT = 30;
        int xp = Skills.GetExperienceForLevel(30);
        body.skills.expSTR = xp;
        body.skills.expRES = xp;
        body.skills.expINT = xp;
        body.skills.UpdateExpBoundaries();
        Log($"set skills {who} -> 30/30/30 xp={xp}");
    }

    static Item Make(string id, Container box, Body body) {
        GameObject prefab = null;
        try { prefab = Resources.Load<GameObject>(id); } catch { }
        if (prefab == null) return null;
        GameObject copy = UnityEngine.Object.Instantiate(prefab, body.transform.position, Quaternion.identity);
        Item item = copy.GetComponent<Item>();
        if (item == null) { UnityEngine.Object.Destroy(copy); return null; }
        item.condition = 100f;
        NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(copy);
        if (box != null) {
            item.transform.SetParent(box.transform, false);
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            if (item.rb != null) item.rb.simulated = false;
        }
        return item;
    }

    static void FillBackpacks(Body body) {
        string[] bagIds = { "duffelbag", "toolbox", "foliagebag", "traumarig" };
        var ids = Item.GlobalItems.Keys.Where(id => !string.IsNullOrEmpty(id)).ToArray();
        int made = 0;
        foreach (string bagId in bagIds) {
            GameObject prefab = null;
            try { prefab = Resources.Load<GameObject>(bagId); } catch { }
            if (prefab == null) continue;
            GameObject copy = UnityEngine.Object.Instantiate(prefab, body.transform.position, Quaternion.identity);
            Item bag = copy.GetComponent<Item>();
            Container box = copy.GetComponent<Container>();
            if (bag == null || box == null) { UnityEngine.Object.Destroy(copy); continue; }
            bag.condition = 100f;
            NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(copy);
            int filled = 0;
            for (int i = 0; i < ids.Length && filled < 8; i++) {
                string id = ids[(i + made * 7) % ids.Length];
                if (id == bagId) continue;
                if (Make(id, box, body) != null) filled++;
            }
            bool placed = false;
            for (int slot = 0; slot < body.slots.Length && !placed; slot++) {
                if (body.HoldingItem(slot)) continue;
                bag.transform.position = body.transform.position;
                body.PickUpItem(bag, slot, true);
                placed = body.GetItem(slot) == bag;
            }
            if (!placed) {
                try { body.WearWearable(bag); placed = true; } catch { }
            }
            if (!placed) { bag.transform.position = (Vector2)body.transform.position + Vector2.up; bag.rb.simulated = true; }
            made++;
            Log($"backpack={bagId} items={filled} placed={placed}");
        }
    }

    static void FillBelt(Body body) {
        GameObject prefab = null;
        try { prefab = Resources.Load<GameObject>("belt"); } catch { }
        if (prefab == null) return;
        GameObject copy = UnityEngine.Object.Instantiate(prefab, body.transform.position, Quaternion.identity);
        Item belt = copy.GetComponent<Item>();
        Container box = copy.GetComponent<Container>();
        if (belt == null || box == null) { UnityEngine.Object.Destroy(copy); return; }
        belt.condition = 100f;
        NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(copy);
        string[] ids = { "machete", "boxof12gauge", "riflemagazine", "smallmagazine", "9mmround", "556round", "shotgun" };
        int filled = 0;
        foreach (string id in ids) if (Make(id, box, body) != null) filled++;
        bool placed = false;
        try { body.WearWearable(belt); placed = body.GetWearable(belt.id) == belt; } catch { }
        if (!placed) {
            for (int slot = 0; slot < body.slots.Length && !placed; slot++) {
                if (body.HoldingItem(slot)) continue;
                body.PickUpItem(belt, slot, true);
                placed = body.GetItem(slot) == belt;
            }
        }
        if (!placed) { belt.transform.position = (Vector2)body.transform.position + Vector2.up; belt.rb.simulated = true; }
        Log($"belt items={filled} placed={placed}");
    }
}

[HarmonyPatch(typeof(Plugin), "Update")]
internal static class MultiUserSetupTick { static void Postfix() => MultiUserSetup.Tick(); }
#endif
