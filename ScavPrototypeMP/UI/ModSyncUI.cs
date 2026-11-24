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
    internal static bool DisclaimerOpen { get; private set; }
    internal static List<ModSync.Entry> HostManifest { get; private set; } = new();
    internal static List<ModSync.Entry> Entries { get; } = new();
    static Vector2 scroll;

    internal static string T(string key, string fallback) => Lang.Get(key, true) ?? fallback;

    internal static void SetHostManifest(List<ModSync.Entry> manifest) {
        HostManifest = manifest ?? new List<ModSync.Entry>();
        Entries.Clear();
        Open = false;
        DisclaimerOpen = false;
    }

    internal static void OpenDisclaimer() {
        if (Entries.Count == 0) BuildEntries();
        DisclaimerOpen = true;
        Open = false;
    }

    internal static void OpenList() {
        if (Entries.Count == 0) BuildEntries();
        DisclaimerOpen = false;
        Open = true;
        scroll = Vector2.zero;
    }

    internal static void CloseList() {
        Open = false;
    }

    internal static void Reset() {
        Open = false;
        DisclaimerOpen = false;
        Entries.Clear();
        HostManifest.Clear();
        scroll = Vector2.zero;
        ModSync.Reset();
    }

    internal static void DrawDisclaimer() {
        if (!DisclaimerOpen) return;
        var panel = MenuDialogUI.Panel(700, 380, out float scale);
        float pad = Mathf.Max(14f, 26f * scale);
        var area = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, panel.height - pad * 2);
        var title = MenuDialogUI.Text(scale, 1.3f);
        var body = MenuDialogUI.Text(scale, 1f);
        float y = MenuDialogUI.Label(area, area.y, T("mod_sync_disclaimer_title", "同步风险提示"), title);
        MenuDialogUI.Label(area, y + 12 * scale, T("mod_sync_disclaimer_text",
            "同步的模组来自房主，不保证安全。请只在你信任房主时继续，并谨慎勾选要下载的插件。下载的 DLL 会作为 BepInEx 插件运行。"), body);

        var labels = new[] {
            T("mod_sync_continue", "继续同步"),
            T("mod_sync_back", "返回"),
        };
        var style = new GUIStyle(GUI.skin.button) {
            fontSize = Mathf.RoundToInt(18 * scale), alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(8, 8, 4, 4), wordWrap = false
        };
        var rects = JoinModNotice.LayoutButtons(area, area.yMax - 46 * scale, 46 * scale, style, labels);
        if (GUI.Button(rects[0], labels[0], style)) {
            DisclaimerOpen = false;
            OpenList();
        }
        if (GUI.Button(rects[1], labels[1], style)) DisclaimerOpen = false;
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
            } else {
                entry.Installed = false;
            }
            // Never preselect: the player decides which host plugins to trust.
            entry.Selected = false;
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

        float y = MenuDialogUI.Label(area, area.y, T("mod_sync_list_title", "同步模组"), title);
        y = MenuDialogUI.Label(area, y + 6 * scale, T("mod_sync_list_hint",
            "勾选要同步的模组；只在你信任房主时继续，下载的 DLL 会作为 BepInEx 插件运行。"), muted);

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
        string selectedText = selected == 0
            ? T("mod_sync_no_selection", "未勾选任何模组")
            : string.Format(T("mod_sync_selected", "已选 {0} 个，已完成 {1} 个"), selected, done);
        MenuDialogUI.Label(area, area.yMax - footer - 2 * scale, selectedText, muted);

        // No selection is also a valid Confirm (just continue); if anything is
        // selected, every checked row must be downloaded or staged.
        bool canConfirm = selected == 0 || done == selected;
        var labels = new[] {
            T("mod_sync_confirm_list", "确认"),
            T("mod_sync_cancel", "取消"),
        };
        var style = new GUIStyle(GUI.skin.button) {
            fontSize = Mathf.RoundToInt(18 * scale), alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(8, 8, 4, 4), wordWrap = false
        };
        var rects = JoinModNotice.LayoutButtons(area, area.yMax - 46 * scale, 46 * scale, style, labels);
        var previous = GUI.enabled;
        GUI.enabled = canConfirm;
        try {
            if (GUI.Button(rects[0], labels[0], style)) {
                CloseList();
                JoinModNotice.Close();
            }
        } finally {
            GUI.enabled = previous;
        }
        if (GUI.Button(rects[1], labels[1], style)) CloseList();
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
            case ModSync.EntryState.Queued:
                return T("mod_sync_status_queued", "等待");
            case ModSync.EntryState.Downloading:
                return string.Format(T("mod_sync_status_downloading", "下载中 {0}%"),
                    Mathf.RoundToInt(entry.Progress * 100f));
            case ModSync.EntryState.Done:
                return T("mod_sync_status_done", "已下载");
            case ModSync.EntryState.Restart:
                return T("mod_sync_status_restart", "重启后生效");
            case ModSync.EntryState.Failed:
                return string.IsNullOrEmpty(entry.Error)
                    ? T("mod_sync_status_failed", "失败")
                    : string.Format(T("mod_sync_status_failed_reason", "失败：{0}"), entry.Error);
            default:
                if (!entry.Installed) return T("mod_sync_status_missing", "未安装");
                return entry.VersionMatches
                    ? T("mod_sync_status_installed", "已安装")
                    : string.Format(T("mod_sync_status_version", "已安装 {0}，主机 {1}"),
                        entry.ClientVersion, entry.Version);
        }
    }
}
#endif
