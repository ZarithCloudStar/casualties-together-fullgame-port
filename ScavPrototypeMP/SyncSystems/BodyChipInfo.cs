using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

// Native WoundView.cInfo is generated locally per body (height/age/specimen id/version)
// and was never network replicated. Keep a per-body copy and send the owning
// client's values to the host so the health panel can show the inspected body's
// real chip row instead of the local player's.
public sealed class BodyChipInfo : MonoBehaviour {
    public bool has;
    public int height, age, id, ver;

    internal bool Set(int height, int age, int id, int ver) {
        bool changed = !has || this.height != height || this.age != age || this.id != id || this.ver != ver;
        this.height = height;
        this.age = age;
        this.id = id;
        this.ver = ver;
        has = true;
        return changed;
    }
}

[HarmonyPatch(typeof(WoundView), nameof(WoundView.SetCharDetails))]
internal static class WoundView_SetCharDetails_ChipInfoPatch {
    static void Postfix(WoundView __instance, int height, int age, int id, int ver) {
        Body body = __instance != null ? __instance.body : null;
        if (body == null) return;
        if (Net.IsRunning && !body.IsBodyLocal()) return;
        ChipInfoSync.RememberLocal(height, age, id, ver);
        BodyChipInfo chip = body.GetComponent<BodyChipInfo>();
        if (chip == null) chip = body.gameObject.AddComponent<BodyChipInfo>();
        chip.Set(height, age, id, ver);
    }
}

// One-shot client -> host upload of the local chip row. The host stores it on the
// client's body; CharacterHealthStateSyncPacket carries it to every other client.
internal sealed class ChipInfoSync : CasualtiesTogetherSingleton {
    const string MessageName = "TogetherBodyChipInfo";
    const int Protocol = 1;
    enum Message : byte { Request = 1 }

    internal static bool HasLocal;
    internal static int LocalHeight, LocalAge, LocalId, LocalVer;

    static float nextSend;

    internal static void RememberLocal(int height, int age, int id, int ver) {
        bool changed = !HasLocal || LocalHeight != height || LocalAge != age || LocalId != id || LocalVer != ver;
        HasLocal = true;
        LocalHeight = height;
        LocalAge = age;
        LocalId = id;
        LocalVer = ver;
        if (changed) nextSend = 0f;
    }

    internal static void EnsureLocal(Body body) {
        if (!HasLocal || body == null) return;
        BodyChipInfo chip = body.GetComponent<BodyChipInfo>();
        if (chip == null) chip = body.gameObject.AddComponent<BodyChipInfo>();
        if (chip.Set(LocalHeight, LocalAge, LocalId, LocalVer)) nextSend = 0f;
    }

    void Awake() {
        Multiplayer.RegisterCustomServerReceiver(MessageName, ServerMessage);
    }

    void Update() {
        if (!Net.IsRunning || !Net.IsClient) return;
        if (Time.unscaledTime < nextSend) return;
        Body body = ScavPlayer.LocalPlayer != null ? ScavPlayer.LocalPlayer.body : null;
        if (body == null) return;
        BodyChipInfo chip = body.GetComponent<BodyChipInfo>();
        if (chip == null || !chip.has) return;

        var writer = Multiplayer.CreateNamedWriter(MessageName);
        writer.Put(Protocol);
        writer.Put((byte)Message.Request);
        writer.Put(chip.height);
        writer.Put(chip.age);
        writer.Put(chip.id);
        writer.Put(chip.ver);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        // Idempotent and tiny; resending also covers reconnects where the host's
        // copy of this client was rebuilt.
        nextSend = Time.unscaledTime + 2f;
    }

    static void ServerMessage(ScavPlayer player, NetDataReader reader) {
        try {
            if (reader.GetInt() != Protocol) return;
            if ((Message)reader.GetByte() != Message.Request) return;
            int height = reader.GetInt();
            int age = reader.GetInt();
            int id = reader.GetInt();
            int ver = reader.GetInt();

            if (!Net.IsServer || player == null || player.body == null) return;
            if (height < 50 || height > 300 || age < 0 || age > 200 || id < 0 || ver < 0 || ver > 99) return;

            BodyChipInfo chip = player.body.GetComponent<BodyChipInfo>();
            if (chip == null) chip = player.body.gameObject.AddComponent<BodyChipInfo>();
            bool changed = chip.Set(height, age, id, ver);
            if (changed) {
                MedicalSync.Server_SendCharacterHealth(player.netBody, true);
                PlayerCamera_ToggleWoundView_MultiplayerPatch.RefreshChipDetails(player.body);
            }
        } catch (System.Exception e) {
            Plugin.log.LogWarning("[chip] server message failed: " + e.Message);
        }
    }
}
