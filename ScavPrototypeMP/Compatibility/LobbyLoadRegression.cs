#if FULLGAME && TESTING
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Together;

// Two opt-in A/B scenarios, sharing the existing isolated save/preferences hooks.
internal static class LobbyLoadRegression {
    internal static bool Enabled => EndingTestPreferences.Enabled &&
        Environment.GetCommandLineArgs().Contains("--mp-lobby-load-test");
    static string Folder => SavesystemPatch.mpsavefolder;
    static string Marker(string name) => Path.Combine(Folder, name);
    static bool Queued => Environment.GetEnvironmentVariable("CT_ENDING_TEST_SCENARIO") == "load-queued";
    static int stage, expectedLayer;
    static float deadline, next, audioDue, audioEnd;
    static uint sequence;
    static knetid itemId;
    const uint Token = 100000;
    static AudioOpusEncoding encoder;
    static readonly float[] frame = new float[960];
    static bool sawChatOff, sawChatOn;
    static string hostGear;
    static void Check(bool value, string description) {
        if (!value) throw new Exception("[lobby-load-test] FAIL " + description);
        log.l("[lobby-load-test] PASS " + description);
    }
    static string Gear(Body body) => string.Join(",", body.GetAllItemsThorough().Where(i => i != null).Select(i => i.id).OrderBy(i => i));
    internal static void Tick() {
        if (!Enabled || stage == 99) return;
        try {
            if (deadline == 0) deadline = Time.realtimeSinceStartup + 160;
            if (Time.realtimeSinceStartup > deadline) throw new Exception("[lobby-load-test] FAIL timeout stage=" + stage);
            if (!Net.IsRunning) return;
            if (stage == 0 && Net.IsServer && PreRunScript.instance != null && CharacterReadyGate.Blocked) {
                var data = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(Marker("save.sv"))));
                expectedLayer = data.Value<int>("biome");
                hostGear = string.Join(",", data["items"].Select(i => i.Value<string>("id")).OrderBy(i => i));
                if (Queued) {
                    PreRunScript.instance.LoadRun();
                    Check(CharacterReadyGate.LoadQueued && !WorldMP.IsMenuLoadPending, "load queued before admission without starting a world");
                    PreRunScript.instance.StartRun();
                    Check(File.Exists(Marker("save.sv")) && CharacterReadyGate.LoadQueued, "new-game click cannot delete a queued load");
                }
                File.WriteAllText(Marker("test-confirm"), "go");
                stage = 1;
            } else if (stage == 0 && Net.IsClient && CharacterLobby.ShouldShowPanel && File.Exists(Marker("test-confirm"))) {
                Check(CharacterLobby.ConfirmPanel(), "new client confirmed through lobby");
                stage = 1;
            } else if (stage == 1 && Net.IsServer && !CharacterReadyGate.Blocked && ScavPlayer.ClientIdToPlayerDict.Count >= 2 && !Queued) {
                Check(ScavPlayer.ClientIdToPlayerDict.Values.All(p => p.body == null), "ready lobby roster has no world bodies");
                PreRunScript.instance.LoadRun();
                Check(SaveSystem.loadedRun && WorldMP.IsMenuLoadPending, "ready-client request retains load intent");
                stage = 2;
            }
            if (stage <= 2 && Util.IsWorldGenerated() && !DeferredPlayerSaveRestore.InProgress && ScavPlayer.LocalPlayer?.body != null) {
                if (Net.IsServer) {
                    Check(WorldGeneration.world.biomeDepth == expectedLayer, "saved layer retained: " + expectedLayer);
                    Check(Gear(ScavPlayer.LocalPlayer.body) == hostGear, "host inventory exactly matches save without extra items");
                    var guest = ScavPlayer.ClientIdToPlayerDict.Values.First(p => !p.IsHost);
                    Check(!PlayerCheckpoint.HasPlayerFile(guest), "new client has no old body checkpoint");
                    Con.ExecuteCommandAdminNoLog("rule EnableMP3Sync true");
                    Con.ExecuteCommandAdminNoLog("rule AllowClientMP3Sources true");
                    guest.Server_TeleportCharacter((Vector2)ScavPlayer.LocalPlayer.body.transform.position + Vector2.right * 3);
                    var go = Utils.Create("mp3player", ScavPlayer.LocalPlayer.body.transform.position, 0);
                    Check(go != null && go.GetComponent<Item>() != null, "created audio test item");
                    Check(NetObjectRegistry.TryGetSyncInfoOrRegister(go.GetComponent<Item>(), out var si), "registered audio test item");
                    itemId = si.syncId;
                    var type = typeof(MP3MultiLibrary).GetNestedType("Session", System.Reflection.BindingFlags.NonPublic);
                    var session = Activator.CreateInstance(type, true);
                    foreach (var field in new (string, object)[] { ("Item", itemId), ("Owner", guest.playerId), ("Requester", guest.playerId),
                        ("Token", Token), ("CreditTime", Time.unscaledTime), ("LastFrame", Time.unscaledTime) })
                        Traverse.Create(session).Field(field.Item1).SetValue(field.Item2);
                    ((IDictionary)AccessTools.Field(typeof(MP3MultiLibrary), "Sessions").GetValue(null))[itemId] = session;
                    AccessTools.Method(typeof(MP3MultiLibrary), "SendControl").Invoke(null, new object[] { itemId, Token, true, true });
                    File.WriteAllText(Marker("test-audio"), ((ushort)itemId).ToString());
                    Con.ExecuteCommandAdminNoLog("rule EnableChatbox false");
                }
                next = Time.unscaledTime + 3;
                stage = 3;
            }
            if (stage == 3) {
                if (!Multiplayer.rules.EnableChatbox && !sawChatOff) {
                    sawChatOff = true;
                    int before = Traverse.Create(typeof(Chat)).Field("CHAT_LOG").GetValue<MaxCapacityQueue<Chat.ChatMsgContainer>>().Count;
                    Chat.LogMessage("test", "history retained while text input is disabled");
                    Check(Traverse.Create(typeof(Chat)).Field("CHAT_LOG").GetValue<MaxCapacityQueue<Chat.ChatMsgContainer>>().Count >= before, "disabled chat retains received history");
                    Check(Chat.InputDisabled, "disabled rule blocks input on living local body");
                    Chat.ShowChat();
                }
                if (Net.IsServer && Time.unscaledTime >= next && !Multiplayer.rules.EnableChatbox)
                    Con.ExecuteCommandAdminNoLog("rule EnableChatbox true");
                if (sawChatOff && Multiplayer.rules.EnableChatbox && !sawChatOn) {
                    sawChatOn = true;
                    Check(!Chat.InputDisabled, "enabled rule restores input with history retained");
                }
                if (Net.IsClient && File.Exists(Marker("test-audio"))) {
                    if (encoder == null) {
                        itemId = ushort.Parse(File.ReadAllText(Marker("test-audio")));
                        encoder = new AudioOpusEncoding(); audioDue = Time.unscaledTime; audioEnd = audioDue + 10;
                    }
                    while (Time.unscaledTime >= audioDue && audioDue < audioEnd) {
                        for (int i = 0; i < frame.Length; i++) frame[i] = 0.05f * Mathf.Sin((sequence * 960 + i) * (2 * Mathf.PI * 440 / 48000));
                        var w = Net.CreateWriter(NetmsgId.SERVER_MP3AudioFrame);
                        w.Put(itemId); w.Put(Token); w.Put(++sequence);
                        w.Put((byte)Array.IndexOf(AudioOpusEncoding.STANDART_FREQUENCIES, (ushort)48000));
                        w.PutBytesWithLength(encoder.Encode(frame, 48000));
                        Net.Client_Send(DeliveryMethod.Unreliable, w);
                        audioDue += .02f;
                    }
                    if (Time.unscaledTime > audioEnd + 1 && sawChatOn) {
                        encoder.Dispose(); encoder = null;
                        File.WriteAllText(Marker("test-audio-sent"), sequence.ToString());
                        log.l("[lobby-load-test] complete side=client sent=" + sequence); stage = 99;
                    }
                }
                if (Net.IsServer && File.Exists(Marker("test-audio-sent"))) {
                    var monitor = UnityEngine.Object.FindObjectOfType<MP3HostAudioMonitor>();
                    int sent = int.Parse(File.ReadAllText(Marker("test-audio-sent")));
                    Check(monitor != null && monitor.ReceivedFrames >= sent * .98, "host received at least 98% of relayed frames");
                    Check(sawChatOff && sawChatOn, "both chat rule states tested");
                    log.l($"[lobby-load-test] complete side=host frames={monitor.ReceivedFrames}/{sent} underruns={monitor.PlaybackUnderruns} maxGapMs={monitor.MaxFrameGap * 1000:0}");
                    stage = 99;
                }
            }
        } catch (Exception ex) { stage = 99; encoder?.Dispose(); encoder = null; log.error("[lobby-load-test] FAIL " + ex); }
    }
}
[HarmonyPatch(typeof(Plugin), "Update")]
internal static class LobbyLoadRegressionTick { static void Postfix() => LobbyLoadRegression.Tick(); }
[HarmonyPatch(typeof(EndingFlowTests), nameof(EndingFlowTests.Tick))]
internal static class LobbyLoadRegressionSkipEnding { static bool Prefix() => !LobbyLoadRegression.Enabled; }
#endif
