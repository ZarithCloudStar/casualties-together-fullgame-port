#if FULLGAME
using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together;

// Informational only. The original enforce-mod-list setting still controls refusal.
// Runs after a real local ScavPlayer exists, for both Steam and direct connections.
internal static class JoinModNotice {
    const int MaxPlugins = 256;
    static bool requested;
    static string notice;
    static Vector2 scroll;
    static GameObject blocker, previousSelection;
    static readonly Dictionary<knetid, ScavPlayer> notified = new();
    internal static bool IsOpen => !string.IsNullOrEmpty(notice);

    internal static void Reset() {
        requested = false;
        notified.Clear();
        Close();
    }

    internal static void Tick() {
        if (!Net.IsClient || !Net.IsConnected || ScavPlayer.LocalPlayer == null || requested) return;
        var writer = Net.CreateWriter(PortMsgId.SERVER_RequestModDetails);
        WriteDescriptor(writer);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        requested = true;
    }

    static void WriteDescriptor(NetDataWriter writer) {
        var all = Multiplayer.GetModGUIDs().Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var ids = all.Take(MaxPlugins).ToArray();
        writer.Put(all.Length > ids.Length);
        writer.Put((ushort)ids.Length);
        foreach (string id in ids) writer.Put(id);
        writer.Put(ModListDiagnostics.WriteLobbyMetadata());
    }

    static string[] ReadDescriptor(NetDataReader reader, out Dictionary<string, string> details, out bool truncated) {
        truncated = reader.GetBool();
        int count = reader.GetUShort();
        if (count > MaxPlugins) throw new ArgumentException("Too many diagnostic plugin entries.");
        var ids = new string[count];
        for (int i = 0; i < count; i++) {
            ids[i] = reader.GetString();
            if (string.IsNullOrEmpty(ids[i]) || ids[i].Length > 256)
                throw new ArgumentException("Invalid diagnostic plugin ID.");
        }
        string metadata = reader.GetString();
        if (metadata.Length > 3200) throw new ArgumentException("Plugin metadata is too large.");
        details = ModListDiagnostics.ReadLobbyMetadata(metadata, ids);
        return ids;
    }

    [ServerReceiver((ushort)PortMsgId.SERVER_RequestModDetails)]
    static void ServerReceive(knetid clientId, NetDataReader reader) {
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out var player)) return;
        if (notified.TryGetValue(clientId, out var previous) && ReferenceEquals(previous, player)) return;
        try {
            var ids = ReadDescriptor(reader, out var details, out bool truncated);
            if (!reader.EndOfData) throw new ArgumentException("Unexpected diagnostic payload.");
            notified[clientId] = player;
            var host = Multiplayer.GetModGUIDs();
            if (truncated || !new HashSet<string>(host, StringComparer.Ordinal).SetEquals(ids)) {
                string text = ModListDiagnostics.Describe(host, ids, ModListDiagnostics.LocalDetails(), details,
                    Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST);
                if (truncated) text += "\n客机插件超过显示上限，列表未完整展示。";
                SessionLog.Event("JOIN MOD DIFFERENCE", "player=" + Multiplayer.SanitizeRichText(player.playerName) + "\n" + text);
                ConsoleScript.instance?.LogToConsole("玩家模组与房主不同：" + Multiplayer.SanitizeRichText(player.playerName) + "\n" + text);
            }
            var writer = Net.CreateWriter(PortMsgId.CLIENT_ModDetails);
            writer.Put(Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST);
            WriteDescriptor(writer);
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, player);
        } catch (Exception e) {
            log.warn("[mod-list] Could not read admission diagnostics: " + e.Message);
        }
    }

    [ClientReceiver((ushort)PortMsgId.CLIENT_ModDetails)]
    static void ClientReceive(knetid _, NetDataReader reader) {
        try {
            bool enforced = reader.GetBool();
            var host = ReadDescriptor(reader, out var hostDetails, out bool truncated);
            if (!reader.EndOfData) throw new ArgumentException("Unexpected diagnostic payload.");
            var client = Multiplayer.GetModGUIDs();
            if (!truncated && new HashSet<string>(host, StringComparer.Ordinal).SetEquals(client)) return;
            notice = ModListDiagnostics.Describe(host, client, hostDetails, ModListDiagnostics.LocalDetails(), enforced);
            notice += "\n\n新增物品或玩法可能需要双方安装对应模组；此提示不会自动识别必装关系。";
            if (truncated || client.Length > MaxPlugins) notice += "\n插件超过显示上限，请同时核对完整插件清单。";
            scroll = Vector2.zero;
            ConsoleScript.instance?.LogToConsole("模组安装差异\n" + notice);
            SessionLog.Event("JOIN MOD DIFFERENCE", notice);
            BlockCanvas();
        } catch (Exception e) {
            log.warn("[mod-list] Could not display admission diagnostics: " + e.Message);
        }
    }

    static void BlockCanvas() {
        if (blocker != null) return;
        previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        blocker = new GameObject("ModDifferenceInputBlocker", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        var canvas = blocker.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;
        var panel = new GameObject("BlockUnderlyingButtons", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(blocker.transform, false);
        var rect = (RectTransform)panel.transform;
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        panel.GetComponent<Image>().color = Color.clear;
    }

    static void Close() {
        notice = null;
        if (blocker != null) UnityEngine.Object.Destroy(blocker);
        blocker = null;
        if (previousSelection != null && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(previousSelection);
        previousSelection = null;
    }

    internal static void Draw() {
        if (!IsOpen) return;
        float width = Mathf.Min(780f, Screen.width - 24f), height = Mathf.Min(600f, Screen.height - 24f);
        var box = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        Color color = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, .7f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = color;
        GUI.Box(box, "");
        var title = new GUIStyle(GUI.skin.label) { fontSize = 24, alignment = TextAnchor.MiddleCenter };
        GUI.Label(new Rect(box.x + 12, box.y + 10, width - 24, 38), "模组安装差异", title);
        var area = new Rect(box.x + 18, box.y + 56, width - 36, height - 144);
        var style = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true, richText = false, padding = new RectOffset(0, 0, 0, 0) };
        float contentWidth = area.width - 24;
        float contentHeight = Mathf.Max(area.height, style.CalcHeight(new GUIContent(notice), contentWidth));
        scroll = GUI.BeginScrollView(area, scroll, new Rect(0, 0, contentWidth, contentHeight));
        GUI.Label(new Rect(0, 0, contentWidth, contentHeight), notice, style);
        GUI.EndScrollView();
        // This dialog is in screen pixels; do not inherit the MP menu's scaled font/padding.
        var button = new GUIStyle(GUI.skin.button) {
            fontSize = 18, fixedHeight = 0, alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(8, 8, 4, 4), wordWrap = false
        };
        if (GUI.Button(new Rect(box.x + 18, box.yMax - 68, (width - 48) / 2, 52), "知道了，继续", button)) Close();
        if (GUI.Button(new Rect(box.x + 30 + (width - 48) / 2, box.yMax - 68, (width - 48) / 2, 52), "断开连接", button)) {
            Close(); Multiplayer.ShutdownNetwork();
        }
    }
}
#endif
