using HarmonyLib;
using CasualtiesTogetherUtils;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Collections;
using LiteNetLib;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;






[HarmonyPatch(typeof(MP3Menu), nameof(MP3Menu.LoadAllMusic))]
public static class MP3Menu_LoadAllMusic_MultiplayerPatch {

    [HarmonyReversePatch]
    public static IEnumerator LoadAllMusic(object instance, string folderPath) {
        throw new NotImplementedException();
    }

    public static IEnumerator Patched_LoadAllMusic(MP3Menu __instance, string folderPath) {

        if (!MP3MultiLibrary.SyncEnabled || !Net.IsClient) {
            if (Net.IsServer) {
                Multiplayer.DoMultiplayerStatusMessageLog("SERVER: Loading custom music.");
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog("Loading custom music.");
            }
            yield return LoadAllMusic(__instance, folderPath);
        }

        if (MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance != null) {
            GameObject.Destroy(MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance);
            MP3PlayerServerAudioStreamer.temp_loader_mp3menu_instance = null;
        }

        if (!MP3MultiLibrary.SyncEnabled || !Net.IsClient) {
            if (MP3MultiLibrary.SyncEnabled && Net.IsServer) {
                Multiplayer.DoMultiplayerStatusMessageLog("SERVER: Sending custom music list to players.");
                MP3PlayerServerAudioStreamer.Server_SendMusicList();
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog("Finished loading custom music.");
            }
        }
    }

    public static bool Prefix(MP3Menu __instance, string folderPath, ref IEnumerator __result) {
        __result = Patched_LoadAllMusic(__instance, folderPath);
        return false;
    }
}


[HarmonyPatch(typeof(MP3Menu), nameof(MP3Menu.UpdateList))]
public static class MP3Menu_UpdateList_MultiplayerPatch {
    public static Item last_locally_used_mp3_player = null;

    public static bool Prefix(MP3Menu __instance) {
        if (!MP3MultiLibrary.SyncEnabled || Net.IsServer) return true;
        MP3Menu.dropdownList ??= new List<TMP_Dropdown.OptionData>();
        var writer = Net.CreateWriter(NetmsgId.SERVER_MP3RequestMusicList);
        writer.Put(1);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        return false;
    }
}
[HarmonyPatch(typeof(MP3Menu), nameof(MP3Menu.Play))]
public static class MP3Menu_Play_MultiplayerPatch {



    public static void Server_PlayThisSongOnThisMp3Player(AudioClip audio, Item mp3player) {
        var g = mp3player.GetOrAddComponent<MP3PlayerServerAudioStreamer>();
        g.Play(audio);
    }

    public static bool Prefix(MP3Menu __instance) {
        if (!MP3MultiLibrary.SyncEnabled) return true;
        if (MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player == null) return true;
        if (MP3MultiLibrary.Enabled) {
            var multi = __instance.GetComponent<MP3MultiMenu>();
            if (multi != null) multi.PlaySelected();
            return false;
        }
        var closingMultiMenu = __instance.GetComponent<MP3MultiMenu>();
        if (closingMultiMenu != null) { closingMultiMenu.RestoreHostNow(); return false; }
        var item = MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player;
        if (item == null) return true;
        if (Net.IsClient) {
            if (NetObjectRegistry.TryGetSyncInfoOrRegister(item, out var si)) {
                var writer = Net.CreateWriter(NetmsgId.SERVER_MP3PlayCustomMusic);
                writer.Put(__instance.dropdown.value); writer.Put(si.syncId);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            }
        } else if (NetObjectRegistry.TryGetSyncInfoOrRegister(item, out var si)) {
            MP3MultiLibrary.LegacyHostRequest((ushort)0, si.syncId, __instance.dropdown.value);
        }
        return false;
    }
}
[HarmonyPatch(typeof(MP3Menu), nameof(MP3Menu.Exit))]
public static class MP3Menu_Exit_MultiplayerPatch {
    public static bool Prefix(MP3Menu __instance) {
        if (!MP3PlayerServerAudioStreamer.unprivated_MP3Menu_loadingMusic)
            MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player = null;
        return true;
    }
}


public abstract class MP3PlayerAudioStreamPlayerBase : StreamedAudioOutput {
    protected override float PrebufferLatency => 0.3f;
    protected override bool RequireBufferedSamples => true;
    public void Silence() {
        if (audioSource != null) StopPlayback();
        vc_buffer.Clear();
        if (clip != null) clip.SetData(new float[clip.samples * clip.channels], 0);
    }
    protected void CleanupMP3Output() {
        Silence();
        if (audioSource != null) Destroy(audioSource);
        if (lowpass != null) Destroy(lowpass);
        if (clip != null) Destroy(clip);
        var decoder = Traverse.Create(this).Field("opus_decoder").GetValue<IDisposable>();
        decoder?.Dispose();
    }
    protected override void Awake() {
        max_hear_distance = 82;
        is_voice = false;
        base.Awake();
    }
}


// Host is the server: it must hear client-owned music without using the
// client-only component (that path needs the item GO and crashes in Awake).
public class MP3HostAudioMonitor : MP3PlayerAudioStreamPlayerBase {
    public Item Item;
    internal int ReceivedFrames;
    internal double MaxFrameGap;
    double lastFrame, reportAt;
    internal void NoteFrame() {
        double now = Time.realtimeSinceStartupAsDouble;
        if (lastFrame > 0) MaxFrameGap = Math.Max(MaxFrameGap, now - lastFrame);
        lastFrame = now;
        ReceivedFrames++;
    }
    void LateUpdate() {
        if (!MP3MultiLibrary.SyncEnabled || Item == null) { Silence(); Destroy(this); return; }
        transform.position = Item.transform.position;
        if (Time.realtimeSinceStartupAsDouble >= reportAt) {
            reportAt = Time.realtimeSinceStartupAsDouble + 10;
            log.l($"[mp3-host] frames={ReceivedFrames} maxGapMs={MaxFrameGap * 1000:0} bufferedMs={BufferedSamples * 1000 / Math.Max(1, sample_rate)} underruns={PlaybackUnderruns}");
        }
    }
    void OnDestroy() { CleanupMP3Output(); }
}

public class MP3PlayerAudioStreamPlayer_ClientOnly : MP3PlayerAudioStreamPlayerBase {
    void OnDestroy() { CleanupMP3Output(); }
    void LateUpdate() {
        if (!MP3MultiLibrary.SyncEnabled || !MP3MultiLibrary.Near(GetComponent<Item>(), ScavPlayer.LocalPlayer) || TimeTheBufferEnded > 3) {
            Silence();
            TimeTheBufferEnded = 20;
            Destroy(this);
        }
    }
}

// should be used only by server 
public class MP3PlayerServerAudioStreamer : MP3PlayerAudioStreamPlayerBase {


    public Item item => GetComponent<Item>();
    AudioSource musicplayersource;
    internal bool IsPlaying => musicplayersource != null && musicplayersource.isPlaying;
    internal void StopMusic() {
        if (musicplayersource != null) musicplayersource.Stop();
        queued_resampled_samples.Clear();
        lock (pcm_buffer_from_the_filter_thing) pcm_buffer_from_the_filter_thing.Clear();
        Silence();
    }


    public List<float> pcm_buffer_from_the_filter_thing = new List<float>(4096);    // should be always mono, its in sample rate AudioSettings.outputSampleRate

    ushort target_samplerate = Voicechat.STANDART_FREQUENCIES.Last();

    AudioOpusEncoding encoder;

    public void CheckRequiredProcessing(AudioClip original) {
        target_samplerate = Mathf.Min(original.frequency, Voicechat.SAMPLE_RATE) > 32000 ? Voicechat.STANDART_FREQUENCIES.Last() : Voicechat.GetClosestSamplerate((ushort)original.frequency);

    }

    public void Play(AudioClip audio) {
        musicplayersource.Stop();
        CheckRequiredProcessing(audio);

        if (audio.channels == 0) {
            musicplayersource.clip = null;
            return;
        }

        musicplayersource.clip = audio;

        queued_resampled_samples.Clear();
        lock (pcm_buffer_from_the_filter_thing) pcm_buffer_from_the_filter_thing.Clear();

        //last_timeSamples = 0;
        musicplayersource.Play();
    }

    //float last_time = 0;
    //int last_timeSamples = 0;

    Queue<float> queued_resampled_samples = new();

    void LateUpdate() {


        if (musicplayersource.isPlaying) {
            if ( SharedMain.LocalWorldIsGenerating ) {
                queued_resampled_samples.Clear();
                lock (pcm_buffer_from_the_filter_thing) pcm_buffer_from_the_filter_thing.Clear();
                return;
            }

            if (!Multiplayer.rules.EnableMP3Sync) {
                Destroy(this);  
                return;
            }


            // check if it ran out of battery 
            if (item.condition <= 0) {
                musicplayersource.Stop();
                return;
            } else {
                // maybe make it use power to play music? but like very little tho
            }




            float[] resampleddata = null;
            lock (pcm_buffer_from_the_filter_thing) {
                if (pcm_buffer_from_the_filter_thing.Count >= Voicechat.system_framesize) {
                    resampleddata = Voicechat.ResampleVoiceBlob(pcm_buffer_from_the_filter_thing, AudioSettings.outputSampleRate, target_samplerate);
                    pcm_buffer_from_the_filter_thing.Clear();
                }
            }
            if (resampleddata != null) foreach (var sample in resampleddata) queued_resampled_samples.Enqueue(sample);
            var required_frame_size = Voicechat.CalculateFrameSizeForSamplerate(target_samplerate);
            if (queued_resampled_samples.Count > required_frame_size) {
                var frame = new float[required_frame_size];
                while (queued_resampled_samples.Count > required_frame_size) {

                    for (int i = 0; i < frame.Length; i++)
                        frame[i] = queued_resampled_samples.Dequeue();


                    if (Voicechat.mp3_listener_volume > 0 ) {
                        ReceiveVoiceBlob((ushort)target_samplerate, frame, true);
                    }


                    var plrs = ScavPlayer.GetPlayersInRadius(this.transform.position, audioSource.maxDistance);
                    plrs.Remove(ScavPlayer.LocalPlayer);

                    if (plrs.Count > 0 && NetObjectRegistry.TryGetSyncInfoOrRegister(gameObject,out var si)) {





                        //SEND IT
                        var voiceblob = encoder.Encode(frame, (ushort)target_samplerate);

                        if (voiceblob.Length > 0) {

                            var freq_index = (byte)Array.IndexOf(AudioOpusEncoding.STANDART_FREQUENCIES, (ushort)target_samplerate);

                            var target_client_ids = plrs.Select(x => x.playerId).ToList();


                            var writer = Net.CreateWriter(NetmsgId.CLIENT_StreamedAudioToClients);

                            writer.Put(si.syncId);
                            writer.Put(freq_index);
                            writer.PutBytesWithLength(voiceblob);
                            writer.Put(MP3MultiLibrary.Token(si.syncId));
                            writer.Put(false);

                            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: target_client_ids);

                            // WRITER END;
                        }


                    }
                }
            }

        } 

        if (AudioListener.volume == 0 && !warned_0_vol) {
            warned_0_vol = true;
            Multiplayer.DoMultiplayerStatusMessageError("SERVER: Can't stream mp3player music with 0 volume!!!!");
        }

    }
    static bool warned_0_vol = false;


    void OnDestroy() {
        if (musicplayersource != null)
            Destroy(musicplayersource.gameObject);

        encoder?.Dispose();
        CleanupMP3Output();
    }

    protected override void Awake() {
        if (Net.IsClient) {
            Destroy(this);
#if DEBUG || RELEASETEST
            log.error("DEV: MUSIC STREAMER WAS CREATED NOT ON SERVER !!! DELETING MYSELF!!!");
#endif
            return;
        }
        encoder = new AudioOpusEncoding();

        var go = new GameObject("mp3play");
        go.AddComponent<MP3PlayerAudioReader>().owner = this;
        musicplayersource = go.AddComponent<AudioSource>();
        musicplayersource.volume = 1;
        musicplayersource.loop = false;
        musicplayersource.minDistance = 9999999f;
        musicplayersource.maxDistance = 99999999f;
        musicplayersource.rolloffMode = AudioRolloffMode.Linear;
        musicplayersource.bypassEffects = false;
        musicplayersource.bypassListenerEffects = true;
        musicplayersource.bypassReverbZones = true;
        musicplayersource.dopplerLevel = 0;
        musicplayersource.spatialBlend = 0;
        //musicplayersource.transform.position = new Vector3(99999999,999999,9999999);

        base.Awake();

    }

    public class MP3PlayerAudioReader : MonoBehaviour {
        public float reader_volume = 0;
        public MP3PlayerServerAudioStreamer owner;
        void OnAudioFilterRead(float[] data, int channels) {
            try {
                if (owner == null || owner.musicplayersource == null || !owner.musicplayersource.isPlaying || channels <= 0) return;
                lock (owner.pcm_buffer_from_the_filter_thing) {
                    if (owner.pcm_buffer_from_the_filter_thing.Count > Voicechat.system_framesize * 25) return;
                    reader_volume = Voicechat.GetFrameMaxVol(data);
                    float invVolume = 1f / (AudioListener.volume == 0 ? 1f : AudioListener.volume);
                    for (int i = 0; i + channels <= data.Length; i += channels) {
                        float sample = 0;
                        for (int channel = 0; channel < channels; channel++) sample += data[i + channel];
                        owner.pcm_buffer_from_the_filter_thing.Add(sample / channels * invVolume);
                    }
                }
            } catch (Exception) {
                // Destroy/stop may coincide with an audio callback; always silence the reader.
            } finally {
                Array.Clear(data, 0, data.Length);
            }
        }
    }
    internal static void Server_SendMusicList(IReadOnlyList<ScavPlayer> targets = null) {
        if (!MP3MultiLibrary.SyncEnabled || MP3Menu.dropdownList == null) return;
        targets ??= ServerMain.AllPlayersExceptHost.ToList();

        var target_client_ids = targets.Select(x => x.playerId).ToList();

        // i was testing shi, TODO maybe use this actually
        //var w2 = new NetDataWriter(true,64);
        //w2.PutArray(MP3Menu.dropdownList.Select(x => x.text).ToArray());

        var writer = Net.CreateWriter(NetmsgId.CLIENT_MP3MenuServerIsSendingMusicList);

        
        writer.Put((int)MP3Menu.dropdownList.Count);
        foreach (var item in MP3Menu.dropdownList) {
            writer.Put(item.text);
        }

        writer.CompressWriter();

        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: target_client_ids);

        // WRITER END;
    }

    public static GameObject temp_loader_mp3menu_instance = null;
    public static bool unprivated_MP3Menu_loadingMusic {
        get {
            return (bool)Traverse.Create(typeof(MP3Menu)).Field("loadingMusic").GetValue();
        }
        set {
            Traverse.Create(typeof(MP3Menu)).Field("loadingMusic").SetValue(value);
        }
    }

    public static void Server_ForceLoadMusic() {
        if (MP3MultiLibrary.SyncEnabled && Net.IsServer && temp_loader_mp3menu_instance == null && !unprivated_MP3Menu_loadingMusic) {
            MP3Menu.dropdownList = null; // reset it to reload the list again

            temp_loader_mp3menu_instance = Utils.Create("Special/MP3SongSelect", PlayerCamera.main?.mainCanvas?.transform ?? Plugin.s.transform);
            temp_loader_mp3menu_instance.GetComponent<MP3Menu>().dropdown.enabled = false;
            temp_loader_mp3menu_instance.transform.position = new Vector3(99999, 99999, 99999);
            temp_loader_mp3menu_instance.transform.localScale = Vector3.one * 0.001f;
            Destroy(temp_loader_mp3menu_instance, 120);
        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_MP3RequestMusicList)]
    static void Server_MP3RequestMusicList(knetid cid, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out int val);
        if (!MP3MultiLibrary.SyncEnabled) return;
        MP3MultiLibrary.Broadcast();
        if (ScavPlayer.TryGetPlayerFromClientId(cid, out var plr) ) {
            if (MP3Menu.dropdownList == null) {
                Server_ForceLoadMusic();
            } else if (!unprivated_MP3Menu_loadingMusic) {
                Server_SendMusicList(new List<ScavPlayer> { plr });
            }
        }
    }
    [ServerReceiver((byte)NetmsgId.SERVER_MP3PlayCustomMusic)]
    static void Server_MP3PlayCustomMusic(knetid cid, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out int val);
        reader.Get(out knetid mp3plr_syncid);

        MP3MultiLibrary.LegacyHostRequest(cid, mp3plr_syncid, val);
    }
    internal static bool client_host_list_ready;
    [ClientReceiver((byte)NetmsgId.CLIENT_MP3MenuServerIsSendingMusicList)]
    static void Client_MP3MenuServerIsSendingMusicList(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        if (!MP3MultiLibrary.SyncEnabled || Net.IsServer) return;
        client_host_list_ready = true;
        MP3MultiLibrary.Revision++;
        log.l("CLIENT: Received Music list.");
        reader = reader.DecompressReader();

        reader.Get(out int musiccount);
        MP3Menu.dropdownList = new List<TMP_Dropdown.OptionData>();
        for (int i = 0; i < musiccount; i++) {
            reader.Get(out string text);
            MP3Menu.dropdownList.Add(new TMP_Dropdown.OptionData(text));   
        }

        // With sharing disabled there is no extra menu component to refresh the host list.
        foreach (var menu in FindObjectsOfType<MP3Menu>()) {
            if (menu.GetComponent<MP3MultiMenu>() != null) continue;
            menu.dropdown.ClearOptions(); menu.dropdown.AddOptions(MP3Menu.dropdownList);
        }
    }


    [ClientReceiver((byte)NetmsgId.CLIENT_StreamedAudioToClients, false)]
    static void Client_StreamedAudioToClients(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        if (!MP3MultiLibrary.SyncEnabled || Voicechat.mp3_listener_volume <= 0 || SharedMain.LocalWorldIsGenerating) {
            return;
        }

        reader.Get(out knetid source_syncid);

        //var data = packet.GetSBytes(); // old decoder
        //var frame = data.Select(x => (float)((float)x / (float)sbyte.MaxValue)).ToArray();

        if (NetObjectRegistry.TryGetSyncInfo(source_syncid, out var si)) {
            if (!si.IsItem() || si.item.id != "mp3player" || !MP3MultiLibrary.Near(si.item, ScavPlayer.LocalPlayer)) return;
            var go = si.go;

            reader.Get(out byte freq_index);
            if (freq_index >= AudioOpusEncoding.STANDART_FREQUENCIES.Length) return;
            var freq = AudioOpusEncoding.STANDART_FREQUENCIES[freq_index];

            reader.Get(out byte[] data);
            uint token = reader.AvailableBytes >= 4 ? reader.GetUInt() : 0;
            bool clientSource = reader.AvailableBytes > 0 && reader.GetBool();
            if (data.Length == 0 || data.Length > 1024 || !MP3MultiLibrary.AcceptFrame(source_syncid, token, clientSource)) return;

            MP3PlayerAudioStreamPlayerBase streamoutput;
            if (Net.IsServer) {
                var monitorGo = go.transform.Find("mp3_host_monitor")?.gameObject;
                if (monitorGo == null) {
                    monitorGo = new GameObject("mp3_host_monitor");
                    monitorGo.transform.SetParent(go.transform, false);
                }
                var monitor = monitorGo.GetComponent<MP3HostAudioMonitor>();
                if (monitor == null) {
                    try { monitor = monitorGo.AddComponent<MP3HostAudioMonitor>(); }
                    catch (System.Exception e) { log.warn("[mp3] host monitor init failed: " + e); return; }
                }
                monitor.Item = si.item;
                monitor.NoteFrame();
                streamoutput = monitor;
            } else {
                streamoutput = go.GetOrAddComponent<MP3PlayerAudioStreamPlayer_ClientOnly>();
            }

            if (!streamoutput.OpusDecode(freq, data, out var frame)) {
#if DEBUG || RELEASETEST
                Plugin.log.LogWarning($" MP3PLAY: CLIENT: Failed to decode opus??? ");
#endif 
                return;
            }

            streamoutput.ReceiveVoiceBlob(freq, frame,true);

        }// else {
         //   NetObjectRegistry.Client_RequestObjectInfo(source_syncid);
         //}
    }







}



internal class MP3PlayerMPPatches : CasualtiesTogetherSingleton {

    void KrokoshaSingletonEvent_OnSceneChange() {
        MP3PlayerServerAudioStreamer.unprivated_MP3Menu_loadingMusic = false;
        MP3PlayerServerAudioStreamer.client_host_list_ready = false;
        MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player = null;
        MP3Menu.clips?.Clear();
        MP3Menu.dropdownList?.Clear();
        MP3Menu.clips = null;
        MP3Menu.dropdownList = null;
    }

}




