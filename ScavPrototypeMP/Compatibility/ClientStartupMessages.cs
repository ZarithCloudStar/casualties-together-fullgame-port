#if FULLGAME
using System;
using System.Collections.Generic;
using LiteNetLib;

namespace Together;

// Custom receivers need a real ScavPlayer. Reliable startup requests must survive
// both the transport handshake and the character panel without blocking SetCharacter.
internal static class ClientStartupMessages {
    const int MaxMessages = 64, MaxBytes = 256 * 1024;
    readonly struct Message {
        internal readonly DeliveryMethod Delivery;
        internal readonly byte[] Data;
        internal Message(DeliveryMethod delivery, ReadOnlySpan<byte> data) {
            Delivery = delivery;
            Data = data.ToArray(); // callers commonly reuse their writer immediately
        }
    }
    static readonly List<Message> pending = new();
    static int bytes;
    internal static int Count => pending.Count;

    static bool WaitingForPlayer(ReadOnlySpan<byte> data) => data.Length > 0 &&
        data[0] == (byte)NetmsgId.CustomMessage && ScavPlayer.LocalPlayer == null;

    internal static bool TryDefer(in DeliveryMethod delivery, in ReadOnlySpan<byte> data) {
        if (!Net.IsRunning || !Net.IsClient) return false;
        bool transportReady = Net.Transport != null && !Net.IsConnecting;
        if (transportReady && !WaitingForPlayer(data)) return false;

        // Stale pings/entity updates should not be replayed after joining.
        if (delivery != DeliveryMethod.ReliableOrdered && delivery != DeliveryMethod.ReliableUnordered &&
            delivery != DeliveryMethod.ReliableSequenced) return true;
        if (pending.Count >= MaxMessages || data.Length > MaxBytes - bytes)
            throw new InvalidOperationException("Reliable startup message queue is full; wait for admission or reconnect.");
        pending.Add(new Message(delivery, data));
        bytes += data.Length;
        if (pending.Count == 1)
            SessionLog.Event("CLIENT STARTUP QUEUED", $"phase={(transportReady ? "character" : "transport")} bytes={data.Length}");
        return true;
    }

    internal static void Flush() {
        if (pending.Count == 0 || !Net.IsClient || !Net.IsConnected) return;
        var transport = Net.Transport;
        int sent = 0;
        // Control messages may pass custom requests waiting for a player. A FIFO
        // blocked by a snapshot request would otherwise deadlock character selection.
        for (int i = 0; i < pending.Count;) {
            var message = pending[i];
            if (WaitingForPlayer(message.Data)) { i++; continue; }
            pending.RemoveAt(i);
            bytes -= message.Data.Length;
            ReadOnlySpan<byte> data = message.Data;
            transport.Client_Send(message.Delivery, data);
            sent++;
            if (!Net.IsRunning || !ReferenceEquals(Net.Transport, transport)) break;
        }
        if (sent > 0) SessionLog.Event("CLIENT STARTUP FLUSHED", $"sent={sent} remaining={pending.Count}");
    }

    internal static void Reset() { pending.Clear(); bytes = 0; }
}
#endif
