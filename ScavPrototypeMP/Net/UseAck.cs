using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Together;

// A client-side use stays protected until the host confirms that it handled
// the request. The host sends the confirmation for accepted and rejected
// requests so a slow connection cannot leave a stale prediction in place.
internal static class UseAck
{
    private const string Command = "use_ack";

    internal static void Send(knetid clientId, knetid item)
    {
        if (!Net.IsServer || item.id == 0)
            return;

        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer player) ||
            player == null || player.IsLocal)
            return;

        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put(Command);
        writer.Put(item.id);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, player.playerId);
        LightDiagnostics.UseAck("sent item=" + item.id);
    }

    internal static Scope After(knetid clientId, knetid item)
    {
        return new Scope(clientId, item);
    }

    internal readonly struct Scope : System.IDisposable
    {
        private readonly knetid client;
        private readonly knetid item;

        internal Scope(knetid client, knetid item)
        {
            this.client = client;
            this.item = item;
        }

        public void Dispose()
        {
            Send(client, item);
        }
    }

    [CTPostInit]
    private static void Init()
    {
        ClientMain.ClientCustomCommandsDict[Command] = (cmd, reader) =>
        {
            reader.Get(out ushort id);
            if (id == 0)
                return;

            if (NetObjectRegistry.TryGetSyncInfo((knetid)id, out SyncInfo syncInfo) && syncInfo != null)
                {
                syncInfo.OnServerAck();
                    LightDiagnostics.UseAck("received item=" + id);
                }
        };
    }
}
