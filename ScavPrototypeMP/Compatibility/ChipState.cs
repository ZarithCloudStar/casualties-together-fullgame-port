#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;

namespace Together;

// Per-save choice and the vanilla per-player gameplay state share one source.
internal static class ChipState {
    internal static bool LocalUnchipped { get; private set; }
    static bool? pendingHostChoice;

    internal static string Label(bool unchipped) => CharacterSelection.Text(
        unchipped ? "chip_unchipped" : "chip_chipped", unchipped ? "Unchipped" : "Chipped");

    internal static void AdoptLocal(bool unchipped) => LocalUnchipped = unchipped;

    internal static void BeginSession() { LocalUnchipped = false; pendingHostChoice = null; }

    // Vanilla difficulty settings are the host's default, never the party's
    // choice. Save bindings and an explicit personal chip button take priority.
    internal static void ApplyHostRunSettings(Dictionary<string, object> settings, string reason,
        bool useSavedBinding = false, ScavPlayer host = null) {
        if (!Util.GameIsFull() || !Net.IsRunning || !Net.IsServer) return;
        host ??= ScavPlayer.LocalPlayer;
        if (host == null || !host.IsLocal) return;
        if (pendingHostChoice.HasValue) {
            host.modChipChosenExplicit = true;
            host.modUnchipped = pendingHostChoice.Value;
            pendingHostChoice = null;
        }
        string key = CharacterRunMemory.PlayerKey(host.SteamId, host.playerName);
        bool unchipped;
        string source;
        if (useSavedBinding && CharacterRunMemory.TryGetChip(key, out unchipped)) source = "save";
        else if (host.modChipChosenExplicit) { unchipped = host.modUnchipped; source = "personal choice"; }
        else {
            if (settings == null || !settings.TryGetValue("unchipped", out var value) || value is not bool flag) return;
            unchipped = flag;
            source = "run setting";
        }
        // The new world's identity carries this value into its first binding.
        // Do not write a lobby draft into the previously selected save.
        ApplyServer(host, unchipped, false);
        log.l($"[chip] host source={source} unchipped={unchipped} reason={reason}");
    }

    internal static void ApplyGameplay(ScavPlayer player) {
        if (!Util.GameIsFull() || !Net.IsRunning || player == null) return;
        Multiplayer.rules.UnchippedIsIndividual = true;
        player.SetUnchipped(player.modUnchipped);
        if (player.IsLocal) AdoptLocal(player.modUnchipped);
    }

    // Call only on the server. A disconnected/null player is not a chipped
    // player; throw so future ending rules cannot accidentally admit one.
    internal static bool IsUnchipped(ScavPlayer player) {
        if (!Net.IsServer) throw new InvalidOperationException("ChipState queries require the authoritative server.");
        if (player == null) throw new ArgumentNullException(nameof(player));
        return player.modUnchipped;
    }

    internal static bool HostIsUnchipped {
        get {
            if (!Net.IsServer) throw new InvalidOperationException("ChipState queries require the authoritative server.");
            foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values)
                if (player != null && player.IsHost) return IsUnchipped(player);
            throw new InvalidOperationException("Host is not registered.");
        }
    }

    internal static void ApplyServer(ScavPlayer player, bool unchipped, bool persist) {
        if (!Net.IsServer || player == null) return;
        player.modUnchipped = unchipped;
        if (player.BasePlayer != null) player.BasePlayer.modUnchipped.Value = unchipped;
        ApplyGameplay(player);
        if (persist) CharacterRunMemory.SetChip(CharacterRunMemory.PlayerKey(player.SteamId, player.playerName), unchipped);
        log.l($"[chip] server player={player.playerName} unchipped={unchipped} save={CharacterRunMemory.CurrentSeed} persistRequested={persist}");
    }

    // Settings UI also gives the host a choice without changing vanilla UI.
    internal static bool Choose(bool unchipped) {
        var player = ScavPlayer.LocalPlayer;
        if (Net.IsServer) {
            if (Net.IsRunning && player != null) {
                string key = CharacterRunMemory.PlayerKey(player.SteamId, player.playerName);
                if (Util.IsInWorld() && CharacterRunMemory.TryGetChip(key, out bool saved) && saved != unchipped) {
                    log.warn($"[chip] kept host save binding unchipped={saved}; ignored settings choice={unchipped}");
                    ApplyServer(player, saved, false);
                    return false;
                }
                player.modChipChosenExplicit = true;
                ApplyServer(player, unchipped, Util.IsInWorld());
                CharacterLobby.Publish(player);
                if (player.body != null) return true;
            } else {
                pendingHostChoice = unchipped;
                AdoptLocal(unchipped);
            }
            return CharacterSelection.Choose(CharacterSelection.Local);
        }
        if (CharacterLobby.LockedBySave || player?.body != null) return false;
        AdoptLocal(unchipped);
        return CharacterSelection.Choose(CharacterSelection.Local);
    }
}

// Native checkbox callbacks update their dictionary without going through
// WorldMP.ApplyRunSettings. Presets/imports refresh the whole settings list.
[HarmonyPatch(typeof(RunSettingDisplay), nameof(RunSettingDisplay.UpdateSetting))]
internal static class ChipState_RunSettingPatch {
    static void Postfix(RunSettingDisplay __instance, Dictionary<string, object> settings) {
        if (__instance.associated?.name == "unchipped")
            ChipState.ApplyHostRunSettings(settings, "difficulty checkbox", Util.IsInWorld());
    }
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.UpdateAllSettingDisplays))]
internal static class ChipState_RunPresetPatch {
    static void Postfix(PreRunScript __instance) =>
        ChipState.ApplyHostRunSettings(__instance.runSettings, "difficulty preset/settings", Util.IsInWorld());
}

// Vanilla may reset the client's local flag late in world generation. Server
// calls retain their argument; authority uses each player's saved chip state.
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetUnchipped))]
internal static class ChipState_LocalPresentationPatch {
    static void Prefix(ref bool unchipped) {
        if (Util.GameIsFull() && Net.IsRunning && Net.IsClient && ScavPlayer.LocalPlayer != null)
            unchipped = ScavPlayer.LocalPlayer.modUnchipped;
    }
}
#endif
