#if FULLGAME
using System;
using System.Collections.Generic;
using System.Linq;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

// A species change is a live mesh replacement, not a disconnect/rejoin. Keep the
// entity, inventory, chip and team-ending ownership; never reload an older body file.
internal static class PortSpeciesSwap {
    internal static bool Rebuilding { get; private set; }
    sealed class State {
        internal CharacterHealthStateSyncPacket health;
        internal CharacterHealthPainkillerStateSyncPacket painkiller;
        internal bool hasPainkiller;
        internal readonly List<(int slot, Item item)> items = new();
        internal readonly List<Item> worn = new();
        internal List<int> crafted;
        internal Dictionary<string, string> memory;
        internal Vector2 position;
        internal int str, res, intel;
        internal float expStr, expRes, expInt;
    }

    internal static void Server(ScavPlayer player, int species, bool wipeXp) {
        if (!Net.IsRunning || !Net.IsServer || player?.body == null || !player.body.alive)
            throw new InvalidOperationException("Species requires a living player on the server.");
        if (!Util.IsWorldGenerated() || WorldGeneration.world.cutsceneActive || ScrollableText.textExists ||
            SaveSlots.Busy || DeferredPlayerSaveRestore.InProgress || player.IsInMinigame)
            throw new InvalidOperationException("Wait until loading, saving or the minigame/cutscene finishes.");
        if (ScavPlayer.ClientIdToPlayerDict.Values.Any(p =>
            MinigameMPManager.GetSessionThisPlayerIsInvolvedIn(p) is MinigameMPManager._LimbMedicalMinigameSession medical && medical.limb?.body == player.body))
            throw new InvalidOperationException("Wait until treatment on this player finishes.");
        byte chosen = (byte)CharacterSpawn.ClampChar(species);
        Rebuild(player, chosen);
        if (wipeXp) player.body.skills.Setup(chosen);
        else {
            float[] multipliers = Skills.BaseMultipliers(chosen);
            player.body.skills.STRGainMult = multipliers[0];
            player.body.skills.RESGainMult = multipliers[1];
            player.body.skills.INTGainMult = multipliers[2];
        }
        player.characterChosen = true;
        CharacterLobby.Publish(player);
        CharacterRunMemory.Set(CharacterRunMemory.PlayerKey(player.SteamId, player.playerName), chosen);
        var health = new CharacterHealthStateSyncPacket();
        health.Write(player.body);
        var writer = Net.CreateWriter(PortMsgId.CLIENT_SpeciesSwap);
        writer.Put(player.playerId); writer.Put(chosen); writer.Put(health);
        Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
        MedicalSync.ForceHealth(player.netBody);
        // Keep both the authoritative binding and the native player-file species current.
        PlayerCheckpoint.TryReadSpecies(player.GetPersistentId(), out _, out int nativeSlot);
        PlayerCheckpoint.RememberSpecies(player.GetPersistentId(), CharacterSpawn.SaveSpeciesName(chosen), nativeSlot);
        if (ServerMain.Server_LastPlayerStates.TryGetValue(player.GetPersistentId(), out var remembered)) {
            remembered.charType = chosen; remembered.health = health;
            ServerMain.Server_LastPlayerStates[player.GetPersistentId()] = remembered;
        }
        try {
            if (SaveSlots.Enabled && SaveSlots.Active != 0 && !SavesystemPatch.PreserveSave)
                SaveSlots.Commit(() => SaveCurrentLayerExit.WriteAndValidate(WorldGeneration.world, WorldGeneration.world.biomeDepth));
            else PlayerCheckpoint.RunChecked(() => PlayerCheckpoint.WriteOne(player, false));
        } catch (Exception e) {
            log.error("[species] character changed but checkpoint failed: " + e);
            throw new InvalidOperationException("Species changed, but saving failed. Save manually before leaving.", e);
        }
        log.l($"[species] player={player.playerName} species={chosen} wipeXp={wipeXp} binding/checkpoint updated");
    }

    [ClientReceiver((ushort)PortMsgId.CLIENT_SpeciesSwap)]
    static void Receive(knetid _, NetDataReader reader) {
        reader.Get(out knetid id); reader.Get(out byte species);
        reader.Get(out CharacterHealthStateSyncPacket health);
        if (species > 2) throw new ArgumentException("Invalid species.");
        if (!ScavPlayer.TryGetPlayerFromClientId(id, out var player)) return;
        Rebuild(player, species);
        if (player.netBody != null) health.Apply(player.netBody, true);
        if (player.IsLocal) CharacterSelection.AdoptForced(species);
    }

    internal static void Rebuild(ScavPlayer player, byte species) {
        if (player == null || player.body == null || Rebuilding) return;
        if (player.body.charType == species) {
            player.characterSelected = species;
            if (player.IsLocal) CharacterSelection.AdoptForced(species);
            return;
        }
        var old = player.netBody;
        if (old == null || old.syncBody == null) throw new InvalidOperationException("Missing body/entity for species change.");
        var entity = old.syncBody;
        bool viewed = WoundView.view != null && WoundView.view.body == player.body;
        var state = new State { position = player.body.transform.position,
            crafted = new List<int>(old.RecipesHasCrafterBefore), memory = new Dictionary<string,string>(old.MindMemory) };
        state.health.Write(player.body);
        state.str = player.body.skills.STR; state.res = player.body.skills.RES; state.intel = player.body.skills.INT;
        state.expStr = player.body.skills.expSTR; state.expRes = player.body.skills.expRES; state.expInt = player.body.skills.expINT;
        state.hasPainkiller = player.body.TryGetComponent<Painkillers>(out _);
        if (state.hasPainkiller) state.painkiller = new CharacterHealthPainkillerStateSyncPacket(player.body);
        for (int i = 0; i < player.body.slots.Length; i++) {
            Item item = player.body.GetItem(i);
            if (item != null) { state.items.Add((i,item)); item.transform.SetParent(null, true); }
        }
        foreach (Item item in player.body.GetAllWearables()) {
            if (item != null && !state.items.Any(x => x.item == item)) {
                state.worn.Add(item); item.transform.SetParent(null, true);
            }
        }
        old.StopPiggyback(true);
        if (old.carrying_person != null) old.carrying_person.StopPiggyback(true);
        Rebuilding = true;
        try {
            player.characterSelected = species;
            if (player.IsLocal) CharacterSelection.AdoptForced(species);
            if (Net.IsServer) entity.characterSelected.Value = species;
            var oldRoot = old.transform.parent != null ? old.transform.parent.gameObject : old.gameObject;
            NetBody.DetachForMeshRebuild(old);
            player.body = null;
            var replacement = NetBody.CreateNewPlayerCharacter(player, entity);
            if (replacement == null || player.body == null) throw new InvalidOperationException("Species body creation failed.");
            replacement.SetBodyPosition(state.position);
            replacement.RecipesHasCrafterBefore = state.crafted;
            replacement.MindMemory = state.memory;
            state.health.Apply(player.body);
            // The network packet quantizes XP; a local mesh swap must retain its full precision.
            player.body.skills.STR = state.str; player.body.skills.RES = state.res; player.body.skills.INT = state.intel;
            player.body.skills.expSTR = state.expStr; player.body.skills.expRES = state.expRes; player.body.skills.expINT = state.expInt;
            player.body.skills.UpdateExpBoundaries();
            if (state.hasPainkiller) {
                if (!player.body.TryGetComponent<Painkillers>(out _)) player.body.gameObject.AddComponent<Painkillers>();
                state.painkiller.Apply(player.body);
            }
            player.body.charType = species;
            ChipState.ApplyGameplay(player);
            foreach (var entry in state.items) {
                if (entry.slot < player.body.slots.Length) player.body.PickUpItem(entry.item, entry.slot, true);
                else player.body.AutoPickUpItem(entry.item);
            }
            foreach (Item item in state.worn) player.body.AutoPickUpItem(item);
            if (player.IsLocal && PlayerCamera.main != null) {
                PlayerCamera.main.body = player.body;
                if (MoodleManager.main != null) MoodleManager.main.body = player.body;
            }
            ItemSync.RefreshWearableSpeciesSprites(player.body);
            if (viewed) PlayerCamera_ToggleWoundView_MultiplayerPatch.WoundView_SetTargetBody(player.body);
            // A checkpoint can run before Unity's deferred Destroy. Hide the retired
            // mesh now so world capture cannot see a second body for this player.
            oldRoot.SetActive(false);
            SharedMain.ForceUpdatePlayerLists();
            UnityEngine.Object.Destroy(oldRoot);
        } finally { Rebuilding = false; }
    }
}
#endif
