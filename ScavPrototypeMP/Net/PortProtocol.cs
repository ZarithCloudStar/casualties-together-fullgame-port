using System;
using LiteNetLib.Utils;
namespace Together;
// Reserved CustomMessage envelope. Ordinary CUCoreLib/named custom messages are unchanged.
internal static class PortProtocol {
    internal const uint Magic = 0x55353150; // U51P
    internal static void WriteMessageId(NetDataWriter writer, ushort id) {
        if (id < 256) { writer.Put((byte)id); return; }
        if (!Enum.IsDefined(typeof(PortMsgId), id)) throw new ArgumentOutOfRangeException(nameof(id));
        writer.Put((byte)NetmsgId.CustomMessage);
        writer.Put(Magic);
        writer.Put(id);
    }
    internal static ushort ReadMessageId(NetDataReader reader) {
        reader.Get(out byte id);
        if (id != (byte)NetmsgId.CustomMessage || reader.AvailableBytes < sizeof(uint)) return id;
        reader.Get(out uint magic);
        if (magic != Magic) { reader.SkipBytes(-sizeof(uint)); return id; }
        reader.Get(out ushort extension);
        if (!Enum.IsDefined(typeof(PortMsgId), extension)) throw new ArgumentException("Unknown port message.");
        return extension;
    }
}
