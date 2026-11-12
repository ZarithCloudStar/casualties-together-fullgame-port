#if FULLGAME
using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Together;

// Server-side, per-save memory of the character each player confirmed.
//
// A save is identified by the world generation seed. A brand new run rolls a new
// seed, so lookup misses and players pick again; rejoining an existing save (even
// after a host restart) hits the same seed and is admitted silently.
//
// Stored beside the multiplayer save so deleting the save also forgets the picks:
//   <persistentDataPath>/mp_save/character_choices.txt
// Format:
//   seed=<int>
//   <seed>|<playerKey>|<chr>|<modUnchipped:0/1> (legacy three columns accepted)
internal static class CharacterRunMemory {
    const string FileName = "character_choices.txt";
    sealed class Record {
        internal int Seed;
        internal string PlayerKey;
        internal byte Character;
    }

    static readonly Dictionary<string, byte> choices = new(StringComparer.Ordinal);
    static readonly Dictionary<string, bool> chips = new(StringComparer.Ordinal);
    static int storedSeed;
    static bool loaded;
    static bool dirty;

    static string DirectoryPath => SavesystemPatch.mpsavefolder;
    static string FilePath => Path.Combine(DirectoryPath, FileName);

    internal static string PlayerKey(ulong steamId, string name) =>
        steamId != 0 ? "s:" + steamId.ToString(CultureInfo.InvariantCulture) : "n:" + (name ?? "");

    // Seed of the save currently loaded. It only changes when a new world is
    // actually generated, so restarting the host keeps the same identity.
    internal static int CurrentSeed {
        get { EnsureLoaded(); return storedSeed; }
    }

    // Called once a world has finished generating. A brand new run rolls a new
    // seed, which invalidates every remembered pick for the previous save.
    internal static void AdoptGeneratedSeed() {
        if (!Util.GameIsFull() || !Net.IsServer) return;
        // Loading a save also runs the finish-worldgen coroutine. Replacing the
        // identity there would orphan every binding made for the loaded save.
        if (SaveSystem.loadedRun) {
            log.l("CHARACTER: keeping the loaded save identity; no new seed adopted");
            EnsureIdentityForLoad("loading the save");
            return;
        }
        int generated = 0;
        try {
            // LastBeforeGenerationState.randomstate is a UnityEngine.Random.State.
            // Its hash is deterministic for identical field values within this
            // runtime, which is all a save identity needs.
            generated = WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.randomstate.GetHashCode();
        } catch (Exception ex) {
            log.warn($"CHARACTER: could not read the world seed: {ex.Message}");
            return;
        }
        if (generated == int.MinValue) generated = int.MaxValue;
        if (generated < 0) generated = -generated;
        if (generated == 0) return;
        EnsureLoaded();
        if (generated == storedSeed) return;
        storedSeed = generated;
        // A brand new save starts with an empty binding set (mp_save was deleted
        // by the host's StartRun, so stale rows must not resurface here).
        choices.Clear();
        chips.Clear();
        // Players who already picked in the lobby (before this save existed) made
        // their choice for this run: it becomes this save's binding.
        int carried = 0;
        foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values) {
            if (player == null || !player.characterChosen || player.characterSelected > 2) continue;
            string key = Compose(storedSeed, PlayerKey(player.SteamId, player.playerName));
            choices[key] = player.characterSelected;
            chips[key] = player.modUnchipped;
            carried++;
        }
        dirty = true;
        Save();
        log.l($"CHARACTER: new save identity seed={storedSeed}; character choices reset, {carried} lobby pick(s) carried over");
    }

    internal static bool TryGet(string playerKey, out byte chr) {
        chr = 0;
        if (string.IsNullOrEmpty(playerKey)) return false;
        // Only a loaded save can bind a character. In the lobby (host has not
        // loaded or created a save yet) players must always be able to pick.
        if (!Util.IsInWorld()) return false;
        int seed = CurrentSeed;
        if (seed == 0) return false;
        EnsureLoaded();
        return choices.TryGetValue(Compose(seed, playerKey), out chr) && chr <= 2;
    }

    // Missing fourth column in pre-chip saves means chipped, without touching
    // vanilla WorldGeneration.unchipped. The server owns this save memory.
    internal static bool GetChip(string playerKey) {
        EnsureLoaded();
        return chips.TryGetValue(Compose(storedSeed, playerKey), out bool value) && value;
    }

    internal static void SetChip(string playerKey, bool unchipped) {
        if (!Net.IsServer || string.IsNullOrEmpty(playerKey) || CurrentSeed == 0) return;
        string key = Compose(storedSeed, playerKey);
        if (!choices.ContainsKey(key)) return; // no unconfirmed player records
        // As with character bindings, a menu pick for a new run must not
        // overwrite the still-present save before StartRun rolls a new seed.
        if (chips.TryGetValue(key, out bool saved)) {
            if (saved != unchipped) log.l($"[chip] kept save binding player={playerKey} unchipped={saved}; ignored lobby draft={unchipped}");
            return;
        }
        chips[key] = unchipped;
        dirty = true;
        Save();
    }

    internal static void Set(string playerKey, byte chr) {
        if (string.IsNullOrEmpty(playerKey) || chr > 2) return;
        int seed = CurrentSeed;
        if (seed == 0) {
            log.warn("CHARACTER: no save identity yet; the choice will not be remembered for this save.");
            return;
        }
        EnsureLoaded();
        choices[Compose(seed, playerKey)] = chr;
        dirty = true;
        Save();
        log.l($"CHARACTER: remembered for save seed={seed} player={playerKey} chr={chr}");
    }

    // Loading a save that arrived without our sidecar (another host took over the
    // world, or the file was not copied) leaves no identity at all, which would
    // silently disable bindings forever. Give that save a fresh identity once.
    internal static void EnsureIdentityForLoad(string reason) {
        if (!Util.GameIsFull() || !Net.IsServer) return;
        EnsureLoaded();
        if (storedSeed != 0) return;
        int generated = UnityEngine.Random.Range(1, int.MaxValue);
        storedSeed = generated;
        dirty = true;
        Save();
        log.l($"CHARACTER: no save identity file; created seed={storedSeed} for {reason}");
    }

    // The first confirmed pick for a save is the binding. Later picks (lobby,
    // command, reconnect) must never overwrite it: the save owns the character.
    internal static bool SetIfAbsent(string playerKey, byte chr) {
        if (string.IsNullOrEmpty(playerKey) || chr > 2) return false;
        int seed = CurrentSeed;
        if (seed == 0) return false;
        EnsureLoaded();
        string key = Compose(seed, playerKey);
        if (choices.ContainsKey(key)) {
            log.l($"CHARACTER: kept the existing binding for save seed={seed} player={playerKey} chr={choices[key]} (ignored chr={chr})");
            return false;
        }
        choices[key] = chr;
        dirty = true;
        Save();
        log.l($"CHARACTER: bound save seed={seed} player={playerKey} chr={chr}");
        return true;
    }

    // Loading a save must beat whatever the lobby produced: players may have
    // picked again while the host was in the menu, but the save owns their
    // character. Called right before the world is loaded so body creation reads
    // the bound value.
    internal static void ReapplyBindingsToPlayers(string reason) {
        if (!Util.GameIsFull() || !Net.IsServer) return;
        EnsureLoaded();
        int seed = CurrentSeed;
        if (seed == 0) return;
        int applied = 0, kept = 0;
        foreach (var player in ScavPlayer.ClientIdToPlayerDict.Values) {
            if (player == null) continue;
            string key = PlayerKey(player.SteamId, player.playerName);
            if (!choices.TryGetValue(Compose(seed, key), out byte chr) || chr > 2) continue;
            ChipState.ApplyServer(player, GetChip(key), false);
            if (player.characterSelected == chr && player.characterChosen) { kept++; continue; }
            log.l($"CHARACTER: save binding overrides lobby pick for player={player.playerName}: {player.characterSelected} -> {chr}");
            player.characterSelected = chr;
            player.characterChosen = true;
            CharacterLobby.Publish(player);
            applied++;
        }
        log.l($"CHARACTER: reapplied {applied} save binding(s) before {reason} ({kept} already correct)");
    }

    static string Compose(int seed, string playerKey) => seed.ToString(CultureInfo.InvariantCulture) + "|" + playerKey;

    static void EnsureLoaded() {
        if (loaded) return;
        loaded = true;
        try {
            if (!File.Exists(FilePath)) return;
            foreach (string raw in File.ReadAllLines(FilePath)) {
                string line = raw?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("seed=", StringComparison.Ordinal)) {
                    int.TryParse(line.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out storedSeed);
                    continue;
                }
                string[] parts = line.Split('|');
                if (parts.Length != 3 && parts.Length != 4) continue;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)) continue;
                if (!byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte chr) || chr > 2) continue;
                string key = Compose(seed, parts[1]);
                choices[key] = chr;
                chips[key] = parts.Length == 4 && parts[3] == "1";
            }
            log.l($"CHARACTER: loaded {choices.Count} remembered choice(s), last seed={storedSeed}");
        } catch (Exception ex) {
            log.error($"CHARACTER: could not read {FileName}: {ex.Message}");
        }
    }

    static void Save() {
        if (!dirty) return;
        dirty = false;
        try {
            Directory.CreateDirectory(DirectoryPath);
            var lines = new List<string> { "# Casualties Together: confirmed characters per save seed", "seed=" + storedSeed.ToString(CultureInfo.InvariantCulture) };
            foreach (var pair in choices) lines.Add(pair.Key + "|" + pair.Value.ToString(CultureInfo.InvariantCulture) + "|" + (chips.TryGetValue(pair.Key, out bool value) && value ? "1" : "0"));
            File.WriteAllLines(FilePath, lines);
        } catch (Exception ex) {
            log.error($"CHARACTER: could not write {FileName}: {ex.Message}");
        }
    }
}
#endif
