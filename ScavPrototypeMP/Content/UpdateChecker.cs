using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using CasualtiesTogetherUtils;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Info = Together.ReleaseUpdate.Info;

namespace Together;

/// <summary>
/// 版本更新检查 + 多人菜单标题栏的"有新版本推送"按钮。
///
/// 默认读取本修改版的 GitHub Releases。插件目录下 `update_url.txt` 可覆盖更新源：
///   - 自建静态清单：https://your.host/latest.json
///   - GitHub Releases API：https://api.github.com/repos/OWNER/REPO/releases/latest
/// 请求失败一律静默（只把按钮标成"检查更新失败，点击重试"），绝不影响启动和联机。
/// </summary>
internal static class UpdateChecker {
    internal enum State { Unknown, Checking, UpToDate, HasUpdate, Failed, NotConfigured, Cached }

    internal static State CurrentState { get; private set; } = State.Unknown;
    internal static Info Latest { get; private set; }
    internal static string LastError { get; private set; } = "";
    internal static bool PopupOpen { get; private set; }

    static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    static DateTime lastCheckUtc = DateTime.MinValue;
    static string cachedUrl;
    static bool copiedNotice;
    static Vector2 notesScroll;
    static GameObject popupBlocker, previousSelection;

    internal static string LocalVersion => BuildInfo.UPDATE_VERSION;
    internal static string LocalBuild => KSteam.GetModBuildTag();

    internal static void OpenPopup() {
        EnsureChecked();
        copiedNotice = false;
        notesScroll = Vector2.zero;
        PopupOpen = true;
        if (!UIMainMenu.IsOpen()) UIMainMenu.SetOpen(true);
        if (popupBlocker == null) {
            previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            popupBlocker = new GameObject("UpdateDialogInputBlocker", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            var canvas = popupBlocker.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = short.MaxValue;
            var panel = new GameObject("更新窗口输入遮挡", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(popupBlocker.transform, false);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            panel.GetComponent<Image>().color = Color.clear;
        }
    }

    internal static void ClosePopup() {
        PopupOpen = false;
        if (popupBlocker != null) UnityEngine.Object.Destroy(popupBlocker);
        popupBlocker = null;
        if (EventSystem.current != null && previousSelection != null && previousSelection.activeInHierarchy)
            EventSystem.current.SetSelectedGameObject(previousSelection);
        previousSelection = null;
    }

    /// <summary>玩家打开多人菜单时调用；首次/缓存过期才会真正发请求。</summary>
    internal static void EnsureChecked(bool force = false) {
        if (CurrentState == State.Checking) return;
        if (!force) {
            var duration = CurrentState == State.Failed || UsingCachedResult ? TimeSpan.FromMinutes(1) : CacheDuration;
            if (CurrentState != State.Unknown && DateTime.UtcNow - lastCheckUtc < duration) return;
        }
        try {
            if (force) cachedUrl = null;
            var url = ResolveUrl();
            PrepareCachedResult(url);
            CurrentState = State.Checking;
            progressText = "检查更新中…";
            LastError = "";
            Util.StartCoroutine(Fetch(url));
        } catch (Exception e) {
            Fail("无法启动更新检查：" + e.Message);
        }
    }

    static string ResolveUrl() {
        if (cachedUrl != null) return cachedUrl;
        cachedUrl = "";
        try {
            var asm = Assembly.GetExecutingAssembly();
            var dir = asm != null && !string.IsNullOrEmpty(asm.Location) ? Path.GetDirectoryName(asm.Location) : null;
            if (!string.IsNullOrEmpty(dir)) {
                var file = Path.Combine(dir, "update_url.txt");
                if (File.Exists(file)) {
                    foreach (var raw in File.ReadAllLines(file)) {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        cachedUrl = line;
                        break;
                    }
                }
            }
        } catch (Exception e) {
            log.warn("UPDATE: could not read update_url.txt: " + e.Message);
        }
        try { cachedUrl = ReleaseUpdate.NormalizeSource(cachedUrl); }
        catch { cachedUrl = null; throw; }
        return cachedUrl;
    }

    static string CachePath => Path.Combine(Path.GetDirectoryName(typeof(UpdateChecker).Assembly.Location), "update_cache.json");
    static string cacheScope, sourceLabel = "", progressText = "检查更新中…";
    static DateTimeOffset lastSuccess;
    internal static bool UsingCachedResult { get; private set; }

    static void PrepareCachedResult(string source) {
        if (cacheScope == source) return;
        cacheScope = source;
        Latest = null; UsingCachedResult = false; lastSuccess = default; sourceLabel = "";
        try {
            var saved = UpdateResultCache.Read(CachePath, source, DateTimeOffset.UtcNow);
            if (saved == null) return;
            Latest = saved.info; lastSuccess = saved.checkedAt; sourceLabel = saved.sourceLabel;
            UsingCachedResult = true;
            log.l($"UPDATE: loaded source-specific cache from {lastSuccess:O}");
        } catch (Exception ex) { log.warn("UPDATE: ignored invalid/unreadable cache: " + ex.Message); }
    }
    static IEnumerator Fetch(string url) {
        var check = UpdateSourceProbe.CheckAsync(url, StartRequest, text => progressText = text);
        while (!check.IsCompleted) yield return null;
        if (check.IsFaulted || check.IsCanceled) {
            Fail("更新检查暂时不可用：" + check.Exception?.GetBaseException().Message);
            yield break;
        }
        var result = check.Result;
        foreach (string failure in result.failures) log.warn("UPDATE: " + failure);
        if (result.info == null) { Fail((result.failures.Count > 1 ? "两个更新入口均暂时无法访问。" : "更新入口暂时无法访问。") + string.Join("；", result.failures)); yield break; }
        try {
            Latest = result.info;
            sourceLabel = result.sourceLabel;
            CurrentState = ReleaseUpdate.Compare(Latest.version, LocalVersion) > 0 ? State.HasUpdate : State.UpToDate;
            UsingCachedResult = false;
            LastError = ""; copiedNotice = false;
            lastCheckUtc = DateTime.UtcNow; lastSuccess = DateTimeOffset.UtcNow;
            log.l($"UPDATE: success via={sourceLabel} source={url} remote={Latest.version} local={LocalVersion} => {CurrentState}");
        } catch (Exception ex) { Fail("发布版本无法比较：" + ex.Message); yield break; }
        try { UpdateResultCache.Write(CachePath, url, Latest, sourceLabel, lastSuccess); }
        catch (Exception ex) { log.warn("UPDATE: live check succeeded, cache write skipped: " + ex.Message); }
    }
    static Task<UpdateSourceProbe.Response> StartRequest(string url, bool followRedirects) {
        var completion = new TaskCompletionSource<UpdateSourceProbe.Response>();
        try { Util.StartCoroutine(SendRequest(url, followRedirects, completion)); }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }
    static IEnumerator SendRequest(string url, bool followRedirects, TaskCompletionSource<UpdateSourceProbe.Response> completion) {
        UnityWebRequest request = null;
        UnityWebRequestAsyncOperation operation;
        try {
            request = UnityWebRequest.Get(url);
            request.timeout = 8;
            request.redirectLimit = followRedirects ? 3 : 0;
            request.SetRequestHeader("User-Agent", "CasualtiesTogetherUpdater/" + LocalVersion);
            request.SetRequestHeader("Accept", followRedirects ? "application/vnd.github+json, application/json" : "text/html");
            operation = request.SendWebRequest();
        } catch (Exception ex) {
            request?.Dispose(); completion.TrySetException(ex); yield break;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try {
            while (!operation.isDone) {
                if (watch.Elapsed.TotalSeconds > 10 || request.downloadedBytes > ReleaseUpdate.MaxManifestChars * 4UL) {
                    request.Abort();
                    completion.TrySetException(new IOException("更新请求超时或响应过大。"));
                    yield break;
                }
                yield return null;
            }
            try {
                if (request.downloadedBytes > ReleaseUpdate.MaxManifestChars * 4UL) throw new IOException("更新响应过大。");
                completion.TrySetResult(new UpdateSourceProbe.Response {
                    status = request.responseCode,
                    text = followRedirects ? request.downloadHandler?.text : null,
                    location = request.GetResponseHeader("Location"),
                    error = request.result == UnityWebRequest.Result.Success ? null : request.error,
                });
            } catch (Exception ex) { completion.TrySetException(ex); }
        } finally {
            request.Dispose();
            completion.TrySetException(new OperationCanceledException("更新请求已取消。"));
        }
    }
    static void Fail(string message) {
        LastError = message ?? "";
        lastCheckUtc = DateTime.UtcNow;
        if (Latest != null && DateTimeOffset.UtcNow - lastSuccess <= TimeSpan.FromDays(7)) {
            UsingCachedResult = true;
            CurrentState = ReleaseUpdate.Compare(Latest.version, LocalVersion) > 0 ? State.HasUpdate : State.Cached;
        } else { Latest = null; UsingCachedResult = false; lastSuccess = default; sourceLabel = ""; CurrentState = State.Failed; }
        log.warn("UPDATE: check unavailable; cached=" + UsingCachedResult + ": " + LastError);
    }
    internal static string StateText() {
        switch (CurrentState) {
            case State.Checking: return progressText;
            case State.HasUpdate: return UsingCachedResult ? "有新版本（缓存）" : "有新版本推送";
            case State.UpToDate: return "当前已是最新版本";
            case State.Failed: return "暂时无法检查";
            case State.Cached: return "离线信息 · 查看";
            case State.NotConfigured: return "未配置更新源";
            default: return "检查更新";
        }
    }

    internal static bool Clickable => CurrentState == State.HasUpdate || CurrentState == State.Failed || CurrentState == State.Cached;

    /// <summary>模态弹窗。调用方在菜单打开且 PopupOpen 时只画它。</summary>
    internal static void DrawPopup() {
        bool previous = GUI.enabled;
        GUI.enabled = true;
        try {
            Rect panel = MenuDialogUI.Panel(980, 720, out float scale);
            float pad = Mathf.Max(12, 22 * scale), gap = Mathf.Max(6, 10 * scale);
            Rect area = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, panel.height - pad * 2);
            var body = MenuDialogUI.Text(scale);
            var muted = MenuDialogUI.Text(scale, .85f, MenuDialogUI.Muted);
            var title = MenuDialogUI.Text(scale, 1.35f);
            float y = MenuDialogUI.Label(area, area.y, CurrentState == State.HasUpdate ? "有新版本可更新" : "版本与联机信息", title);
            y = MenuDialogUI.Label(area, y + gap, StateText(), MenuDialogUI.Text(scale, 1, CurrentState == State.HasUpdate ? MenuDialogUI.Accent : MenuDialogUI.Muted));
            var info = Latest;
            float buttonHeight = Mathf.Max(32, body.fontSize * 1.9f);
            float footerHeight = buttonHeight * 2 + gap * 2 + MenuDialogUI.TextHeight("QQ群：130925719    网盘提取码：wzsw", muted, area.width);
            float footerY = area.yMax - footerHeight;
            Rect notes = new Rect(area.x, y + gap, area.width, Mathf.Max(20, footerY - y - gap * 2));
            MenuDialogUI.Box(notes, new Color(.045f, .05f, .065f), new Color(.27f, .29f, .32f));
            GUILayout.BeginArea(new Rect(notes.x + gap, notes.y + gap, notes.width - gap * 2, Mathf.Max(1, notes.height - gap * 2)));
            try {
                notesScroll = GUILayout.BeginScrollView(notesScroll);
                try {
                    GUILayout.Label($"当前发布：{LocalVersion}", body);
                    GUILayout.Label($"本机构建：{LocalBuild}", muted);
                    if (lastSuccess != default) {
                        GUILayout.Label((UsingCachedResult ? "上次成功检查：" : "检查时间：") + lastSuccess.LocalDateTime.ToString("yyyy-MM-dd HH:mm") + "\n信息来源：" + sourceLabel, muted);
                        if (UsingCachedResult) GUILayout.Label("以下为缓存信息，尚未确认本次最新状态。", muted);
                    }
                    if (info != null) {
                        GUILayout.Space(gap);
                        GUILayout.Label($"远端发布：{info.version}", body);
                        if (info.publishedAt > 0) {
                            string date = DateTimeOffset.FromUnixTimeSeconds(info.publishedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                            GUILayout.Label("发布时间：" + date + (info.build.Length == 0 ? "" : "\n发布构建：" + info.build), muted);
                        }
                    }
                    GUILayout.Space(gap * 1.5f);
                    if (CurrentState == State.Failed || UsingCachedResult) GUILayout.Label(LastError, body);
                    GUILayout.Label(info == null ? "暂未获取发布说明。可重新检查，或直接访问发布页与网盘。" :
                        (UsingCachedResult ? "上次获取的发布说明：\n" : "发布说明：\n") +
                        (string.IsNullOrWhiteSpace(info.notes) ? "发布方未填写说明。" : info.notes), body);
                    GUILayout.Space(gap);
                    GUILayout.Label("下载后先关闭游戏，再覆盖安装。联机双方请使用相同安装包。", muted);
                } finally { GUILayout.EndScrollView(); }
            } finally { GUILayout.EndArea(); }
            float column = (area.width - gap * 2) / 3;
            if (MenuDialogUI.Button(new Rect(area.x, footerY, column, buttonHeight), "GitHub 发布页", scale, true, CurrentState == State.HasUpdate))
                OpenLink(ReleaseUpdate.ReleasePage);
            if (MenuDialogUI.Button(new Rect(area.x + column + gap, footerY, column, buttonHeight), "百度网盘下载", scale)) OpenLink(ReleaseUpdate.DownloadPage);
            if (MenuDialogUI.Button(new Rect(area.x + (column + gap) * 2, footerY, column, buttonHeight), "复制网盘链接", scale)) Copy(ReleaseUpdate.DownloadPage);
            float row2 = footerY + buttonHeight + gap;
            if (MenuDialogUI.Button(new Rect(area.x, row2, column, buttonHeight), CurrentState == State.Checking ? "正在检查…" : "重新检查", scale, CurrentState != State.Checking)) EnsureChecked(true);
            if (MenuDialogUI.Button(new Rect(area.x + column + gap, row2, column, buttonHeight), "复制联机群号", scale)) Copy(ReleaseUpdate.CommunityGroup);
            if (MenuDialogUI.Button(new Rect(area.x + (column + gap) * 2, row2, column, buttonHeight), "关闭", scale)) ClosePopup();
            MenuDialogUI.Label(area, row2 + buttonHeight + gap, copiedNotice ? "已复制到剪贴板。" : "QQ群：" + ReleaseUpdate.CommunityGroup + "    网盘提取码：" + ReleaseUpdate.DownloadCode, muted);
        } catch (Exception e) {
            log.error("UPDATE: popup draw failed: " + e);
            ClosePopup();
        } finally { GUI.enabled = previous; }
    }
    static void OpenLink(string url) {
        if (!ReleaseUpdate.IsWebUrl(url)) { LastError = "链接无效。"; return; }
        try { Application.OpenURL(url); }
        catch (Exception e) { LastError = "无法打开链接：" + e.Message; }
    }
    static void Copy(string text) {
        try { GUIUtility.systemCopyBuffer = text; copiedNotice = true; }
        catch (Exception e) { LastError = "无法复制：" + e.Message; }
    }
}
