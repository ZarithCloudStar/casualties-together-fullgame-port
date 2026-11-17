using HarmonyLib;
using LiteNetLib;

namespace Together;

internal static class LayerExitWhenHostDead {
    static bool saveBusy;
    internal static bool ExitBlocked() {
#if FULLGAME
        return FullgameWorldCompat.IsAscentLayer() || (EndingFlow.Active && WorldGeneration.world != null
            && WorldGeneration.world.biomeDepth >= 9 && WorldGeneration.world.biomeDepth <= 11);
#else
        return false;
#endif
    }

    [CTPostInit]
    static void Register() {
        ServerMain.ServerCustomCommandsDict["layer_continue"] = (plr, cmd, args) => Accept(plr, false);
        ServerMain.ServerCustomCommandsDict["layer_save"] = (plr, cmd, args) => Accept(plr, true);
        Multiplayer.OnSceneChangeOrWorldStartGenerate += () => saveBusy = false;
    }

    internal static bool ClientAsk(WorldGeneration world, bool save) {
        if (!Net.IsClient || !ServerMain.HostBodyIsDead() || world == null) return false;
        if (ExitBlocked()) { world.savePanel?.SetActive(false); return true; }
        if (!ServerMain.LocalPlayerAtLayerExit() || !ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()) {
            PlayerCamera.main?.DoAlert(Lang.Get("layerfinish_not_enough_plrs"));
            world.savePanel?.SetActive(false);
            return true;
        }
        var writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put(save ? "layer_save" : "layer_continue");
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        world.savePanel?.SetActive(false);
        return true;
    }

    static void Accept(ScavPlayer player, bool save) {
        if (!Net.IsServer || player == null || saveBusy || !ServerMain.HostBodyIsDead()
            || player.body == null || !player.body.alive || !ServerMain.DidHeFinishTheLayer(player.body)) return;
        var world = WorldGeneration.world;
        if (world == null || world.doingRegen || world.generatingWorld || !world.worldExists
            || ExitBlocked() || !ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()) return;
        if (save) {
            saveBusy = true;
            // Keep our slot/current-layer validation and transactional save paths.
            try { world.SaveAndExit(); }
            finally { saveBusy = false; }
        } else world.ContinueRun();
    }
}

[HarmonyPatch(typeof(WorldGeneration), "Update")]
internal static class WorldGeneration_Update_DeadHostExitPanel {
    static void Postfix(WorldGeneration __instance) {
        if (!Net.IsRunning || __instance == null || __instance.savePanel == null || !ServerMain.HostBodyIsDead()
            || __instance.doingRegen || __instance.generatingWorld || !__instance.worldExists) return;
        bool show = !LayerExitWhenHostDead.ExitBlocked() && ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()
            && (!Net.IsClient || ServerMain.LocalPlayerAtLayerExit());
        if (__instance.savePanel.activeSelf != show) __instance.savePanel.SetActive(show);
    }
}
