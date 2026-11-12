#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace Together;

internal static class ProsthesisRepairAuthority {
    // Learned from native TryProsthesisRepair, on the authority, while the
    // validated ApplyWoundItem request executes. Never supplied by a client.
    internal static readonly Dictionary<string, (int count, string type)> Materials = new();
    internal static bool CanRepair(ScavPlayer healer, Limb limb, Item item, out int count) {
        count = 0;
        if (healer?.body == null || limb == null || item == null ||
            !Materials.TryGetValue(item.id, out var material) || material.count <= 0 ||
            !limb.prostheticInfo.hasProsthetic || limb.prostheticInfo.itemId != material.type ||
            limb.prostheticInfo.maxHoles <= 0 || limb.dismembered || limb.dislocated ||
            item.condition <= 0f || !healer.body.conscious ||
            !ItemSync.CheckIfBodyReachThisItem(item, healer.body, 20f, false) ||
            !PlayerCamera_ApplyWoundItem_MultiplayerPatch.DoApplyWoundItemChecks(healer.body, limb, item) ||
            (limb.body != healer.body && !Util.DoFullInteractionCheck(healer.body, limb.body, false,
                SharedMain.max_player_interaction_distance * 1.3f, false))) return false;
        count = material.count;
        return true;
    }

    internal sealed class Budget {
        public Item item;
        public int maxPatches, left;
        public ushort sequence;
    }
    internal sealed class Session : MinigameMPManager._LimbMedicalMinigameSession {
        internal readonly Dictionary<ScavPlayer, Budget> budgets = new();
        public override void OnPlayerJoin(ScavPlayer player) {
            if (CanRepair(player, limb, player.minigame_currentItem, out int count))
                budgets[player] = new Budget { item = player.minigame_currentItem, maxPatches = count,
                    left = UnityEngine.Mathf.FloorToInt(count * player.minigame_currentItem.condition) };
        }
    }

    internal static void Receive(knetid client, NetDataReader reader) {
        reader.Get(out ushort sessionId); reader.Get(out ushort sequence); reader.Get(out bool bone);
        if (!Net.IsServer || !MedicalSync.CanSyncHealth() ||
            !ScavPlayer.TryGetPlayerFromClientId(client, out var player) ||
            !MinigameMPManager.TryGetSessionThisPlayerIsInvolvedIn<Session>(player, out var session) ||
            session.session_id != sessionId || session.minigame_ended ||
            !session.budgets.TryGetValue(player, out var budget) || sequence != budget.sequence + 1) return;
        budget.sequence = sequence;
        var limb = session.limb;
        var item = budget.item;
        if (item != player.minigame_currentItem || budget.left <= 0 ||
            !CanRepair(player, limb, item, out int count) || count != budget.maxPatches ||
            item.condition + .0015f < 1f / count ||
            bone != limb.broken || (!bone && limb.muscleHealth >= 99.9f)) {
            MedicalSync.Server_QueueSendCharacterHealth(limb.GetNetBody(), true);
            MinigameMPManager.Server_ForceEndMinigameForPlayer(player);
            return;
        }
        float before = limb.muscleHealth, durability = item.condition;
        float amount = 100f / limb.prostheticInfo.maxHoles;
        if (bone) {
            limb.boneHealTimer -= amount;
            if (limb.boneHealTimer < 5f) limb.MendBone();
        } else limb.muscleHealth = UnityEngine.Mathf.Min(100f, limb.muscleHealth + amount);
        budget.left--;
        item.condition = UnityEngine.Mathf.Max(0f, item.condition - (1f / count - .0015f));
        if (item.condition < .01f) item.condition = 0f;
        if (NetObjectRegistry.TryGetSyncInfo(item, out var si)) si.WriteObjectIntoPacket();
        MedicalSync.Server_SendCharacterHealth(limb.GetNetBody(), true);
        MinigameMPManager.Note($"prosthesis accepted session={sessionId} step={sequence} bone={bone} muscle={before:F3}->{limb.muscleHealth:F3} condition={durability:F4}->{item.condition:F4}");
    }

    static ProsthesisRepairMinigame current;
    static ushort sequence;
    internal static void Step(Limb limb, bool bone) {
        var minigame = MinigameBase.main.currentMinigame as ProsthesisRepairMinigame;
        if (minigame == null || minigame.limb != limb) return;
        if (current != minigame) { current = minigame; sequence = 0; }
        var writer = Net.CreateWriter(NetmsgId.SERVER_ProsthesisRepair);
        writer.Put(ScavPlayer.LocalPlayer.minigame_session); writer.Put(++sequence); writer.Put(bone);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }
    // Native UI keeps its holes/patches/sounds, but all gameplay writes below
    // become one validated step. No predicted health or durability is stored.
    internal static void Muscle(Limb limb, float value) { if (Net.IsRunning) Step(limb, false); else limb.muscleHealth = value; }
    internal static void Bone(Limb limb, float value) { if (Net.IsRunning) Step(limb, true); else limb.boneHealTimer = value; }
    internal static void Condition(Item item, float value) { if (!Net.IsRunning) item.condition = value; }
    internal static void Mend(Limb limb) { if (!Net.IsRunning) limb.MendBone(); }
}

[HarmonyPatch(typeof(Item), nameof(Item.TryProsthesisRepair))]
internal static class ProsthesisMaterialPatch {
    static void Prefix(Item item, int holeCount, string prosthesisType) {
        if (Net.IsServer && item != null) ProsthesisRepairAuthority.Materials[item.id] = (holeCount, prosthesisType);
    }
}
[HarmonyPatch(typeof(ProsthesisRepairMinigame), nameof(ProsthesisRepairMinigame.Update))]
internal static class ProsthesisRepairUpdatePatch {
    static bool Prefix() => !Net.IsRunning || MinigameMPManager.client_i_know_my_minigame_session;
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        foreach (var code in instructions) {
            string method = code.StoresField(AccessTools.Field(typeof(Limb), "muscleHealth")) ? "Muscle" :
                code.StoresField(AccessTools.Field(typeof(Limb), "boneHealTimer")) ? "Bone" :
                code.StoresField(AccessTools.Field(typeof(Item), "condition")) ? "Condition" :
                code.Calls(AccessTools.Method(typeof(Limb), nameof(Limb.MendBone))) ? "Mend" : null;
            if (method != null) { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(ProsthesisRepairAuthority), method); }
            yield return code;
        }
    }
}
#endif
