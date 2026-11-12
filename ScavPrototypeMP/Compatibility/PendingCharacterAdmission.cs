#if FULLGAME
using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Together;

// Transport connections may exist without a LES NetPlayer, BasePlayer or Body.
// Only the existing SetCharacter message is accepted until admission completes.
internal static class PendingCharacterAdmission {
    // LES allocates byte player IDs. knetid is ushort: this range cannot alias
    // a real OwnerId, even on servers configured for more than 200 players.
    internal const ushort FirstTemporaryId = 60000, LastTemporaryId = 60254;
    internal static float? RequestedTimeoutSeconds { get; private set; }

    sealed class Pending {
        internal knetid Id;
        internal object Connection;
        internal Multiplayer.IntroductionPlayerInfo Intro;
        internal ulong SteamId;
        internal string CreationName;
        internal double AcceptedAt, Deadline;
        internal bool Creating, LoggedIgnoredMessage, ModUnchipped;
        internal Func<bool> Connected;
        internal Action<BasePlayer> Promote;
        internal Action Cleanup;
        internal Action<string> Reject;
    }

    static readonly Dictionary<knetid, Pending> pending = new();
    static readonly Dictionary<object, knetid> byConnection = new();
    static readonly List<BasePlayer> promoted = new();
    internal static int Count {
        get {
            // Promote rebinds the connection before Unity Start publishes the
            // ScavPlayer. A ready lobby player needs no Body yet, but must be in
            // the roster before save bindings/world-start packets are produced.
            promoted.RemoveAll(entity => entity == null || (entity.player != null &&
                ScavPlayer.ClientIdToPlayerDict.TryGetValue(entity.OwnerId, out var player) &&
                ReferenceEquals(player, entity.player)));
            return pending.Count + promoted.Count;
        }
    }

    internal static IReadOnlyList<(knetid id, string name, double elapsed)> Snapshot() {
        double now = Time.realtimeSinceStartupAsDouble;
        return Array.AsReadOnly(pending.Values.Select(entry =>
            (entry.Id, entry.Intro.userName, Math.Max(0, now - entry.AcceptedAt))).ToArray());
    }

    internal static bool ValidTimeout(float seconds) => seconds == 0f || (seconds >= 1f && seconds <= 3600f);

    static double DeadlineAfter(double acceptedAt, float seconds) =>
        seconds == 0f ? double.PositiveInfinity : acceptedAt + seconds;

    internal static void ParseTimeout(string value) {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds) && ValidTimeout(seconds)) {
            RequestedTimeoutSeconds = seconds;
            log.l($"CHARACTER: selection timeout configured seconds={seconds.ToString(CultureInfo.InvariantCulture)}");
        } else {
            log.warn($"CHARACTER: ignoring invalid --mp-charsel-timeout '{value ?? "<missing>"}'; expected 0 (no timeout) or 1..3600 seconds.");
        }
    }

    internal static bool HasName(string name) => pending.Values.Any(p => p.Intro.userName == name);
    internal static bool IsPending(knetid id) => pending.ContainsKey(id);

    internal static bool CanDispatch(knetid caller, byte message) {
        ushort id = caller;
        if (id < FirstTemporaryId || id > LastTemporaryId) return true;
        if (!pending.TryGetValue(caller, out var entry)) return false;
        if (message == (byte)NetmsgId.SERVER_SetCharacter && !entry.Creating) return true;
        if (!entry.LoggedIgnoredMessage) {
            entry.LoggedIgnoredMessage = true;
            log.warn($"CHARACTER: pending player={entry.Intro.userName} tempId={id}; ignoring messages until confirmation.");
        }
        return false;
    }

    internal static bool Begin(object connection, Multiplayer.IntroductionPlayerInfo intro, ulong steamId, string creationName,
        Func<bool> connected, Action<knetid> bindPending, Action<BasePlayer> promote, Action cleanup, Action<string> reject) {
        if (!Util.GameIsFull() || !Net.IsServer || !Net.IsRunning) return false;
        if (byConnection.ContainsKey(connection)) {
            log.warn($"CHARACTER: duplicate pending connection player={intro.userName}");
            return false;
        }
        knetid id = 0;
        for (ushort candidate = FirstTemporaryId; candidate <= LastTemporaryId; candidate++) {
            if (!pending.ContainsKey((knetid)candidate)) { id = candidate; break; }
        }
        if ((ushort)id == 0) {
            log.warn($"CHARACTER: pending slots exhausted; rejecting player={intro.userName}");
            reject("Too many players choosing a character. Please try again.");
            return false;
        }
        if (intro.characterSelected > 2) intro.characterSelected = 0;
        double now = Time.realtimeSinceStartupAsDouble;
        var entry = new Pending {
            Id = id, Connection = connection, Intro = intro, SteamId = steamId, CreationName = creationName,
            AcceptedAt = now, Deadline = DeadlineAfter(now, RequestedTimeoutSeconds ?? 60f),
            Connected = connected, Promote = promote, Cleanup = cleanup, Reject = reject
        };
        pending.Add(id, entry);
        byConnection.Add(connection, id);
        try {
            bindPending(id);
            log.l($"CHARACTER: pending player={intro.userName} tempId={id} chr={intro.characterSelected} timeout={RequestedTimeoutSeconds ?? 60f:0.###}");
            log.l($"CHARACTER: roster {intro.userName}=unset chosen=false");

            // Rejoining the same save: the server already knows this player's pick,
            // so admit immediately and let the client skip the panel.
            if (CharacterRunMemory.TryGet(CharacterRunMemory.PlayerKey(steamId, creationName), out byte stored)) {
                entry.Intro.characterSelected = stored;
                entry.ModUnchipped = CharacterRunMemory.GetChip(CharacterRunMemory.PlayerKey(steamId, creationName));
                log.l($"CHARACTER: stored choice for this save player={intro.userName} chr={stored}; admitting without the panel");
                return Complete(entry, true);
            }
            return true;
        } catch (Exception ex) {
            log.error($"CHARACTER: pending bind failed player={intro.userName}: {ex}");
            Remove(entry);
            reject("Character selection connection failed.");
            return false;
        }
    }

    internal static bool Choose(knetid caller, byte chr, bool chosen, float? timeoutSeconds, bool? modUnchipped = null) {
        if (!pending.TryGetValue(caller, out var entry) || entry.Creating || chr > 2) return false;
        if (timeoutSeconds.HasValue) {
            if (!ValidTimeout(timeoutSeconds.Value)) {
                log.warn($"CHARACTER: rejected pending timeout player={entry.Intro.userName}: {timeoutSeconds.Value}");
                return false;
            }
            // Measure from acceptance, not from the latest message: replay or
            // repeated dismissals cannot keep a half-connection alive forever.
            entry.Deadline = DeadlineAfter(entry.AcceptedAt, timeoutSeconds.Value);
        }
        entry.Intro.characterSelected = chr;
        if (modUnchipped.HasValue) entry.ModUnchipped = modUnchipped.Value;
        if (!chosen) {
            log.l($"CHARACTER: pending choice player={entry.Intro.userName} chr={chr} chosen=false; no entity created");
            return true;
        }
        // The save is authoritative: a record already stored for this save/player
        // beats whatever the lobby sent, and the admission uses the stored value.
        if (CharacterRunMemory.TryGet(CharacterRunMemory.PlayerKey(entry.SteamId, entry.CreationName), out byte bound)) {
            if (bound != chr) {
                log.l($"CHARACTER: ignored lobby choice chr={chr} for player={entry.Intro.userName}; the save binds chr={bound}");
            }
            entry.Intro.characterSelected = bound;
            entry.ModUnchipped = CharacterRunMemory.GetChip(CharacterRunMemory.PlayerKey(entry.SteamId, entry.CreationName));
        }
        return Complete(entry, true);
    }

    static bool Complete(Pending entry, bool chosen) {
        if (entry.Creating) return false;
        entry.Creating = true;
        BasePlayer entity = null;
        try {
            if (!entry.Connected()) { Remove(entry); return false; }
            // Keep the original creation/Start/OnPlayerJoined/LES baseline and
            // late-join world flow. Only its admission time has moved.
            entity = BasePlayer.ServerCreatePlayer(entry.CreationName, entry.SteamId, entry.Intro.color,
                false, entry.Intro.characterSelected, chosen, entry.ModUnchipped);
            if (entity == null || entity.player == null)
                throw new InvalidOperationException("Player prefab was not created for the admitted connection.");
            entry.Promote(entity); // rebind the transport before Unity Start
            promoted.Add(entity);
            Remove(entry);
            if (chosen) CharacterRunMemory.SetIfAbsent(CharacterRunMemory.PlayerKey(entry.SteamId, entry.CreationName), entry.Intro.characterSelected);
            ChipState.ApplyServer(entity.player, entry.ModUnchipped, chosen);
            log.l($"CHARACTER: admitted player={entry.Intro.userName} tempId={entry.Id} playerId={entity.OwnerId} reason={(chosen ? "confirmed" : "timeout")}");
            if (chosen) log.l($"CHARACTER: player={entry.Intro.userName} chose chr={entry.Intro.characterSelected} ({ServerMain.GetCharacterPrefabPath(entry.Intro.characterSelected)})");
            return true;
        } catch (Exception ex) {
            log.error($"CHARACTER: admission failed player={entry.Intro.userName}: {ex}");
            Remove(entry);
            if (entity != null) Net.RemoveServerPlayer(entity.serverNetPlayer);
            entry.Reject("Character creation failed. Please reconnect.");
            return false;
        }
    }

    static void Remove(Pending entry) {
        pending.Remove(entry.Id);
        byConnection.Remove(entry.Connection);
        try { entry.Cleanup(); }
        catch (Exception ex) { log.error($"CHARACTER: pending cleanup failed player={entry.Intro.userName}: {ex}"); }
    }

    internal static void Disconnect(object connection) {
        if (connection == null || !byConnection.TryGetValue(connection, out var id) || !pending.TryGetValue(id, out var entry)) return;
        Remove(entry);
        log.l($"CHARACTER: pending disconnected player={entry.Intro.userName} tempId={id}; no entity created");
    }

    internal static bool Reject(knetid id, string reason) {
        if (!Util.GameIsFull() || !Net.IsServer || !pending.TryGetValue(id, out var entry) || entry.Creating) return false;
        // Remove first: transport disconnect callbacks may run synchronously.
        // No player factory, body or save binding is touched by this path.
        Remove(entry);
        try { entry.Reject(reason); }
        catch (Exception ex) { log.error($"CHARACTER: could not disconnect pending player={entry.Intro.userName}: {ex}"); }
        log.l($"CHARACTER: pending rejected player={entry.Intro.userName} tempId={id} reason={reason}");
        return true;
    }

    internal static void Tick() {
        if (!Util.GameIsFull() || !Net.IsServer || !Net.IsRunning) return;
        foreach (var entry in pending.Values.ToArray()) {
            if (entry.Creating || !pending.ContainsKey(entry.Id)) continue;
            if (!entry.Connected()) { Disconnect(entry.Connection); continue; }
            if (double.IsPositiveInfinity(entry.Deadline) || Time.realtimeSinceStartupAsDouble < entry.Deadline) continue;
            log.warn($"CHARACTER: admission timeout player={entry.Intro.userName} tempId={entry.Id} seconds={entry.Deadline - entry.AcceptedAt:0.###}; disconnecting without creating an entity.");
            Reject(entry.Id, CharacterSelection.Text("character_timeout", "Character selection timed out. Please reconnect and choose a character."));
        }
    }

    internal static void Reset() {
        foreach (var entry in pending.Values.ToArray()) Remove(entry);
        promoted.Clear();
    }
}
#endif
