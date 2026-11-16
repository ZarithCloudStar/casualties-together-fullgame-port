using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
namespace Together;
internal static class PortSpeciesRoster {
    internal static void WriteSpeciesRoster(NetDataWriter writer) {
        var players = new List<ScavPlayer>(ScavPlayer.ClientIdToPlayerDict.Values);
        writer.Put((byte)Math.Min(255, players.Count));
        for (int i = 0; i < players.Count && i < 255; i++) {
            writer.Put(players[i].playerId); writer.Put(players[i].characterSelected);
        }
    }
    internal static void ReadSpeciesRoster(NetDataReader reader) {
        if (reader.EndOfData) return;
        reader.Get(out byte count);
        for (int i = 0; i < count; i++) {
            reader.Get(out knetid id); reader.Get(out byte species);
            if (ScavPlayer.TryGetPlayerFromClientId(id, out var player)) player.characterSelected = species;
        }
    }
}
