using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Together;

// Unity/Steam metadata is captured only on the main thread, never by the writer.
internal static class SessionLogGame {
    internal static void Start() {
        try {
            string protocol = "V" + Application.version + "_MPv" + BuildInfo.NETWORK_VERSION;
            string fallbackFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
                "Orsoniks", "CasualtiesUnknown", "mp_save", "logs");
            string folder = null;
            try {
                if (Path.IsPathRooted(Paths.GameRootPath)) folder = Path.Combine(Paths.GameRootPath, "logs");
            } catch { }
            SessionLog.Start(folder ?? fallbackFolder, Path.Combine(Paths.BepInExRootPath, "LogOutput.log"),
                $"local={DateTimeOffset.Now:O}\nutc={DateTime.UtcNow:O}\nUPDATE_VERSION={BuildInfo.UPDATE_VERSION}" +
                $"\nFULL_VERSION_TAG={protocol}\nbuild={KSteam.GetModBuildTag()}\ngame={Application.version}" +
                $"\nos={Environment.OSVersion} osBits={(Environment.Is64BitOperatingSystem ? 64 : 32)} processBits={(IntPtr.Size * 8)}" +
                $"\ninstall={Paths.GameRootPath}\nplayer=unavailable steamID=unavailable\nnetwork=offline (not connected)", fallbackFolder);
            Application.quitting += SessionLog.CleanExit;
            Application.logMessageReceivedThreaded += UnityLog;
        } catch { }
    }

    static void UnityLog(string message, string stack, LogType type) {
        try {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                SessionLog.Event("UNITY " + type, message + " stack=" + stack);
        } catch { }
    }

    internal static void Identity() {
        try {
            SessionLog.Event("PLAYER", $"name={UIMainMenu.USERINPUT_NAME} steamID={(KSteam.Loaded ? KSteam.GetLocalUserSteamID().m_SteamID : 0)} steamInitialized={KSteam.Loaded} network=offline");
        } catch { }
    }

    internal static void SteamConnection(string message, bool error = false) {
        try {
            SessionLog.Event("steam-conn", message);
            if (error) Plugin.log.LogError(message); else Plugin.log.LogInfo(message);
        } catch { }
    }

    internal static void Minigame(string phase, Minigame game) {
        try { if (game != null) SessionLog.Event("MINIGAME " + phase, game.GetType().Name); } catch { }
    }

    internal static void WorldSnapshot(string message, bool error = false) {
        try {
            SessionLog.Event("WORLD SNAPSHOT", message);
            if (error) Together.log.error(message); else Together.log.l(message);
        } catch { }
    }

    internal static void MedicalPanel() {
        try {
            var view = WoundView.view;
            if (view != null) SessionLog.Event("MEDICAL PANEL", $"open={view.gameObject.activeSelf} body={view.body?.name}");
        } catch { }
    }

    internal static void ObserveMedicalPanel(WoundView view) {
        try {
            if (view != null && view.GetComponent<SessionMedicalPanel>() == null)
                view.gameObject.AddComponent<SessionMedicalPanel>();
        } catch { }
    }
}

internal sealed class SessionMedicalPanel : MonoBehaviour {
    void OnEnable() => SessionLogGame.MedicalPanel();
    void OnDisable() => SessionLog.Event("MEDICAL PANEL", "open=false");
}

internal static class SessionJoinLog {
    static string method = "steam", target = "0", owner = "0", name = "", port = "0";
    static int attempts;
    static bool pending, failed, succeeded;
    static readonly Dictionary<string, int> counts = new Dictionary<string, int>();

    internal static void Begin(string via, string address) {
        try {
            method = via; target = address; owner = "0"; name = ""; port = "0";
            pending = true; failed = succeeded = false;
            string key = via + ":" + address;
            if (counts.Count >= 256 && !counts.ContainsKey(key)) counts.Clear();
            counts.TryGetValue(key, out attempts); counts[key] = ++attempts;
            SessionLog.Event("JOIN BEGIN", Details("", ""));
        } catch { }
    }

    internal static void SteamMetadata(ulong lobby) {
        try {
            if (!KSteam.Loaded) return;
            var id = new CSteamID(lobby);
            var steamOwner = SteamMatchmaking.GetLobbyOwner(id);
            if (steamOwner.m_SteamID != 0) {
                owner = steamOwner.m_SteamID.ToString();
                name = SteamFriends.GetFriendPersonaName(steamOwner);
            }
            int.TryParse(SteamMatchmaking.GetLobbyData(id, "cu_p2p_port"), out int remotePort);
            if (remotePort < 0 || remotePort > 999) remotePort = 0;
            port = remotePort.ToString();
            SessionLog.Event("LOBBY", Details("", ""));
        } catch { }
    }

    internal static void DirectMetadata(string ip, int remotePort) {
        try { owner = ip; name = ip; port = remotePort.ToString(); SessionLog.Event("DIRECT DIAL", Details("", "")); } catch { }
    }

    static string Field(string value) => "\"" + SessionLog.OneLine(value).Replace("\"", "'") + "\"";
    static string Details(string end, string debug) =>
        $"method={method} lobby={(method == "direct" ? "0" : target)} owner={owner} name={Field(name)} port={port} end={Field(end)} debug={Field(debug)} attempts={attempts}" +
        (method == "direct" ? " ip=" + Field(target) : "");

    internal static void Failure(string reason, string debug = "", bool ownerSocketEnded = false) {
        try {
            if (failed || (!pending && !ownerSocketEnded)) return;
            failed = true; pending = false;
            SessionLog.JoinFailure(Details(reason, debug), target);
        } catch { }
    }

    internal static void Success() {
        try {
            if (!pending || failed || succeeded) return;
            succeeded = true; pending = false;
            SessionLog.Event("JOIN SUCCEEDED", Details("accepted", ""));
        } catch { }
    }

    internal static void Reset() {
        try {
            if (pending && !failed) SessionLog.Event("JOIN CANCELLED", Details("network reset", ""));
            pending = false;
        } catch { }
    }
}

// Hooks observe the public lifecycle, including single-player and every minigame subclass.
[HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.StartMinigame))]
internal static class SessionMinigameStart {
    static void Prefix(Minigame minigame) => SessionLogGame.Minigame("start requested", minigame);
    static void Postfix(MinigameBase __instance, Minigame minigame) {
        try { if (__instance.currentMinigame == minigame) SessionLogGame.Minigame("started", minigame); } catch { }
    }
}

[HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.EndMinigame))]
internal static class SessionMinigameEnd {
    static void Prefix(MinigameBase __instance, out string __state) {
        __state = null;
        try {
            if (__instance.currentMinigame != null) __state = __instance.currentMinigame.GetType().Name;
            if (__state != null) SessionLog.Event("MINIGAME end requested", __state);
        } catch { }
    }
    static void Postfix(MinigameBase __instance, string __state) {
        try { if (__state != null && __instance.currentMinigame == null) SessionLog.Event("MINIGAME ended", __state); } catch { }
    }
}

[HarmonyPatch]
internal static class SessionSaveLoad {
    static IEnumerable<MethodBase> TargetMethods() {
        yield return AccessTools.Method(typeof(SaveSystem), nameof(SaveSystem.SaveGame));
        yield return AccessTools.Method(typeof(SaveSystem), nameof(SaveSystem.TryLoadGame));
    }
    [HarmonyPriority(Priority.First)]
    static void Prefix(MethodBase __originalMethod) {
        try { SessionLog.Event("SAVE/LOAD BEGIN", __originalMethod.Name); } catch { }
    }
    static void Postfix(MethodBase __originalMethod, bool __runOriginal) {
        try { SessionLog.Event("SAVE/LOAD END", $"operation={__originalMethod.Name} originalRan={__runOriginal} directory={SavesystemPatch.savedatapathreplacement}"); } catch { }
    }
    static void Finalizer(MethodBase __originalMethod, Exception __exception) {
        try { if (__exception != null) SessionLog.Event("SAVE/LOAD ERROR", __originalMethod.Name + " " + __exception); } catch { }
    }
}
