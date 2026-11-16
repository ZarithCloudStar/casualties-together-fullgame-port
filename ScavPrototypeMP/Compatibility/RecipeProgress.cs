using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Together;

// INT unlocks belong to the team. hasMadeBefore is only a local UI projection.
internal static class RecipeProgress {
    static int[] baseIntel;
    static int revision = 1;
    static readonly Dictionary<knetid, int> sent = new();
    static bool sharedReceived, openPending;
    static float nextRequest;

    internal static void ResetNetwork() {
        sent.Clear(); revision++;
        sharedReceived = openPending = false; nextRequest = 0;
        PlayerCheckpoint.ResetCraftState();
    }

    internal static void RecipesCreated() {
        ResetNetwork();
        baseIntel = Recipes.recipes.Select(r => r?.INT ?? 0).ToArray();
    }

    static bool Valid(int index) => Recipes.recipes != null && index >= 0 && index < Recipes.recipes.Count && Recipes.recipes[index] != null;

    internal static void RestoreShared(JObject data) {
        if (!Net.IsServer || data["savedRecipeData"] == null || Recipes.recipes == null) return;
        var saved = data["savedRecipeData"].ToObject<SavedRecipeData>()?.saved;
        if (saved == null) return;
        for (int i = 0; i < Math.Min(saved.Length, Recipes.recipes.Count); i++)
            if (Recipes.recipes[i] != null) Recipes.recipes[i].INT = saved[i].Item2;
        revision++;
        PlayerCheckpoint.ApplyLocalCraftMarkers();
        log.l("[recipes] restored shared blueprint state unlocked=" + Recipes.recipes.Count(r => r != null && r.INT == 0));
    }

    internal static void Learn(Body body, Item item, int index) {
        if (!Valid(index)) return;
        if (Net.IsServer) LearnServer(index);
        else if (Net.IsClient && Util.IsBodyLocal(body)) {
            // Predict the panel; the server learns/broadcasts and persists the team table.
            Recipes.recipes[index].INT = 0;
            var writer = Net.CreateWriter(NetmsgId.SERVER_BlueprintLearn);
            writer.Put(index);
            writer.Put(ItemSync.TryGetSyncInfo(item, out var sync) ? sync.syncId : default(knetid));
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        } else if (!Net.IsRunning) Recipes.recipes[index].INT = 0;
    }

    static void LearnServer(int index) {
        if (!Valid(index) || Recipes.recipes[index].INT == 0) return;
        Recipes.recipes[index].INT = 0; revision++;
        Notify(index);
        Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_BlueprintThing, (knetid)index, true, false);
        log.l("[recipes] shared blueprint learned index=" + index);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_BlueprintLearn)]
    static void ServerLearn(knetid client, NetDataReader reader) {
        int index = reader.GetInt(); reader.Get(out knetid itemId);
        if (!Net.IsServer || !Valid(index) || !ScavPlayer.TryGetPlayerAndNetBodyFromClientId(client, out var player, out var nb) ||
            player.body == null || !player.body.alive || !player.body.conscious) return;
        // The normal item-use request still owns consumption and XP. Do not let
        // a forged recipe index unlock recipes without a matching reachable blueprint.
        if (!ItemSync.TryGetItemSyncInfo(itemId, out var sync) || sync.item == null || sync.item.id != "blueprint" ||
            !sync.item.TryGetComponent<BlueprintScript>(out var blueprint) || blueprint.recipeIndex != index ||
            !ItemSync.CheckIfBodyReachThisItem(sync, player.body)) return;
        LearnServer(index);
    }

    internal static void ReceiveLearned(int index) {
        if (!Net.IsClient || !Valid(index)) return;
        bool changed = Recipes.recipes[index].INT != 0;
        Recipes.recipes[index].INT = 0;
        if (changed) Notify(index);
        RefreshPanel();
    }

    internal static void Notify(int index) {
        if (PlayerCamera.main != null && Valid(index))
            PlayerCamera.main.DoAlert(Locale.GetOther("learnedrecipe").Replace("r1", Locale.GetItem(Recipes.recipes[index].simpleName)));
    }

    internal static void SendShared(knetid client, bool force = false) {
        // ForceLoad temporarily installs a personal save's global table. The
        // body-load finally restores the team table and sends the state then.
        if (!Net.IsServer || PlayerCheckpoint.RestoringBody || Recipes.recipes == null || (!force && sent.TryGetValue(client, out int prior) && prior == revision)) return;
        var unlocked = Recipes.recipes.Select((r, i) => (r, i)).Where(p => p.r != null && p.r.INT == 0).Select(p => p.i).ToArray();
        var writer = Net.CreateWriter(NetmsgId.CLIENT_BlueprintState);
        writer.Put((ushort)unlocked.Length);
        foreach (int index in unlocked) writer.Put(index);
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, client);
        sent[client] = revision;
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BlueprintState)]
    static void ReceiveShared(knetid _, NetDataReader reader) {
        int count = reader.GetUShort();
        var unlocked = new HashSet<int>();
        for (int i = 0; i < count; i++) { int index = reader.GetInt(); if (Valid(index)) unlocked.Add(index); }
        if (!Net.IsClient || Recipes.recipes == null) return;
        for (int i = 0; i < Recipes.recipes.Count; i++) {
            if (Recipes.recipes[i] == null) continue;
            if (unlocked.Contains(i)) Recipes.recipes[i].INT = 0;
            else if (baseIntel != null && i < baseIntel.Length) Recipes.recipes[i].INT = baseIntel[i];
        }
        sharedReceived = true;
        RefreshPanel();
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestRecipeState)]
    static void RequestState(knetid client, NetDataReader _) {
        if (!Net.IsServer || !ScavPlayer.TryGetPlayerAndNetBodyFromClientId(client, out var player, out var nb) || player.body == null) return;
        SendShared(client, true);
        PlayerCheckpoint.SendCrafted(nb);
    }

    internal static bool Ready(bool opening) {
        if (!Net.IsRunning) return true;
        PlayerCheckpoint.ApplyLocalCraftMarkers();
#if FULLGAME
        if (Net.IsServer && DeferredPlayerSaveRestore.InProgress) return false;
#endif
        if (!Net.IsClient || (sharedReceived && PlayerCheckpoint.ClientCraftedReceived)) return true;
        if (opening) openPending = true;
        if (Time.realtimeSinceStartup >= nextRequest) {
            nextRequest = Time.realtimeSinceStartup + 2f;
            Net.Client_Send(DeliveryMethod.ReliableOrdered, Net.CreateWriter(NetmsgId.SERVER_RequestRecipeState));
        }
        return false;
    }

    internal static void RefreshPanel() {
        PlayerCheckpoint.ApplyLocalCraftMarkers();
        var cam = PlayerCamera.main;
        if (cam == null || cam.craftingPanel == null) return;
        if (openPending && sharedReceived && PlayerCheckpoint.ClientCraftedReceived) {
            openPending = false;
            cam.OpenCraftScreen();
        } else if (cam.craftingPanel.activeSelf) cam.RefreshRecipeList();
    }
}

[HarmonyPatch(typeof(Recipes), nameof(Recipes.SetUpRecipes))]
internal static class RecipeProgress_Setup {
    [HarmonyPriority(Priority.Last)]
    static void Postfix() => RecipeProgress.RecipesCreated();
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.OpenCraftScreen))]
internal static class RecipeProgress_Open {
    [HarmonyPriority(Priority.First)]
    static bool Prefix() {
        if (RecipeProgress.Ready(true)) return true;
        Util.DoAlert("正在同步你的制作记录和全队蓝图，请稍候。 / Syncing your crafting progress and team blueprints.");
        return false;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.RefreshRecipeList))]
internal static class RecipeProgress_Refresh {
    [HarmonyPriority(Priority.First)]
    static void Prefix() { PlayerCheckpoint.ApplyLocalCraftMarkers(); }
}

[HarmonyPatch(typeof(NetBody), nameof(NetBody.ResetMind))]
internal static class RecipeProgress_MindReset {
    static void Postfix(NetBody __instance) {
        if (Net.IsServer) {
            PlayerCheckpoint.MarkCraftedLoaded(__instance);
            PlayerCheckpoint.SendCrafted(__instance);
        } else if (Net.IsClient && __instance.IsLocal) PlayerCheckpoint.ClearClientCrafted();
    }
}

[HarmonyPatch(typeof(Body), "Start")]
internal static class RecipeProgress_BodyReady {
    [HarmonyPriority(Priority.Last)]
    static void Postfix(Body __instance) {
        if (!Net.IsServer || !__instance.TryGetNetBody(out var nb) || !nb.IsPlayer || nb.plr == null) return;
        // Other Body.Start patches restore the disk/disconnect copy first.
        if (!nb.plr.IsLocal) RecipeProgress.SendShared(nb.plr.playerId, true);
        PlayerCheckpoint.SendCrafted(nb);
    }
}
