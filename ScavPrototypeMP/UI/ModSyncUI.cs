#if FULLGAME
using BepInEx.Bootstrap;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Together;

// Initial "mods are out of sync" dialog and the centered mod list. The prompt
// owns the three choices; the list owns selection/download progress and only
// enables Confirm after every checked row is downloaded (or staged for restart).
internal static class ModSyncUI {
    internal static bool Open { get; private set; }
    internal static List<ModSync.Entry> HostManifest { get; private set; } = new();
    internal static List<ModSync.Entry> Entries { get; } = new();
    static Vector2 scroll;

    internal static void SetHostManifest(List<ModSync.Entry> manifest) {
        HostManifest = manifest ?? new List<ModSync.Entry>();
        Entries.Clear();
        Open = false;
    }

    internal static void OpenList() {
        if (Entries.Count == 0) BuildEntries();
        Open = true;
        scroll = Vector2.zero;
    }

    internal static void CloseList() {
        Open = false;
    }

    internal static void Reset() {
        Open = false;
        Entries.Clear();
        HostManifest.Clear();
        scroll = Vector2.zero;
        ModSync.Reset();
    }

    static void BuildEntries() {
        ModSync.Reset();
        Entries.Clear();
        foreach (var host in HostManifest) {
            var entry = host.Clone();
            entry.State = ModSync.EntryState.Idle;
            entry.Selected = false;
            if (Chainloader.PluginInfos.TryGetValue(entry.Guid, out var info)) {
                entry.Installed = true;
                entry.ClientVersion = info.Metadata.Version?.ToString() ?? "";
                entry.VersionMatches = string.Equals(entry.ClientVersion, entry.Version, StringComparison.Ordinal);
                entry.Selected = !entry.VersionMatches;
            } else {
                entry.Installed = false;
                entry.Selected = true;
            }
            Entries.Add(entry);
        }
    }

    internal static void Draw() {
        if (!Open) return;
        var panel = MenuDialogUI.Panel(820, 560, out float scale);
        float pad = Mathf.Max(14f, 26f * scale);
        var area = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, panel.height - pad * 2);
        var title = MenuDialogUI.Text(scale, 1.3f);
        var body = MenuDialogUI.Text(scale, 1f);
        var muted = MenuDialogUI.Text(scale, .9f, MenuDialogUI.Muted);

        float y = MenuDialogUI.Label(area, area.y, "同步模组", title);
        y = MenuDialogUI.Label(area, y + 6 * scale,
            "勾选要同步的模组；只在你信任房主时继续，下载的 DLL 会作为 BepInEx 插件运行。", muted);

        float footer = 58 * scale;
        var listRect = new Rect(area.x, y + 8 * scale, area.width, Mathf.Max(40, area.yMax - y - footer - 16 * scale));
        GUILayout.BeginArea(listRect);
        try {
            scroll = GUILayout.BeginScrollView(scroll);
            try {
                foreach (var entry in Entries) DrawRow(entry, body, muted);
            } finally {
                GUILayout.EndScrollView();
            }
        } finally {
            GUILayout.EndArea();
        }

        int selected = Entries.Count(entry => entry.Selected);
        int done = Entries.Count(entry => entry.Selected
            && (entry.State == ModSync.EntryState.Done || entry.State == ModSync.EntryState.Restart));
        MenuDialogUI.Label(area, area.yMax - footer - 2 * scale,
            "已选 " + selected + " 个，已完成 " + done + " 个", muted);

        bool canConfirm = selected > 0 && done == selected;
        float width = Mathf.Min(180 * scale, (area.width - 12 * scale) / 2f);
        float height = 46 * scale;
        var previous = GUI.enabled;
        GUI.enabled = canConfirm;
        try {
            if (MenuDialogUI.Button(new Rect(area.center.x - width - 6 * scale, area.yMax - height, width, height),
                    "确认", scale, canConfirm, true)) {
                CloseList();
                JoinModNotice.Close();
            }
        } finally {
            GUI.enabled = previous;
        }
        if (MenuDialogUI.Button(new Rect(area.center.x + 6 * scale, area.yMax - height, width, height),
                "取消", scale)) {
            CloseList();
        }
    }

    static void DrawRow(ModSync.Entry entry, GUIStyle body, GUIStyle muted) {
        GUILayout.BeginHorizontal();
        try {
            bool canToggle = entry.State != ModSync.EntryState.Queued
                && entry.State != ModSync.EntryState.Downloading;
            var previous = GUI.enabled;
            GUI.enabled = canToggle;
            bool next;
            try {
                next = GUILayout.Toggle(entry.Selected, "");
            } finally {
                GUI.enabled = previous;
            }
            if (next != entry.Selected) {
                if (next) ModSync.Begin(entry);
                else ModSync.Cancel(entry);
            }
            GUILayout.Label(entry.DisplayName, body, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.Label(Status(entry), muted, GUILayout.ExpandWidth(false));
        } finally {
            GUILayout.EndHorizontal();
        }
    }

    static string Status(ModSync.Entry entry) {
        switch (entry.State) {
            case ModSync.EntryState.Queued: return "等待";
            case ModSync.EntryState.Downloading:
                return "下载中 " + Mathf.RoundToInt(entry.Progress * 100f) + "%";
            case ModSync.EntryState.Done: return "已下载";
            case ModSync.EntryState.Restart: return "重启后生效";
            case ModSync.EntryState.Failed:
                return string.IsNullOrEmpty(entry.Error) ? "失败" : "失败：" + entry.Error;
            default:
                if (!entry.Installed) return "未安装";
                return entry.VersionMatches
                    ? "已安装"
                    : "已安装 " + entry.ClientVersion + "，主机 " + entry.Version;
        }
    }
}
#endif
