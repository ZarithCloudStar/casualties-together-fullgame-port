using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using LiteNetLib;
using UnityEngine;
using UnityEngine.Networking;

namespace Together;

// Reads decoded PCM on the main thread: no audio callback races and no dependency on master volume.
public sealed class MP3PlayerClientAudioStreamer : MonoBehaviour {
    internal uint Token { get; private set; }
    knetid syncId;
    AudioClip localClip;
    AudioOpusEncoding encoder;
    MP3PlayerAudioStreamPlayer_ClientOnly localOutput;
    UnityWebRequest request;
    bool cancelled, loaded, notified;
    double position;
    float due, drainUntil;
    uint sequence;
    const ushort Rate = 48000;
    readonly float[] frame = new float[960]; // 20 ms at 48 kHz, matching the decoder frame size.
    float[] input;

    internal void Begin(knetid item, uint token, MP3Track track) {
        syncId = item; Token = token;
        StartCoroutine(Load(track));
    }
    IEnumerator Load(MP3Track track) {
        string path = Path.Combine(MP3MultiLibrary.Folder, track.Name);
        // Hash off-thread, then let Unity decode only the verified local file.
        var validation = Task.Run(() => {
            try {
                var file = new FileInfo(path);
                return MP3MultiLibrary.SafeName(track.Name) && file.Exists && file.Length == track.Size &&
                    (file.Attributes & FileAttributes.ReparsePoint) == 0 && MP3MultiLibrary.FileHash(path) == track.Hash;
            } catch (Exception) { return false; }
        });
        while (!validation.IsCompleted) yield return null;
        if (cancelled) yield break;
        if (validation.Status != TaskStatus.RanToCompletion || !validation.Result) { Fail(); yield break; }
        Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("mp3_loading"));
        request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.UNKNOWN);
        yield return request.SendWebRequest();
        if (cancelled) yield break;
        if (request.result != UnityWebRequest.Result.Success) { Fail(); yield break; }
        localClip = DownloadHandlerAudioClip.GetContent(request);
        request.Dispose(); request = null;
        if (localClip == null || localClip.channels <= 0 || localClip.frequency <= 0 || localClip.samples <= 0) { Fail(); yield break; }
        encoder = new AudioOpusEncoding();
        localOutput = gameObject.AddComponent<MP3PlayerAudioStreamPlayer_ClientOnly>();
        input = new float[((int)Math.Ceiling(frame.Length * (double)localClip.frequency / Rate) + 2) * localClip.channels];
        loaded = true; due = Time.unscaledTime;
    }
    void Update() {
        if (cancelled) { Destroy(this); return; }
        var item = GetComponent<Item>();
        if (!MP3MultiLibrary.Enabled || SharedMain.LocalWorldIsGenerating || item == null || item.condition <= 0 || !MP3MultiLibrary.Near(item, ScavPlayer.LocalPlayer)) {
            Finish(false); return;
        }
        if (!loaded) return;
        if (drainUntil > 0) {
            if (Time.unscaledTime >= drainUntil) Finish(false);
            return;
        }
        // Bound catch-up after stalls. Sending a burst of seconds of old music is not useful.
        if (Time.unscaledTime - due > 0.2f) due = Time.unscaledTime;
        int budget = 10;
        while (Time.unscaledTime >= due && budget-- > 0) {
            if (position >= localClip.samples) { drainUntil = Time.unscaledTime + 0.75f; return; }
            try {
                int offset = (int)position;
                if (!localClip.GetData(input, offset)) { Fail(); return; }
                double ratio = (double)localClip.frequency / Rate;
                for (int i = 0; i < frame.Length; i++) {
                    double at = position + i * ratio;
                    if (at >= localClip.samples) { frame[i] = 0; continue; }
                    double relative = at - offset;
                    int index = (int)relative;
                    float alpha = (float)(relative - index), a = 0, b = 0;
                    for (int c = 0; c < localClip.channels; c++) {
                        a += input[index * localClip.channels + c];
                        b += at + 1 < localClip.samples ? input[(index + 1) * localClip.channels + c] : 0;
                    }
                    frame[i] = Mathf.Lerp(a, b, alpha) / localClip.channels;
                }
                position += frame.Length * ratio;
                byte[] data = encoder.Encode(frame, Rate);
                if (data.Length == 0) { Fail(); return; }
                if (localOutput != null && Voicechat.mp3_listener_volume > 0) localOutput.ReceiveVoiceBlob(Rate, frame, true);
                var w = Net.CreateWriter(NetmsgId.SERVER_MP3AudioFrame);
                w.Put(syncId); w.Put(Token); w.Put(++sequence);
                w.Put((byte)Array.IndexOf(AudioOpusEncoding.STANDART_FREQUENCIES, Rate));
                w.PutBytesWithLength(data);
                Net.Client_Send(DeliveryMethod.Unreliable, w);
                due += 0.02f;
            } catch (Exception e) { log.warn("MP3 local playback failed: " + e.Message); Fail(); return; }
        }
    }
    void Fail() {
        Multiplayer.DoMultiplayerStatusMessageError(Lang.Get("mp3_load_failed"));
        Finish(true);
    }
    void Finish(bool failed) {
        if (!notified) { notified = true; MP3MultiLibrary.EndLocal(syncId, Token, failed); }
        Cancel(); Destroy(this);
    }
    internal void Cancel() {
        if (cancelled) return;
        cancelled = true;
        StopAllCoroutines();
        if (request != null) { request.Abort(); request.Dispose(); request = null; }
        if (localOutput != null) { localOutput.Silence(); Destroy(localOutput); localOutput = null; }
        if (localClip != null) { Destroy(localClip); localClip = null; }
        encoder?.Dispose(); encoder = null;
    }
    void OnDestroy() { Cancel(); }
}
