#if FULLGAME && TESTING
using System;
using System.Collections;
using System.IO;
using System.Linq;
using CasualtiesTogetherUtils;
using LiteNetLib;
using UnityEngine;

namespace Together;

internal static class Port551Smoke {
    static bool loading, running;
    static float readyAt;
    static string Folder => Path.GetDirectoryName(Environment.GetEnvironmentVariable("CT_ENDING_TEST_SAVE"));
    static string PathOf(string flag) => Path.Combine(Folder, flag);
    static void Mark(string flag) => File.WriteAllText(PathOf(flag), "ready");
    static void Check(bool value, string name) {
        if (!value) { log.error("[551-smoke] FAIL " + name); throw new Exception(name); }
        log.l("[551-smoke] PASS " + name);
    }
    static IEnumerator Wait(Func<bool> ready, string phase, float seconds = 75f) {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!ready()) {
            if (Time.realtimeSinceStartup > deadline) { Check(false, "timeout " + phase); yield break; }
            yield return null;
        }
    }
    internal static void Tick() {
        SteamManager.disableAchievements = true;
        if (Net.IsClient && CharacterLobby.ShouldShowPanel) { ChipState.Choose(true); CharacterLobby.ConfirmPanel(); }
        if (Net.IsServer && Net.IsRunning && !loading && !Util.IsInWorld() && PreRunScript.instance != null &&
            ScavPlayer.ClientIdToPlayerDict.Count >= 2 && !CharacterReadyGate.Blocked) {
            if (readyAt == 0f) readyAt = Time.unscaledTime + 3f;
            if (Time.unscaledTime >= readyAt) { loading = true; PreRunScript.instance.StartRun(); }
        }
        if (running || !Util.IsWorldGenerated() || ScavPlayer.LocalPlayer?.body == null) return;
        running = true;
        Util.StartCoroutine(Net.IsServer ? Host() : Client());
    }
    static IEnumerator Host() {
        yield return Wait(() => ScavPlayer.ClientIdToPlayerDict.Count == 2 &&
            ScavPlayer.ClientIdToPlayerDict.Values.All(p => p.body != null && p.ServerPlayerState.is_loaded_in), "both spawned");
        yield return new WaitForSecondsRealtime(3f);
        Check(BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("net.cucorelib"), "bundled CUCoreLib loaded");
        var guest = ScavPlayer.ClientIdToPlayerDict.Values.Single(p => !p.IsLocal);
        Check(guest.body.charType == 1, "guest chosen Milky");
        Mark("inventory-go");
        yield return Wait(() => guest.IsInInventory, "guest inventory input");
        Check(guest.IsInInventory, "guest inventory status reaches host");
        PlayerCamera.main.radialOpen = true;
        Mark("host-inventory-go");
        yield return Wait(() => File.Exists(PathOf("host-inventory-client-ok")), "host inventory replication");
        PlayerCamera.main.radialOpen = false;
        Mark("inventory-close");
        yield return Wait(() => !guest.IsInInventory, "guest inventory close");
        Multiplayer.rules.AllowClientCheatCommands = true;
        guest.body.brainHealth = 57f;
        MedicalSync.ForceHealth(guest.netBody);
        Mark("heal-go");
        yield return Wait(() => guest.body.brainHealth >= 99f, "client heal accepted");
        Check(guest.body.brainHealth >= 99f, "client heal runs on authority");
        guest.body.skills.STR = 8; guest.body.skills.RES = 9; guest.body.skills.INT = 10;
        guest.body.skills.UpdateExpBoundaries();
        float preservedXp = guest.body.skills.minSTR + .25f;
        guest.body.skills.expSTR = preservedXp;
        var items = guest.body.GetAllItemsThorough().Where(x => x != null).ToArray();
        PortSpeciesSwap.Server(guest, 2, false);
        Check(guest.body.charType == 2 && guest.characterSelected == 2, "server changed to Dune");
        Check(guest.body.skills.STR == 8 && guest.body.skills.RES == 9 && guest.body.skills.INT == 10 &&
            Mathf.Abs(guest.body.skills.expSTR - preservedXp) < .001f, "wipeXp false preserves skills");
        Check(guest.modUnchipped && guest.netBody.unchipped, "chip preserved");
        Check(items.All(x => guest.body.GetAllItemsThorough().Contains(x)), "inventory objects preserved");
        Check(CharacterRunMemory.TryGet(CharacterRunMemory.PlayerKey(guest.SteamId, guest.playerName), out byte remembered) && remembered == 2,
            "save-bound role updated");
        Check(PlayerCheckpoint.TryReadSpecies(guest.GetPersistentId(), out string species, out _) &&
            species == CharacterSpawn.SaveSpeciesName(2), "native player checkpoint updated");
        Mark("dune-go");
        yield return Wait(() => File.Exists(PathOf("dune-client-ok")), "client Dune receipt");
        PortSpeciesSwap.Server(guest, 0, true);
        int[] baseSkills = Skills.BaseSkills(0);
        Check(guest.body.charType == 0 && guest.body.skills.STR == baseSkills[0] && guest.body.skills.RES == baseSkills[1] &&
            guest.body.skills.INT == baseSkills[2] && guest.body.skills.expSTR == guest.body.skills.minSTR &&
            guest.body.skills.expRES == guest.body.skills.minRES && guest.body.skills.expINT == guest.body.skills.minINT,
            "wipeXp true restores starting skills");
        Mark("expie-go");
        yield return Wait(() => File.Exists(PathOf("expie-client-ok")), "client Expie receipt");
        var host = ScavPlayer.LocalPlayer;
        var phase = EndingFlow.State.phase; var cargoOwner = EndingFlow.State.cargoOwner;
        PortSpeciesSwap.Server(host, 2, false);
        Check(host.body.charType == 2 && CharacterSelection.Local == 2 && PlayerCamera.main.body == host.body,
            "host species/camera/local binding synchronized");
        Mark("host-dune-go");
        yield return Wait(() => File.Exists(PathOf("host-dune-client-ok")), "client sees host species");
        PortSpeciesSwap.Server(host, 0, true);
        Check(EndingFlow.State.phase == phase && EndingFlow.State.cargoOwner == cargoOwner, "team ending state preserved");
        PlayerCheckpoint.WriteRules(true);
        var header = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(SavesystemPatch.mpsavefolder, SavesystemPatch.mpsaveheaderfile)));
        Check(header.Value<bool?>(AchievementBreakSync.SaveHeaderKey) == true, "cheat lock persisted in save header");
        Multiplayer.rules.AllowClientCheatCommands = false;
        guest.ServerPlayerState.admin_privilege = false;
        guest.body.brainHealth = 73f; MedicalSync.ForceHealth(guest.netBody);
        Mark("deny-go");
        yield return Wait(() => File.Exists(PathOf("deny-sent")), "denied heal sent");
        yield return new WaitForSecondsRealtime(1f);
        Check(guest.body.brainHealth < 90f, "unauthorized client heal denied");
        Mark("host-complete");
        log.l("[551-smoke] COMPLETE HOST");
    }
    static IEnumerator Client() {
        Check(BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("net.cucorelib"), "client CUCoreLib loaded");
        yield return Wait(() => File.Exists(PathOf("inventory-go")), "inventory signal");
        PlayerCamera.main.radialOpen = true;
        yield return Wait(() => File.Exists(PathOf("host-inventory-go")) &&
            ScavPlayer.ClientIdToPlayerDict.Values.Any(p => p.IsHost && p.IsInInventory), "host inventory status");
        Check(true, "host inventory status reaches guest");
        Mark("host-inventory-client-ok");
        yield return Wait(() => File.Exists(PathOf("inventory-close")), "close inventory signal");
        PlayerCamera.main.radialOpen = false;
        yield return Wait(() => File.Exists(PathOf("heal-go")), "heal signal");
        var w = Net.CreateWriter(NetmsgId.SERVER_PlayerHealRequest);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
        yield return Wait(() => File.Exists(PathOf("dune-go")) && ScavPlayer.LocalPlayer.body.charType == 2, "client Dune");
        yield return new WaitForSecondsRealtime(.5f);
        Check(ScavPlayer.LocalPlayer.body.skills.STR == 8 && ScavPlayer.LocalPlayer.modUnchipped, "client role/skills/chip synchronized");
        Mark("dune-client-ok");
        yield return Wait(() => File.Exists(PathOf("expie-go")) && ScavPlayer.LocalPlayer.body.charType == 0, "client Expie");
        Check(ScavPlayer.LocalPlayer.body.skills.expSTR == ScavPlayer.LocalPlayer.body.skills.minSTR, "client wiped XP synchronized");
        Mark("expie-client-ok");
        yield return Wait(() => File.Exists(PathOf("host-dune-go")) &&
            ScavPlayer.ClientIdToPlayerDict.Values.Any(p => p.IsHost && p.body != null && p.body.charType == 2), "host Dune replicated");
        Check(true, "host replacement replicated to guest");
        Mark("host-dune-client-ok");
        yield return Wait(() => File.Exists(PathOf("deny-go")), "denied heal signal");
        w = Net.CreateWriter(NetmsgId.SERVER_PlayerHealRequest);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
        Mark("deny-sent");
        yield return Wait(() => File.Exists(PathOf("host-complete")), "host complete");
        log.l("[551-smoke] COMPLETE CLIENT");
    }
}
#endif
