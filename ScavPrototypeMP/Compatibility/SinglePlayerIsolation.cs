using System;

namespace Together;

// Leaving a room must not carry multiplayer rules or run settings into the
// single-player main menu/run. Chip and character choices are also MP-only.
internal static class SinglePlayerIsolation {
    internal static void Reset() {
        try {
            Multiplayer.rules = new MultiplayerGameRules();
            Multiplayer.ApplyGameRules();
        } catch (Exception ex) {
            log.warn("SP-ISOLATION: rule reset warning: " + ex.Message);
        }
        WorldGeneration.runSettings = null;
        log.l("SP-ISOLATION: cleared multiplayer rules/run settings");
    }
}
