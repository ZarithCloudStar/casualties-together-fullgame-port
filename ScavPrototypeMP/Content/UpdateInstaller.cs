using CasualtiesTogetherUtils;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Networking;

namespace Together;

// Downloads a complete release package and stages it for the BepInEx patcher.
// The patcher applies it on the next launch, before CasualtiesMP.dll is loaded;
// a running game cannot safely overwrite its own plugin assembly.
internal static class UpdateInstaller {
    internal enum State { Idle, Downloading, Staging, Ready, Failed }

    internal static State Current { get; private set; } = State.Idle;
    internal static string Status { get; private set; } = "";
    internal static float Progress { get; private set; }
    internal static bool Busy => Current == State.Downloading || Current == State.Staging;

    static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "TogetherAutoUpdater");
    static readonly string StageRoot = Path.Combine(TempRoot, "MULTIPLAYER_STAGED");
    static readonly string ZipPath = Path.Combine(TempRoot, "package.zip");

    internal static bool CanInstall(ReleaseUpdate.Info info) =>
        info != null && !string.IsNullOrEmpty(info.assetUrl) && info.assetSize > 0;

    internal static void Restart() {
        if (Current != State.Ready) return;
        try {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string dir = Path.GetDirectoryName(exe);
            string steamUrl = "steam://rungameid/4576490";
            string script =
                "Wait-Process -Id " + Process.GetCurrentProcess().Id + " -Timeout 90 -ErrorAction SilentlyContinue; " +
                "Start-Sleep -Seconds 2; " +
                "try { Start-Process '" + steamUrl + "' } catch { Start-Process -FilePath '" + exe.Replace("'", "''") + "' -WorkingDirectory '" + dir.Replace("'", "''") + "' }";
            var info = new ProcessStartInfo {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(info);
            if (Net.IsRunning) MultiplayerQuit.Request();
            else Application.Quit();
        } catch (Exception ex) {
            Status = "无法启动重启器：" + ex.Message;
            Current = State.Failed;
        }
    }

    internal static void Begin(ReleaseUpdate.Info info) {
        if (Busy || Current == State.Ready) return;
        if (!CanInstall(info)) {
            Current = State.Failed;
            Status = "此发布没有可用的完整包。";
            return;
        }
        Current = State.Downloading;
        Progress = 0;
        Status = "准备下载…";
        Util.StartCoroutine(Run(DownloadCore(info)));
    }

    static IEnumerator Run(IEnumerator work) {
        while (true) {
            object current;
            try { if (!work.MoveNext()) yield break; current = work.Current; }
            catch (Exception ex) { Fail(ex); yield break; }
            yield return current;
        }
    }

    static void Fail(Exception ex) {
        Current = State.Failed;
        Status = "更新失败：" + ex.Message;
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true); } catch { }
    }

    static IEnumerator DownloadCore(ReleaseUpdate.Info info) {
        if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true);
        Directory.CreateDirectory(TempRoot);

        using (var request = UnityWebRequest.Get(info.assetUrl)) {
            request.timeout = 180;
            request.downloadHandler = new DownloadHandlerBuffer();
            var operation = request.SendWebRequest();
            while (!operation.isDone) {
                Progress = request.downloadProgress;
                Status = "下载中 " + Mathf.RoundToInt(Progress * 100f) + "%";
                yield return null;
            }
            if (request.result != UnityWebRequest.Result.Success)
                throw new IOException("下载失败：" + request.error);
            byte[] data = request.downloadHandler.data;
            if (data == null || data.Length < 1024 * 1024)
                throw new IOException("下载内容不完整。");
            if (info.assetSize > 0 && data.Length != info.assetSize)
                throw new IOException("下载大小与 Release 不一致。");
            File.WriteAllBytes(ZipPath, data);
        }

        Current = State.Staging;
        Progress = 0;
        Status = "校验并解压…";
        if (Directory.Exists(StageRoot)) Directory.Delete(StageRoot, true);
        using (var archive = ZipFile.OpenRead(ZipPath)) {
            if (archive.Entries.Count < 10) throw new IOException("更新包内容不完整。");
            bool hasPlugin = false, hasPatcher = false;
            foreach (var entry in archive.Entries) {
                if (entry.FullName == "BepInEx/plugins/Together/CasualtiesMP.dll") hasPlugin = true;
                if (entry.FullName == "BepInEx/patchers/TogetherAutoUpdaterPatcher.dll") hasPatcher = true;
            }
            if (!hasPlugin) throw new IOException("更新包缺少 CasualtiesMP.dll。");
            if (!hasPatcher) throw new IOException("更新包缺少自动更新 Patcher。");
        }
        Directory.CreateDirectory(StageRoot);
        ZipFile.ExtractToDirectory(ZipPath, StageRoot, true);
        if (!File.Exists(Path.Combine(StageRoot, "BepInEx", "plugins", "Together", "CasualtiesMP.dll")))
            throw new IOException("解压后的更新包不完整。");
        try { File.Delete(ZipPath); } catch { }

        Current = State.Ready;
        Progress = 1;
        Status = "更新已下载；重启游戏后自动安装。";
    }
}
