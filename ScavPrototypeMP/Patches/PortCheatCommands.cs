using HarmonyLib;
namespace Together;
[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.CheckForCheats))]
static class ConsoleScript_CheckForCheats_MultiplayerPatch {
    static void Prefix(ConsoleScript __instance, ref bool __state) {
        __state = SteamManager.disableAchievements;
        if (!Net.IsRunning) {
            return;
        }
        Con.ConFailIfCheatsDisabled(); // throw in multiplayer when cheats are off
    }

    // stock just sets SteamManager.disableAchievements on this machine - push to the whole lobby/save
    static void Postfix(bool __state) {
        if (!Net.IsRunning || !Net.IsServer) {
            return;
        }
        // only the first break for this session - don't re-alert every subsequent cheat
        if (__state || !SteamManager.disableAchievements) {
            return;
        }
        AchievementBreakSync.Server_Broadcast(showAlert: true);
    }
}
