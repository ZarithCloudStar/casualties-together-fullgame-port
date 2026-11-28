using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

// Metadata only. Audio files never leave their owner's custommusic directory.
internal sealed class MP3Track {
    internal string Name, Hash, Id;
    internal long Size;
}
internal sealed class MP3LibrarySource {
    internal knetid Owner;
    internal string Name;
    internal List<MP3Track> Tracks = new();
}
internal static class MP3MultiLibrary {
    internal const int MaxTracks = 256;
    internal static bool SyncEnabled => Net.IsRunning && Multiplayer.rules.EnableMP3Sync;
    internal static bool Enabled => SyncEnabled && Multiplayer.rules.AllowClientMP3Sources;
    internal static readonly Dictionary<knetid, MP3LibrarySource> Sources = new();
    internal static List<MP3Track> LocalTracks = new();
    internal static int Revision;
    internal static string Folder => Path.Combine(Application.dataPath, "custommusic");
    internal static string TrackId(string name, long size, string hash) {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(name + "\0" + size + "\0" + hash)));
    }
    static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    internal static string FileHash(string path) {
        using var sha = SHA256.Create();
        using var file = File.OpenRead(path);
        return Hex(sha.ComputeHash(file));
    }
    internal static bool Supported(string name) {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext == ".mp3" || ext == ".wav" || ext == ".ogg";
    }
    internal static bool SafeName(string name) => !string.IsNullOrEmpty(name) && name.Length <= 240 &&
        name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Supported(name);
    internal static Task<List<MP3Track>> Scan(string folder) => Task.Run(() => {
        var tracks = new List<MP3Track>();
        try {
            if (!Directory.Exists(folder)) return tracks;
            foreach (var path in Directory.GetFiles(folder).OrderBy(x => x, StringComparer.Ordinal)) {
                if (tracks.Count == MaxTracks) break;
                try {
                    var name = Path.GetFileName(path);
                    if (!SafeName(name)) continue;
                    var file = new FileInfo(path);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length <= 0) continue;
                    var hash = FileHash(path);
                    tracks.Add(new MP3Track { Name = name, Size = file.Length, Hash = hash, Id = TrackId(name, file.Length, hash) });
                } catch (Exception) { /* One unreadable file must not abort the library. */ }
            }
        } catch (Exception) { }
        return tracks;
    });
    internal static void WriteTracks(NetDataWriter w, List<MP3Track> tracks) {
        w.Put(tracks.Count);
        foreach (var t in tracks) { w.Put(t.Name); w.Put(t.Size); w.Put(t.Hash); w.Put(t.Id); }
    }
    static List<MP3Track> ReadTracks(NetDataReader r) {
        int count = r.GetInt();
        if (count < 0 || count > MaxTracks) throw new InvalidDataException("MP3 track count");
        var result = new List<MP3Track>();
        var seen = new HashSet<string>();
        for (int i = 0; i < count; i++) {
            string name = r.GetString(240); long size = r.GetLong();
            string hash = r.GetString(64), id = r.GetString(64);
            if (!SafeName(name) || size <= 0 || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || id != TrackId(name, size, hash))
                throw new InvalidDataException("MP3 metadata");
            if (seen.Add(id)) result.Add(new MP3Track { Name = name, Size = size, Hash = hash, Id = id });
        }
        return result;
    }
    internal static void Report() {
        if (!Enabled || ScavPlayer.LocalPlayer == null) return;
        if (Net.IsServer) {
            Sources[(ushort)0] = new MP3LibrarySource { Owner = (ushort)0, Name = ScavPlayer.LocalPlayer.playerName, Tracks = LocalTracks };
            Broadcast();
        } else {
            var w = Net.CreateWriter(NetmsgId.SERVER_MP3Library);
            WriteTracks(w, LocalTracks);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
        }
    }
    internal static void Broadcast() {
        if (!Enabled || !Net.IsServer) return;
        foreach (var p in ScavPlayer.ClientIdToPlayerDict.Values.ToArray()) {
            if (!Sources.TryGetValue(p.playerId, out var source)) Sources[p.playerId] = source = new MP3LibrarySource { Owner = p.playerId };
            source.Name = p.playerName;
        }
        if (!Sources.ContainsKey((ushort)0)) Sources[(ushort)0] = new MP3LibrarySource { Owner = (ushort)0, Name = Lang.Get("mp3_host") };
        foreach (var id in Sources.Keys.Where(id => id.id != 0 && !ScavPlayer.ClientIdToPlayerDict.ContainsKey(id)).ToArray()) Sources.Remove(id);
        Revision++;
        var targets = ServerMain.AllPlayersExceptHost.ToList();
        // One bounded packet per owner also stays below Steam's reliable-message size limit.
        foreach (var s in Sources.Values.OrderBy(s => s.Owner.id)) {
            var w = Net.CreateWriter(NetmsgId.CLIENT_MP3Libraries);
            w.Put(Sources.Count);
            foreach (var owner in Sources.Keys.OrderBy(id => id.id)) w.Put(owner);
            w.Put(s.Owner); w.Put(s.Name ?? ""); WriteTracks(w, s.Tracks);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, targets);
        }
    }
    [ServerReceiver((byte)NetmsgId.SERVER_MP3Library)]
    static void ReceiveLibrary(knetid cid, NetDataReader r) {
        if (!Enabled || !ScavPlayer.TryGetPlayerFromClientId(cid, out var player) || cid.id == 0) return;
        try {
            var tracks = ReadTracks(r);
            Sources[cid] = new MP3LibrarySource { Owner = cid, Name = player.playerName, Tracks = tracks };
            Broadcast();
            if (MP3Menu.dropdownList != null && !MP3PlayerServerAudioStreamer.unprivated_MP3Menu_loadingMusic)
                MP3PlayerServerAudioStreamer.Server_SendMusicList(new[] { player });
            else if (MP3Menu.dropdownList == null) MP3PlayerServerAudioStreamer.Server_ForceLoadMusic();
        } catch (Exception e) { log.warn("MP3 library rejected: " + e.Message); }
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_MP3Libraries)]
    static void ReceiveLibraries(knetid _, NetDataReader r) {
        if (!Enabled || Net.IsServer) return;
        try {
            int count = r.GetInt();
            if (count < 1 || count > 256) return;
            var roster = new HashSet<knetid>();
            for (int i = 0; i < count; i++) { r.Get(out knetid id); roster.Add(id); }
            r.Get(out knetid owner); string name = r.GetString(256);
            var tracks = ReadTracks(r);
            if (!roster.Contains(owner) || !roster.Contains((ushort)0)) return;
            foreach (var id in Sources.Keys.Where(id => !roster.Contains(id)).ToArray()) Sources.Remove(id);
            Sources[owner] = new MP3LibrarySource { Owner = owner, Name = name, Tracks = tracks };
            Revision++;
        } catch (Exception e) { log.warn("MP3 catalog ignored: " + e.Message); }
    }

    internal sealed class Session {
        internal knetid Item, Owner, Requester;
        internal uint Token, Sequence;
        internal float LastFrame, Credit = 12, CreditTime;
    }
    static readonly Dictionary<knetid, Session> Sessions = new();
    // Per-item generation prevents delayed audio/control packets from reviving an old song.
    static readonly Dictionary<knetid, (uint token, bool active, bool clientSource)> Playback = new();
    static uint generation;
    internal static uint Token(knetid item) => Sessions.TryGetValue(item, out var s) ? s.Token : 0;
    internal static bool AcceptFrame(knetid item, uint token, bool clientSource = false) {
        if (!SyncEnabled || (clientSource && !Enabled)) return false;
        if (Playback.TryGetValue(item, out var state)) {
            if (token < state.token || (token == state.token && !state.active)) return false;
        }
        Playback[item] = (token, true, clientSource);
        return true;
    }
    internal static bool Near(Item item, ScavPlayer p) => p != null && item != null && ScavPlayer.GetPlayersInRadius(item.transform.position, 82).Contains(p);
    internal static void Request(Item item, knetid owner, string track, int hostIndex) {
        if (!Enabled || item == null || !NetObjectRegistry.TryGetSyncInfoOrRegister(item, out var si)) return;
        var w = Net.CreateWriter(NetmsgId.SERVER_MP3PlayTrack);
        w.Put(si.syncId); w.Put(owner); w.Put(track ?? ""); w.Put(hostIndex);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
    }
    internal static void LegacyHostRequest(knetid cid, knetid item, int index) {
        var w = new NetDataWriter();
        w.Put(item); w.Put((ushort)0); w.Put(""); w.Put(index);
        PlayTrack(cid, new NetDataReader(w));
    }
    [ServerReceiver((byte)NetmsgId.SERVER_MP3PlayTrack)]
    static void PlayTrack(knetid cid, NetDataReader r) {
        if (!SyncEnabled) return;
        r.Get(out knetid id); r.Get(out knetid owner); string track = r.GetString(64); int index = r.GetInt();
        if (owner.id != 0 && !Enabled) return;
        if (!ScavPlayer.TryGetPlayerFromClientId(cid, out var p) || !p.IsConscious() ||
            !NetObjectRegistry.TryGetSyncInfo(id, out var si) || !si.IsItem() || si.item.id != "mp3player" ||
            !ItemSync.CheckIfBodyReachThisItem(si, p.body)) return;
        if (owner.id == 0 && index == -1 && track.Length == 0) { Stop(id); return; }
        if (si.item.condition <= 0) return;
        MP3Track selected = null;
        if (owner.id == 0) {
            if (MP3PlayerServerAudioStreamer.unprivated_MP3Menu_loadingMusic || MP3Menu.clips == null || index < 0 || index >= MP3Menu.clips.Count) return;
        } else {
            if (!Sources.TryGetValue(owner, out var source) || (selected = source.Tracks.Find(t => t.Id == track)) == null ||
                !ScavPlayer.TryGetPlayerFromClientId(owner, out var sourcePlayer) || !Near(si.item, sourcePlayer)) {
                Notice(cid); return;
            }
        }
        Stop(id);
        var session = new Session { Item = id, Owner = owner, Requester = cid, Token = ++generation,
            LastFrame = Time.unscaledTime, CreditTime = Time.unscaledTime };
        Sessions[id] = session;
        SendControl(id, session.Token, true, owner.id != 0);
        if (owner.id == 0) {
            MP3Menu_Play_MultiplayerPatch.Server_PlayThisSongOnThisMp3Player(MP3Menu.clips[index], si.item);
        } else {
            var w = Net.CreateWriter(NetmsgId.CLIENT_MP3PlayLocal);
            w.Put(id); w.Put(session.Token); WriteTracks(w, new List<MP3Track> { selected });
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, owner);
        }
    }
    static void Notice(knetid target) {
        // Use the MP3 control channel for a localized, non-fatal playback error.
        var w = Net.CreateWriter(NetmsgId.CLIENT_MP3Stop); w.Put((ushort)0); w.Put((uint)0); w.Put(false); w.Put(true); w.Put(false);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, target);
    }
    internal static void Stop(knetid id) {
        bool clientSource = Sessions.TryGetValue(id, out var previous) && previous.Owner.id != 0;
        Sessions.Remove(id);
        if (NetObjectRegistry.TryGetSyncInfo(id, out var si)) {
            var streamer = si.go.GetComponent<MP3PlayerServerAudioStreamer>();
            if (streamer != null) streamer.StopMusic();
        }
        SendControl(id, ++generation, false, clientSource);
    }
    static void SendControl(knetid id, uint token, bool active, bool clientSource) {
        ApplyControl(id, token, active, clientSource);
        var w = Net.CreateWriter(NetmsgId.CLIENT_MP3Stop);
        w.Put(id); w.Put(token); w.Put(active); w.Put(false); w.Put(clientSource);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, w, ServerMain.AllPlayersExceptHost.ToList());
    }
    static void ApplyControl(knetid id, uint token, bool active, bool clientSource) {
        if (Playback.TryGetValue(id, out var old) && token < old.token) return;
        active &= SyncEnabled && (!clientSource || Enabled);
        Playback[id] = (token, active, clientSource);
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si)) return;
        var source = si.go.GetComponent<MP3PlayerClientAudioStreamer>();
        if (source != null && (!active || source.Token != token)) source.Cancel();
        var output = si.go.GetComponent<MP3PlayerAudioStreamPlayer_ClientOnly>();
        if (output != null) { output.Silence(); UnityEngine.Object.Destroy(output); }
        var monitor = si.go.transform.Find("mp3_host_monitor")?.GetComponent<MP3HostAudioMonitor>();
        if (monitor != null) { monitor.Silence(); UnityEngine.Object.Destroy(monitor.gameObject); }
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_MP3Stop, false)]
    static void ReceiveControl(knetid _, NetDataReader r) {
        r.Get(out knetid id); uint token = r.GetUInt(); bool active = r.GetBool(), error = r.GetBool(), clientSource = r.GetBool();
        if (error) { Multiplayer.DoMultiplayerStatusMessageError(Lang.Get("mp3_load_failed")); return; }
        ApplyControl(id, token, active, clientSource);
    }
    [ClientReceiver((byte)NetmsgId.CLIENT_MP3PlayLocal)]
    static void PlayLocal(knetid _, NetDataReader r) {
        if (!Enabled || !Net.IsClient) return;
        r.Get(out knetid id); uint token = r.GetUInt();
        var tracks = ReadTracks(r);
        if (tracks.Count != 1 || !AcceptFrame(id, token, true)) return;
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || !si.IsItem() || si.item.id != "mp3player" || !Near(si.item, ScavPlayer.LocalPlayer)) {
            EndLocal(id, token, true); return;
        }
        var old = si.go.GetComponent<MP3PlayerClientAudioStreamer>();
        if (old != null) { old.Cancel(); UnityEngine.Object.Destroy(old); }
        si.go.AddComponent<MP3PlayerClientAudioStreamer>().Begin(id, token, tracks[0]);
    }
    internal static void EndLocal(knetid id, uint token, bool failed) {
        if (!Enabled || !Net.IsClient) return;
        var w = Net.CreateWriter(NetmsgId.SERVER_MP3SourceEnded); w.Put(id); w.Put(token); w.Put(failed);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, w);
    }
    [ServerReceiver((byte)NetmsgId.SERVER_MP3SourceEnded)]
    static void SourceEnded(knetid cid, NetDataReader r) {
        r.Get(out knetid id); uint token = r.GetUInt(); bool failed = r.GetBool();
        if (Sessions.TryGetValue(id, out var s) && s.Owner.Equals(cid) && s.Token == token) {
            if (failed) Notice(s.Requester);
            Stop(id);
        }
    }
    internal static MP3HostAudioMonitor HostMonitor(Item item) {
        if (item == null) return null;
        var child = item.transform.Find("mp3_host_monitor")?.GetComponent<MP3HostAudioMonitor>();
        if (child != null) return child;
        var go = new GameObject("mp3_host_monitor");
        go.transform.SetParent(item.transform, false);
        try { var m = go.AddComponent<MP3HostAudioMonitor>(); m.Item = item; return m; }
        catch (System.Exception e) { log.warn("[mp3] host monitor init failed: " + e.Message); UnityEngine.Object.Destroy(go); return null; }
    }

    // Host is also a listener but must not round-trip its own relay through the
    // network message queue: feeding the already-decoded frame locally keeps
    // the 20 ms cadence intact and removes the jitter that caused stutter.
    internal static void FeedHost(MP3HostAudioMonitor monitor, byte freqIndex, byte[] data) {
        if (monitor == null || data == null || data.Length == 0) return;
        if (freqIndex >= AudioOpusEncoding.STANDART_FREQUENCIES.Length) return;
        var freq = AudioOpusEncoding.STANDART_FREQUENCIES[freqIndex];
        if (!monitor.OpusDecode(freq, data, out var frame)) return;
        monitor.NoteFrame();
        monitor.ReceiveVoiceBlob(freq, frame, true);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_MP3AudioFrame)]
    static void RelayFrame(knetid cid, NetDataReader r) {
        if (!Enabled || SharedMain.LocalWorldIsGenerating) return;
        r.Get(out knetid id); uint token = r.GetUInt(), sequence = r.GetUInt(); byte freqIndex = r.GetByte();
        if (!Sessions.TryGetValue(id, out var s) || !s.Owner.Equals(cid) || s.Token != token || sequence <= s.Sequence ||
            freqIndex >= AudioOpusEncoding.STANDART_FREQUENCIES.Length) return;
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || !si.IsItem() || si.item.id != "mp3player" || si.item.condition <= 0 ||
            !ScavPlayer.TryGetPlayerFromClientId(cid, out var owner) || !Near(si.item, owner)) { Stop(id); return; }
        s.Credit = Mathf.Min(30, s.Credit + (Time.unscaledTime - s.CreditTime) * 60);
        s.CreditTime = Time.unscaledTime;
        if (s.Credit < 1) return;
        r.Get(out byte[] data);
        if (data.Length == 0 || data.Length > 1024) return;
        s.Credit--; s.Sequence = sequence; s.LastFrame = Time.unscaledTime;
        var w = Net.CreateWriter(NetmsgId.CLIENT_StreamedAudioToClients);
        w.Put(id); w.Put(freqIndex); w.PutBytesWithLength(data); w.Put(token); w.Put(true);
        var listeners = ScavPlayer.GetPlayersInRadius(si.go.transform.position, 82);
        var host = listeners.FirstOrDefault(p => p != null && p.IsHost);
        if (host != null) {
            var monitor = HostMonitor(si.item);
            if (monitor != null) { monitor.Item = si.item; FeedHost(monitor, freqIndex, data); }
        }
        var targets = listeners.Where(p => !p.playerId.Equals(cid) && !p.IsHost).Select(p => p.playerId).ToList();
        if (targets.Count > 0) Net.Server_SendToClients(DeliveryMethod.Unreliable, w, targets);
    }
    internal static void TickSessions() {
        if (!Net.IsServer) return;
        foreach (var pair in Sessions.ToArray()) {
            var s = pair.Value;
            if (!SyncEnabled || (s.Owner.id != 0 && !Enabled) || SharedMain.LocalWorldIsGenerating || !NetObjectRegistry.TryGetSyncInfo(s.Item, out var si) || !si.IsItem() || si.item.condition <= 0) {
                Stop(pair.Key); continue;
            }
            if (s.Owner.id != 0) {
                if (!ScavPlayer.TryGetPlayerFromClientId(s.Owner, out var owner) || !Near(si.item, owner) ||
                    Time.unscaledTime - s.LastFrame > (s.Sequence == 0 ? 30 : 3)) Stop(pair.Key);
            } else {
                var streamer = si.go.GetComponent<MP3PlayerServerAudioStreamer>();
                if (streamer == null || !streamer.IsPlaying) Stop(pair.Key);
            }
        }
    }
    internal static void DisableClientSources() {
        if (Net.IsServer) foreach (var s in Sessions.Values.Where(s => s.Owner.id != 0).ToArray()) Stop(s.Item);
        Sources.Clear(); LocalTracks = new List<MP3Track>(); Revision++;
        foreach (var pair in Playback.Where(p => p.Value.clientSource).ToArray())
            ApplyControl(pair.Key, pair.Value.token, false, true);
        foreach (var source in UnityEngine.Object.FindObjectsOfType<MP3PlayerClientAudioStreamer>()) {
            source.Cancel(); UnityEngine.Object.Destroy(source);
        }
        if (SyncEnabled) foreach (var menu in UnityEngine.Object.FindObjectsOfType<MP3MultiMenu>()) menu.RestoreHostNow();
    }
    // A layer transition destroys the old world objects, but the custom-music
    // metadata and clips are session-scoped. Keep them so the host does not
    // synchronously reload every track at the start of every layer.
    internal static void ResetWorld() {
        Sources.Clear(); Sessions.Clear(); Playback.Clear(); Revision++;
        MP3PlayerServerAudioStreamer.client_host_list_ready = false;
        if (MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance != null) {
            UnityEngine.Object.Destroy(MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance);
            MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance = null;
        }
        MP3PlayerServerAudioStreamer.unprivated_MP3Menu_loadingMusic = false;
        foreach (var menu in UnityEngine.Object.FindObjectsOfType<MP3Menu>()) menu.StopAllCoroutines();
        foreach (var s in UnityEngine.Object.FindObjectsOfType<MP3PlayerClientAudioStreamer>()) { s.Cancel(); UnityEngine.Object.Destroy(s); }
        foreach (var s in UnityEngine.Object.FindObjectsOfType<MP3PlayerAudioStreamPlayer_ClientOnly>()) { s.Silence(); UnityEngine.Object.Destroy(s); }
        foreach (var s in UnityEngine.Object.FindObjectsOfType<MP3PlayerServerAudioStreamer>()) { s.StopMusic(); UnityEngine.Object.Destroy(s); }
    }
    internal static void Reset() {
        ResetWorld();
        LocalTracks.Clear();
        MP3Menu.clips = null;
        MP3Menu.dropdownList = null;
    }
}

// The persistent singleton starts scanning only after the local world/player is ready.
internal sealed class MP3MultiLibraryLifecycle : CasualtiesTogetherSingleton {
    bool active, reported, worldActive, sessionGone;
    int epoch;
    float nextRoster;
    string roster;
    void Update() {
        if (!Net.IsRunning) {
            if (!sessionGone) {
                sessionGone = true;
                epoch++; active = reported = worldActive = false;
                roster = null;
                MP3MultiLibrary.Reset();
            }
            return;
        }
        sessionGone = false;
        bool ready = MP3MultiLibrary.SyncEnabled && Util.IsWorldGenerated() && !SharedMain.LocalWorldIsGenerating && ScavPlayer.LocalPlayer != null;
        if (!ready) {
            if (worldActive) {
                epoch++; active = reported = worldActive = false; roster = null;
                MP3MultiLibrary.ResetWorld();
            }
            return;
        }
        worldActive = true;
        MP3MultiLibrary.TickSessions();
        if (!MP3MultiLibrary.Enabled) {
            if (active) { epoch++; active = reported = false; roster = null; MP3MultiLibrary.DisableClientSources(); }
            return;
        }
        if (!active) {
            active = true;
            if (MP3MultiLibrary.LocalTracks.Count == 0) {
                StartCoroutine(Scan(epoch));
            } else {
                // Metadata and clips survived the layer transition. Reuse them
                // instead of hashing/reloading the custommusic folder again.
                reported = true;
                MP3MultiLibrary.Report();
                if (Net.IsServer && MP3Menu.dropdownList == null) MP3PlayerServerAudioStreamer.Server_ForceLoadMusic();
            }
            foreach (var menu in UnityEngine.Object.FindObjectsOfType<MP3Menu>()) {
                if (menu.dropdown.enabled && menu.GetComponent<MP3MultiMenu>() == null) menu.gameObject.AddComponent<MP3MultiMenu>();
            }
        }
        if (Net.IsServer && Time.unscaledTime >= nextRoster) {
            nextRoster = Time.unscaledTime + 1;
            string current = string.Join("|", ScavPlayer.ClientIdToPlayerDict.Values.OrderBy(p => p.playerId.id).Select(p => p.playerId + ":" + p.playerName));
            if (roster != current) { roster = current; MP3MultiLibrary.Broadcast(); }
        }
        if (reported && !Net.IsServer && Time.unscaledTime >= nextRoster) {
            nextRoster = Time.unscaledTime + 5;
            if (!MP3MultiLibrary.Sources.ContainsKey(ScavPlayer.LocalPlayer.playerId)) MP3MultiLibrary.Report();
        }
    }
    void KrokoshaSingletonEvent_OnRulesUpdate() {
        if (MP3MultiLibrary.Enabled) return;
        // Rule changes revoke the old scan before a completed background hash can publish it.
        epoch++; active = reported = false; roster = null;
        if (MP3MultiLibrary.SyncEnabled) MP3MultiLibrary.DisableClientSources();
        else if (worldActive) { worldActive = false; MP3MultiLibrary.Reset(); }
    }
    IEnumerator Scan(int scanEpoch) {
        var task = MP3MultiLibrary.Scan(MP3MultiLibrary.Folder);
        while (!task.IsCompleted) yield return null;
        if (scanEpoch != epoch || !active || !MP3MultiLibrary.Enabled) yield break;
        MP3MultiLibrary.LocalTracks = task.Status == TaskStatus.RanToCompletion ? task.Result : new List<MP3Track>();
        reported = true; MP3MultiLibrary.Report();
        if (Net.IsServer && MP3Menu.dropdownList == null) MP3PlayerServerAudioStreamer.Server_ForceLoadMusic();
    }
    void KrokoshaSingletonEvent_OnSceneChange() {
        epoch++; active = reported = worldActive = false; roster = null;
        if (Net.IsRunning && MP3MultiLibrary.SyncEnabled) MP3MultiLibrary.ResetWorld();
        else MP3MultiLibrary.Reset();
    }
}

