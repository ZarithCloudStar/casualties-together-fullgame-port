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

// Admission-time mod-list exchange. The companion build replaces the old
// informational-only dialog with a syncable list: the host publishes its
// BepInEx/plugins manifest, the client can ignore, confirm (open the checklist),
// or cancel (disconnect). The checklist itself lives in ModSyncUI.
internal static class JoinModNotice {
    const int MaxPlugins = 256;
    static bool requested;
    static string notice;
    static Vector2 scroll;
    static GameObject blocker, previousSelection;
    static readonly Dictionary<knetid, ScavPlayer> notified = new();
    internal static bool IsOpen => !string.IsNullOrEmpty(notice);

    internal static string T(string key, string fallback) => Lang.Get(key, true) ?? fallback;

    internal static void Reset() {
        requested = false;
        notified.Clear();
        Close();
    }

    internal static void Tick() {
        ModSync.Tick();
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
        writer.Put(ModSync.EncodeManifest(ModSync.BuildManifest()));
    }

    static string[] ReadDescriptor(NetDataReader reader, out Dictionary<string, string> details,
        out bool truncated, out List<ModSync.Entry> manifest) {
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
        string encodedManifest = reader.GetString();
        if (encodedManifest.Length > 256 * 1024) throw new ArgumentException("Mod sync manifest is too large.");
        manifest = ModSync.DecodeManifest(encodedManifest);
        return ids;
    }

    [ServerReceiver((ushort)PortMsgId.SERVER_RequestModDetails)]
    static void ServerReceive(knetid clientId, NetDataReader reader) {
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out var player)) return;
        if (notified.TryGetValue(clientId, out var previous) && ReferenceEquals(previous, player)) return;
        try {
            var ids = ReadDescriptor(reader, out var details, out bool truncated, out _);
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
            var host = ReadDescriptor(reader, out var hostDetails, out bool truncated, out var manifest);
            if (!reader.EndOfData) throw new ArgumentException("Unexpected diagnostic payload.");
            ModSyncUI.SetHostManifest(manifest);

            var client = Multiplayer.GetModGUIDs();
            bool versionDifference = ModSync.HasDifferences(manifest);
            if (!truncated && new HashSet<string>(host, StringComparer.Ordinal).SetEquals(client) && !versionDifference) return;

            notice = ModListDiagnostics.Describe(host, client, hostDetails, ModListDiagnostics.LocalDetails(), enforced);
            var versionText = ModSync.DescribeVersionDifferences(manifest);
            if (!string.IsNullOrEmpty(versionText)) notice += "\n\n" + versionText;
            notice += "\n\n" + T("mod_sync_hint",
                "可以从主机同步缺失或版本不同的插件；带资源或依赖的模组可能需要手动安装。\n选择「无视并继续」不会改变本机文件。");
            if (truncated || client.Length > MaxPlugins)
                notice += "\n插件超过显示上限，请同时核对完整插件清单。";
            scroll = Vector2.zero;
            ConsoleScript.instance?.LogToConsole("模组不同步\n" + notice);
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

    internal static void Close() {
        notice = null;
        ModSyncUI.Reset();
        if (blocker != null) UnityEngine.Object.Destroy(blocker);
        blocker = null;
        if (previousSelection != null && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(previousSelection);
        previousSelection = null;
    }

    internal static void Draw() {
        if (!IsOpen) return;
        if (ModSyncUI.DisclaimerOpen) {
            ModSyncUI.DrawDisclaimer();
            return;
        }
        if (ModSyncUI.Open) {
            ModSyncUI.Draw();
            return;
        }

        float width = Mathf.Min(820f, Screen.width - 24f), height = Mathf.Min(620f, Screen.height - 24f);
        var box = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        Color color = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, .7f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = color;
        GUI.Box(box, "");
        var title = new GUIStyle(GUI.skin.label) { fontSize = 24, alignment = TextAnchor.MiddleCenter };
        GUI.Label(new Rect(box.x + 12, box.y + 10, width - 24, 38),
            T("mod_sync_title", "模组不同步"), title);
        var area = new Rect(box.x + 18, box.y + 56, width - 36, height - 156);
        var style = new GUIStyle(GUI.skin.label) {
            fontSize = 18, wordWrap = true, richText = false,
            padding = new RectOffset(0, 0, 0, 0)
        };
        float contentWidth = area.width - 24;
        float contentHeight = Mathf.Max(area.height, style.CalcHeight(new GUIContent(notice), contentWidth));
        scroll = GUI.BeginScrollView(area, scroll, new Rect(0, 0, contentWidth, contentHeight));
        GUI.Label(new Rect(0, 0, contentWidth, contentHeight), notice, style);
        GUI.EndScrollView();

        var button = new GUIStyle(GUI.skin.button) {
            fontSize = 18, fixedHeight = 0, alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(8, 8, 4, 4), wordWrap = false
        };
        var labels = new[] {
            T("mod_sync_ignore", "无视并继续"),
            T("mod_sync_confirm", "同步主机模组"),
            T("mod_sync_disconnect", "断开连接"),
        };
        var rects = LayoutButtons(box, box.yMax - 68f, 52f, button, labels);
        if (GUI.Button(rects[0], labels[0], button)) Close();
        if (GUI.Button(rects[1], labels[1], button)) ModSyncUI.OpenDisclaimer();
        if (GUI.Button(rects[2], labels[2], button)) {
            Close();
            Multiplayer.ShutdownNetwork();
        }
    }

    // Width follows the actual text length; the row is centered and shrinks
    // proportionally if the three labels cannot fit.
    internal static Rect[] LayoutButtons(Rect box, float y, float height, GUIStyle style, string[] labels) {
        const float gap = 10f;
        float available = Mathf.Max(1f, box.width - 36f);
        var widths = new float[labels.Length];
        float total = 0f;
        for (int i = 0; i < labels.Length; i++) {
            widths[i] = Mathf.Max(96f, style.CalcSize(new GUIContent(labels[i])).x + 30f);
            total += widths[i];
        }
        total += gap * (labels.Length - 1);
        if (total > available) {
            float fixedGaps = gap * (labels.Length - 1);
            float factor = Mathf.Max(0.5f, (available - fixedGaps) / Mathf.Max(1f, total - fixedGaps));
            total = fixedGaps;
            for (int i = 0; i < widths.Length; i++) {
                widths[i] *= factor;
                total += widths[i];
            }
        }
        var rects = new Rect[labels.Length];
        float x = box.center.x - total * 0.5f;
        for (int i = 0; i < labels.Length; i++) {
            rects[i] = new Rect(x, y, widths[i], height);
            x += widths[i] + gap;
        }
        return rects;
    }
}
#endif
