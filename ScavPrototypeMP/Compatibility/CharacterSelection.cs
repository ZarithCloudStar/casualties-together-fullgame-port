using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Reflection;
#if FULLGAME
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections;
using System.Linq;
using UnityEngine;
#endif

namespace Together;

internal static class CharacterSelection {
    internal static int? CommandLineOverride { get; private set; }
#if FULLGAME
    static byte? rememberedChoice;
    static int preferenceRevision;
#endif

    internal static void ParseOverride(string value) {
        if (int.TryParse(value, out int chr) && chr >= 0 && chr <= 2) {
            CommandLineOverride = chr;
            log.l($"CMD: set character to: {chr}");
        } else {
            log.warn($"CMD: ignoring invalid --mp-setcharacter '{value ?? "<missing>"}'; expected 0, 1 or 2.");
        }
    }

    internal static byte Local {
        get {
            int chr = 0;
#if FULLGAME
            if (Util.GameIsFull()) chr = rememberedChoice ?? PreRunScript.characterSelected;
#endif
            chr = CommandLineOverride ?? chr;
            if (chr < 0 || chr > 2) {
                log.warn($"CHARACTER: invalid local selection {chr}; using Experiment (0).");
                chr = 0;
            }
            return (byte)chr;
        }
    }

#if FULLGAME
    internal static string Text(string key, string fallback) => Lang.Get(key, true) ?? fallback;

    // UI callbacks only enqueue work. In particular, never deactivate the
    // selected button or touch the registry inside Unity's pointer dispatch.
    internal static bool RunUiAction(string phase, Action action) {
        try { action(); return true; }
        catch (Exception ex) { ReportUiError(phase, ex); return false; }
    }

    internal static void ReportUiError(string phase, Exception ex) {
        log.error($"CHARACTER: {phase} failed: {ex}");
        try {
            Multiplayer.DoMultiplayerStatusMessageLog(Text("character_action_failed", "Character selection failed. Please try again; details are in the log."));
        } catch (Exception statusError) {
            log.error($"CHARACTER: could not display error status: {statusError}");
        }
    }

    internal static bool DeferUiAction(string phase, Action action) =>
        RunUiAction(phase + " enqueue", () => Util.StartCoroutine(NextFrame(phase, action)));

    static IEnumerator NextFrame(string phase, Action action) {
        yield return null;
        RunUiAction(phase, action);
    }

    static void RememberAfterFrame(int chr) {
        int revision = ++preferenceRevision;
        DeferUiAction("remember choice", () => {
            if (revision != preferenceRevision) return; // rapid choices: newest wins
            try {
                PlayerPrefs.SetInt("lastchar", chr);
                // A confirmed pick is a commitment for future joins: the lobby
                // panel is skipped and the server admits us straight away.
                PlayerPrefs.SetInt(ConfirmedPrefsKey, 1);
                PlayerPrefs.Save();
                log.l($"CHARACTER: remembered chr={chr} confirmed=true");
            } catch (Exception ex) {
                // Restricted registry access must never undo the in-memory or
                // network choice, nor propagate into a Unity event callback.
                log.error($"CHARACTER: could not remember lastchar={chr}: {ex}");
            }
        });
    }

    // Present only after the player pressed Confirm at least once. Dismissing or
    // merely being admitted by the server does not write it.
    internal const string ConfirmedPrefsKey = "ct_char_confirmed";

    // The server's save binding wins over a local lobby pick; follow it so the
    // local preview and spawn use the same character the server will create.
    internal static void AdoptForced(byte chr) {
        if (chr > 2) throw new ArgumentOutOfRangeException(nameof(chr));
        CommandLineOverride = null;
        rememberedChoice = chr;
        PreRunScript.characterSelected = chr;
    }
    internal static void AdoptBound(byte chr) {
        if (!Util.GameIsFull() || chr > 2) return;
        if (rememberedChoice == chr) return;
        rememberedChoice = chr;
        PreRunScript.characterSelected = Local;
        log.l($"CHARACTER: following the save binding chr={chr}");
    }

    internal static bool HasConfirmedChoice {        get {
            if (!Util.GameIsFull()) return false;
            try { return PlayerPrefs.GetInt(ConfirmedPrefsKey, 0) > 0; }
            catch (Exception ex) {
                log.warn($"CHARACTER: could not read the stored confirmation; showing the panel: {ex.Message}");
                return false;
            }
        }
    }


    internal static void RestoreRememberedChoice() {
        if (!Util.GameIsFull()) return;
        int saved = rememberedChoice ?? 0;
        if (!rememberedChoice.HasValue) {
            try { saved = PlayerPrefs.GetInt("lastchar", 0); }
            catch (Exception ex) { log.error($"CHARACTER: could not read lastchar; using Experiment: {ex}"); }
        }
        rememberedChoice = (byte)(saved >= 0 && saved <= 2 ? saved : 0);
        PreRunScript.characterSelected = Local;
    }

    internal static bool Choose(int chr) => ApplyChoice(chr, true, true);

    internal static bool SendRememberedChoice(bool chosen) => ApplyChoice(Local, chosen, false);

    static bool ApplyChoice(int chr, bool chosen, bool persist) {
        if (!Util.GameIsFull()) return false;
        if (chr < 0 || chr > 2) {
            log.warn($"CHARACTER: rejected local choice chr={chr}; expected 0..2");
            Multiplayer.DoMultiplayerStatusMessageLog(Text("character_invalid", "Choose character 0, 1 or 2."));
            return false;
        }
        if (CommandLineOverride.HasValue && CommandLineOverride.Value != chr) {
            log.warn($"CHARACTER: ignoring choice chr={chr}; command-line override is {CommandLineOverride.Value}");
            PreRunScript.characterSelected = Local;
            Multiplayer.DoMultiplayerStatusMessageLog(Text("character_cli_override", "Character selection is fixed by --mp-setcharacter."));
            return false;
        }

        // A save that already owns this player's character refuses any change, even
        // while the host is back in the lobby between runs.
        if (chosen && CharacterLobby.LockedBySave && Local != (byte)chr) {
            log.warn($"CHARACTER: refused changing chr={chr}; this save already binds chr={Local}.");
            Multiplayer.DoMultiplayerStatusMessageLog(Text("character_locked_saved", "This save already has your character; it cannot be changed."));
            return false;
        }

        // Once a body exists the character is locked: the server cannot rebuild a
        // live body, and a client-only swap desyncs the appearance between peers.
        var bodyOwner = ScavPlayer.LocalPlayer;
        if (chosen && bodyOwner != null && bodyOwner.body != null && Local != (byte)chr) {
            log.warn($"CHARACTER: refused changing chr={chr}; the local player is already in the world as chr={Local}.");
            Multiplayer.DoMultiplayerStatusMessageLog(Text("character_locked_in_game", "You cannot change your character after entering the world."));
            return false;
        }

        SessionLog.Event("CHARACTER SELECTED", $"character={chr} chosen={chosen} persist={persist}");
        rememberedChoice = (byte)chr;
        PreRunScript.characterSelected = Local;
        if (persist) RememberAfterFrame(chr);

        if (Net.IsServer) {
            var local = ScavPlayer.LocalPlayer;
            if (local == null) {
                log.warn("CHARACTER: saved local choice; local player is not registered yet.");
                return true;
            }
            return ServerChoose(local.playerId, (byte)chr, chosen, null, ChipState.LocalUnchipped);
        }
        if (Net.IsClient) {
            if (!Net.IsConnected) {
                log.warn("CHARACTER: choice saved locally; client disconnected before send.");
                Multiplayer.DoMultiplayerStatusMessageLog(Text("character_need_connection", "Connect to a server first."));
                return false;
            }
            log.l($"CHARACTER: choice send begin chr={chr} chosen={chosen}");
            var writer = Net.CreateWriter(NetmsgId.SERVER_SetCharacter);
            writer.Put((byte)chr);
            writer.Put(chosen);
            writer.Put(ChipState.LocalUnchipped); // optional tail on reserved ___5; same message number
            // Optional SetCharacter tail, not an intro/protocol replacement.
            // A client CLI override must reach the server for timeout tests.
            if (PendingCharacterAdmission.RequestedTimeoutSeconds.HasValue)
                writer.Put(PendingCharacterAdmission.RequestedTimeoutSeconds.Value);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            log.l($"CHARACTER: sent choice chr={chr}");
            CharacterLobby.OnLocalChoiceSent(chosen);
            var local = ScavPlayer.LocalPlayer;
            bool hasBody = local != null && local.body != null;
            if (persist) RunUiAction("choice status", () => Multiplayer.DoMultiplayerStatusMessageLog(hasBody
                ? DeferredMessage : Text("character_saved", "Character choice saved.")));
        } else {
            Multiplayer.DoMultiplayerStatusMessageLog(Text("character_saved", "Character choice saved."));
        }
        return true;
    }

    // Existing respawn paths reset health on the same Body. Until a synchronized
    // replacement lifecycle is approved, promise only the next actual creation.
    internal static string DeferredMessage => Text("character_deferred_create",
        "Character choice saved for your next character creation. Your current body is unchanged.");

    internal static bool ServerChoose(knetid playerId, byte chr, bool chosen = true, float? timeoutSeconds = null, bool? modUnchipped = null) {
        if (!Util.GameIsFull() || !Net.IsServer) return false;
        if (chr > 2) {
            log.warn($"CHARACTER: rejected server choice player={playerId} chr={chr}; expected 0..2");
            return false;
        }
        if (PendingCharacterAdmission.IsPending(playerId))
            return PendingCharacterAdmission.Choose(playerId, chr, chosen, timeoutSeconds, modUnchipped);
        if (!ScavPlayer.ClientIdToPlayerDict.TryGetValue(playerId, out var player) || player == null) {
            log.warn($"CHARACTER: rejected choice for unknown player={playerId}");
            return false;
        }
        // A delayed pre-admission replay cannot unconfirm an admitted player.
        if (!chosen && player.characterChosen) { CharacterLobby.Publish(player); return true; }
        // The save record is authoritative: once this save binds a character for
        // this player, later lobby/command choices are ignored, not applied.
        if (chosen && CharacterRunMemory.TryGet(CharacterRunMemory.PlayerKey(player.SteamId, player.playerName), out byte bound)) {
            if (bound != chr) {
                log.warn($"CHARACTER: ignored choice chr={chr} for player={player.playerName}; the save binds chr={bound}");
                Multiplayer.DoMultiplayerStatusMessageLog(Text("character_locked_saved", "This save already has your character; it cannot be changed."));
                return false;
            }
            modUnchipped = CharacterRunMemory.GetChip(CharacterRunMemory.PlayerKey(player.SteamId, player.playerName));
        }
        var originalBody = player.body;
        if (modUnchipped.HasValue && originalBody != null && modUnchipped.Value != player.modUnchipped) {
            log.warn($"[chip] rejected change after body creation player={player.playerName}");
            CharacterLobby.Publish(player);
            return false;
        }
        player.characterSelected = chr;
        player.characterChosen = chosen;
        if (chosen && modUnchipped.HasValue) {
            CharacterRunMemory.SetIfAbsent(CharacterRunMemory.PlayerKey(player.SteamId, player.playerName), chr);
            ChipState.ApplyServer(player, modUnchipped.Value, true);
        }
        CharacterLobby.Publish(player);
        // netBody dereferences body inside its getter. A lobby player may not
        // have a body yet: save the choice now and let body creation consume it.
        SyncBody sb = null;
        if (originalBody != null && Net.ServerEntityManager != null) {
            sb = Net.ServerEntityManager.GetEntities<SyncBody>()
                .FirstOrDefault(e => e.GetParent<BasePlayer>()?.player == player);
        }
        if (sb != null) sb.characterSelected.Value = chr;
        log.l($"CHARACTER: player={player.playerName} chose chr={chr} ({ServerMain.GetCharacterPrefabPath(chr)})");
        log.l($"CHARACTER: selection player={player.playerName} sbChr={(sb != null ? sb.characterSelected.Value.ToString() : "none")} bodyUnchanged={ReferenceEquals(originalBody, player.body)} deferred={originalBody != null}");
        if (originalBody != null) {
            if (player.IsLocal) Multiplayer.DoMultiplayerStatusMessageLog(DeferredMessage);
            else ServerMain.Server_SendMPLogMessage(DeferredMessage, false, new[] { playerId });
        }
        return true;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_SetCharacter)]
    static void ReceiveSetCharacter(knetid caller, NetDataReader reader) {
        if (!Util.GameIsFull() || !Net.IsServer) return;
        int bytes = reader.AvailableBytes;
        if (bytes != 1 && bytes != 2 && bytes != 3 && bytes != 6 && bytes != 7) {
            log.warn($"CHARACTER: rejected malformed SetCharacter from player={caller}; bytes={reader.AvailableBytes}");
            return;
        }
        byte chr = reader.GetByte();
        // The old one-byte command is an explicit choice. Auto-send and panel
        // dismissal use a false flag; no extra field is added to the intro.
        byte flag = bytes >= 2 ? reader.GetByte() : (byte)1;
        if (flag > 1) {
            log.warn($"CHARACTER: rejected invalid chosen flag from player={caller}: {flag}");
            return;
        }
        bool? chip = null;
        if (bytes == 3 || bytes == 7) {
            byte value = reader.GetByte();
            if (value > 1) { log.warn($"[chip] rejected invalid chip byte from player={caller}: {value}"); return; }
            chip = value == 1;
        }
        float? timeout = bytes == 6 || bytes == 7 ? reader.GetFloat() : (float?)null;
        if (timeout.HasValue && !PendingCharacterAdmission.ValidTimeout(timeout.Value)) {
            log.warn($"CHARACTER: rejected invalid SetCharacter timeout from player={caller}: {timeout.Value}");
            return;
        }
        ServerChoose(caller, chr, flag == 1, timeout, chip);
    }

    internal static void ExecuteCommand(string[] args) {
        if (Con.GetCommandCallerPlayer() != ScavPlayer.LocalPlayer) {
            log.warn("CHARACTER: rejected forwarded command; choose on your own client.");
            return;
        }
        if (args.Length == 1) {
            Multiplayer.DoMultiplayerStatusMessageLog($"{Text("character_current", "Selected character")}: {Local} ({ServerMain.GetCharacterPrefabPath(Local)})");
            return;
        }
        if (args.Length != 2 || !int.TryParse(args[1], out int chr)) {
            log.warn("CHARACTER: rejected command; usage: character [0|1|2]");
            Multiplayer.DoMultiplayerStatusMessageLog(Text("character_invalid", "Choose character 0, 1 or 2."));
            return;
        }
        Choose(chr);
    }
#endif
}

#if FULLGAME
// Apply only the explicit process-local override. The native pre-run selection
// remains the default, and P7's local-template initialization stays unchanged.
[HarmonyPatch]
internal static class PlayerCamera_SpawnCharacter_CommandLinePatch {
    static bool Prepare() => Util.GameIsFull();
    static MethodBase TargetMethod() => AccessTools.Method(typeof(PlayerCamera), "SpawnCharacter", new[] { typeof(int) });

    static void Prefix(ref int chr) {
        chr = CharacterSelection.Local;
        PreRunScript.characterSelected = chr;
    }
}

[HarmonyPatch(typeof(PreRunScript), "Awake")]
internal static class PreRunScript_Awake_RememberCharacterPatch {
    static bool Prepare() => Util.GameIsFull();
    static void Postfix() => CharacterSelection.RestoreRememberedChoice();
}

[HarmonyPatch(typeof(PreRunScript), "SelectCharacter", new[] { typeof(int) })]
internal static class PreRunScript_SelectCharacter_RememberPatch {
    static bool Prepare() => Util.GameIsFull();
    static void Prefix(ref int chr) {
        if (CharacterSelection.CommandLineOverride.HasValue) chr = CharacterSelection.Local;
    }
    static void Postfix(int chr) => CharacterSelection.Choose(chr);
}
#endif
