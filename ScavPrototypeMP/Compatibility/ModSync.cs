#if FULLGAME
using BepInEx;
using BepInEx.Bootstrap;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Together;

// Host -> client plugin sync. The host publishes its BepInEx/plugins DLLs as a
// manifest; the client selects rows in ModSyncUI and downloads the missing or
// different ones. Files are size/SHA256 checked and either loaded now or
// staged as .dll.new for the companion preloader patcher.
internal static class ModSync {
    internal enum EntryState { Idle, Queued, Downloading, Done, Restart, Failed }

    internal sealed class Entry {
        internal string Guid;
        internal string Name;
        internal string Version;
        internal string RelativePath;
        internal long Size;

        internal bool Selected;
        internal EntryState State;
        internal float Progress;
        internal string Error;
        internal bool Installed;
        internal string ClientVersion;
        internal bool VersionMatches;

        internal string DisplayName {
            get {
                var name = string.IsNullOrWhiteSpace(Name) ? Guid : Name;
                return string.IsNullOrWhiteSpace(Version) ? name : name + " " + Version;
            }
        }

        internal Entry Clone() => new() {
            Guid = Guid,
            Name = Name,
            Version = Version,
            RelativePath = RelativePath,
            Size = Size,
        };
    }

    const int ChunkSize = 8 * 1024;
    const long MaxFileBytes = 64L * 1024 * 1024;
    const long MaxSessionBytes = 256L * 1024 * 1024;
    const double DownloadTimeout = 60.0;

    static readonly Queue<Entry> queue = new();
    static Entry active;
    static byte[] buffer;
    static int received;
    static string activeSha;
    static string activeRelative;
    static double downloadStarted;
    static long sessionBytes;
    static float manifestCacheTime = -100;
    static List<Entry> manifestCache;

    internal static bool Busy => active != null || queue.Count > 0;
    internal static Entry Active => active;

    internal static void Reset() {
        queue.Clear();
        active = null;
        buffer = null;
        received = 0;
        activeSha = null;
        activeRelative = null;
        sessionBytes = 0;
    }

    internal static void InvalidateManifest() {
        manifestCache = null;
        manifestCacheTime = -100;
    }

    internal static List<Entry> BuildManifest() {
        var now = Time.realtimeSinceStartup;
        if (manifestCache != null && now - manifestCacheTime < 1f) return manifestCache;
        var result = new List<Entry>();
        try {
            var root = PluginRoot();
            foreach (var info in Multiplayer.GetBepinPlugins()) {
                if (info?.Metadata == null || string.IsNullOrEmpty(info.Location)) continue;
                string full;
                try { full = Path.GetFullPath(info.Location); } catch { continue; }
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (!full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                string relative = full.Substring(root.Length);
                long size;
                try { size = new FileInfo(full).Length; } catch { continue; }
                if (size <= 0 || size > MaxFileBytes) continue;
                result.Add(new Entry {
                    Guid = info.Metadata.GUID,
                    Name = info.Metadata.Name ?? "",
                    Version = info.Metadata.Version?.ToString() ?? "",
                    RelativePath = relative,
                    Size = size,
                });
            }
        } catch (Exception e) {
            log.warn("[mod-sync] manifest build failed: " + e.Message);
        }
        result = result
            .GroupBy(entry => entry.Guid, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Guid, StringComparer.Ordinal)
            .ToList();
        manifestCache = result;
        manifestCacheTime = now;
        return result;
    }

    internal static string EncodeManifest(IEnumerable<Entry> entries) {
        var builder = new StringBuilder();
        foreach (var entry in entries ?? Enumerable.Empty<Entry>()) {
            if (entry == null || string.IsNullOrEmpty(entry.Guid)) continue;
            builder.Append(Clean(entry.Guid)).Append('\t')
                .Append(Clean(entry.Name)).Append('\t')
                .Append(Clean(entry.Version)).Append('\t')
                .Append(Clean(entry.RelativePath)).Append('\t')
                .Append(entry.Size).Append('\n');
        }
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    internal static List<Entry> DecodeManifest(string encoded) {
        var result = new List<Entry>();
        try {
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 256 * 1024) return result;
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            foreach (var raw in text.Split('\n')) {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 5) continue;
                if (!long.TryParse(parts[4], out long size) || size <= 0 || size > MaxFileBytes) continue;
                if (string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[3])) continue;
                result.Add(new Entry {
                    Guid = parts[0],
                    Name = parts[1],
                    Version = parts[2],
                    RelativePath = parts[3],
                    Size = size,
                });
            }
        } catch (Exception e) {
            log.warn("[mod-sync] manifest decode failed: " + e.Message);
            result.Clear();
        }
        return result;
    }

    internal static bool HasDifferences(IEnumerable<Entry> hostManifest) {
        foreach (var entry in hostManifest ?? Enumerable.Empty<Entry>()) {
            if (entry == null || string.IsNullOrEmpty(entry.Guid)) continue;
            if (!Chainloader.PluginInfos.TryGetValue(entry.Guid, out var info)) return true;
            string clientVersion = info.Metadata?.Version?.ToString() ?? "";
            if (!string.Equals(clientVersion, entry.Version, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    internal static string DescribeVersionDifferences(IEnumerable<Entry> hostManifest) {
        var lines = new List<string>();
        foreach (var entry in hostManifest ?? Enumerable.Empty<Entry>()) {
            if (entry == null || string.IsNullOrEmpty(entry.Guid)) continue;
            if (!Chainloader.PluginInfos.TryGetValue(entry.Guid, out var info)) continue;
            string clientVersion = info.Metadata?.Version?.ToString() ?? "";
            if (string.Equals(clientVersion, entry.Version, StringComparison.Ordinal)) continue;
            lines.Add("- " + (string.IsNullOrWhiteSpace(entry.Name) ? entry.Guid : entry.Name)
                + "：本机 " + clientVersion + "，主机 " + entry.Version);
        }
        return lines.Count == 0 ? "" : "版本不同：\n" + string.Join("\n", lines);
    }

    internal static void Begin(Entry entry) {
        if (entry == null || string.IsNullOrEmpty(entry.Guid)) return;
        if (entry.State == EntryState.Queued || entry.State == EntryState.Downloading
            || entry.State == EntryState.Done || entry.State == EntryState.Restart) return;
        entry.Selected = true;
        entry.State = EntryState.Queued;
        entry.Progress = 0f;
        entry.Error = null;
        queue.Enqueue(entry);
        Pump();
    }

    internal static void Cancel(Entry entry) {
        if (entry == null) return;
        entry.Selected = false;
        if (entry.State == EntryState.Queued) {
            entry.State = EntryState.Idle;
            entry.Progress = 0f;
        }
    }

    internal static void Tick() {
        if (active != null && active.State == EntryState.Downloading
            && Time.realtimeSinceStartupAsDouble - downloadStarted > DownloadTimeout) {
            Fail("下载超时。");
        }
    }

    static void Pump() {
        if (active != null) return;
        while (queue.Count > 0) {
            var next = queue.Dequeue();
            if (!next.Selected || next.State != EntryState.Queued) continue;
            active = next;
            next.State = EntryState.Downloading;
            next.Progress = 0f;
            next.Error = null;
            buffer = null;
            received = 0;
            activeSha = null;
            activeRelative = null;
            downloadStarted = Time.realtimeSinceStartupAsDouble;
            try {
                var writer = Net.CreateWriter(PortMsgId.SERVER_ModSyncRequest);
                writer.Put(next.Guid);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                log.l("[mod-sync] requested " + next.DisplayName);
            } catch (Exception e) {
                Fail("请求失败：" + e.Message);
            }
            return;
        }
    }

    [ServerReceiver((ushort)PortMsgId.SERVER_ModSyncRequest)]
    static void ServerReceiveRequest(knetid clientId, NetDataReader reader) {
        ScavPlayer player = null;
        try {
            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out player)) return;
            string guid = reader.GetString();
            var entry = BuildManifest().FirstOrDefault(item => string.Equals(item.Guid, guid, StringComparison.Ordinal));
            if (entry == null) { SendError(player, "主机没有该模组。"); return; }
            if (!TryResolveHostPath(entry.RelativePath, out var full)) { SendError(player, "主机文件路径无效。"); return; }
            var info = new FileInfo(full);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes) { SendError(player, "主机文件大小无效。"); return; }

            var bytes = File.ReadAllBytes(full);
            var sha = Sha256(bytes);
            var start = Net.CreateWriter(PortMsgId.CLIENT_ModSyncStart);
            start.Put(entry.Guid);
            start.Put(bytes.Length);
            start.Put(sha);
            start.Put(entry.RelativePath);
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, start, player);

            for (int offset = 0; offset < bytes.Length; offset += ChunkSize) {
                int length = Math.Min(ChunkSize, bytes.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(bytes, offset, chunk, 0, length);
                var writer = Net.CreateWriter(PortMsgId.CLIENT_ModSyncChunk);
                writer.Put(entry.Guid);
                writer.Put(offset);
                writer.PutBytesWithLength(chunk);
                Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, player);
            }
            log.l("[mod-sync] sent " + entry.DisplayName + " (" + bytes.Length + " bytes) to " + player.playerName);
        } catch (Exception e) {
            log.warn("[mod-sync] host send failed: " + e.Message);
            if (player != null) SendError(player, "主机读取文件失败。");
        }
    }

    [ClientReceiver((ushort)PortMsgId.CLIENT_ModSyncStart)]
    static void ClientReceiveStart(knetid _, NetDataReader reader) {
        if (active == null || active.State != EntryState.Downloading) return;
        try {
            string guid = reader.GetString();
            int size = reader.GetInt();
            string sha = reader.GetString();
            string relative = reader.GetString();
            if (!string.Equals(guid, active.Guid, StringComparison.Ordinal)
                || size <= 0 || size > MaxFileBytes) {
                Fail("主机返回的文件信息无效。");
                return;
            }
            if (!string.Equals(relative, active.RelativePath, StringComparison.Ordinal)) {
                Fail("主机返回的路径与清单不一致。");
                return;
            }
            activeSha = sha;
            activeRelative = relative;
            buffer = new byte[size];
            received = 0;
            active.Progress = 0f;
        } catch (Exception e) {
            Fail("读取下载信息失败：" + e.Message);
        }
    }

    [ClientReceiver((ushort)PortMsgId.CLIENT_ModSyncChunk)]
    static void ClientReceiveChunk(knetid _, NetDataReader reader) {
        if (active == null || active.State != EntryState.Downloading || buffer == null) return;
        try {
            string guid = reader.GetString();
            int offset = reader.GetInt();
            var chunk = reader.GetBytesWithLength();
            if (!string.Equals(guid, active.Guid, StringComparison.Ordinal)) return;
            if (offset < 0 || chunk == null || offset + chunk.Length > buffer.Length) {
                Fail("下载分块超出范围。");
                return;
            }
            Buffer.BlockCopy(chunk, 0, buffer, offset, chunk.Length);
            received = Math.Max(received, offset + chunk.Length);
            active.Progress = buffer.Length == 0 ? 0f : received / (float)buffer.Length;
            if (received >= buffer.Length) Complete();
        } catch (Exception e) {
            Fail("读取下载分块失败：" + e.Message);
        }
    }

    [ClientReceiver((ushort)PortMsgId.CLIENT_ModSyncError)]
    static void ClientReceiveError(knetid _, NetDataReader reader) {
        if (active == null) return;
        try { Fail(reader.GetString()); } catch { Fail("主机同步失败。"); }
    }

    static void Complete() {
        if (active == null || buffer == null) { Fail("下载内容为空。"); return; }
        var entry = active;
        try {
            if (buffer.Length != entry.Size) throw new InvalidOperationException("文件大小不一致。");
            var sha = Sha256(buffer);
            if (!string.Equals(sha, activeSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("文件哈希不一致。");
            if (!TryResolveClientPath(entry, activeRelative, out var target))
                throw new InvalidOperationException("目标路径无效。");

            sessionBytes += buffer.Length;
            if (sessionBytes > MaxSessionBytes) throw new InvalidOperationException("本次同步总大小超限。");

            bool loaded = Chainloader.PluginInfos.ContainsKey(entry.Guid);
            if (!loaded) {
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, buffer);
                    if (TryLoadAll(target, out var loadError)) {
                        entry.State = EntryState.Done;
                        entry.Progress = 1f;
                        log.l("[mod-sync] installed and loaded " + entry.DisplayName);
                        FinishActive();
                        return;
                    }
                    log.warn("[mod-sync] saved but load failed: " + loadError);
                } catch (Exception e) {
                    log.warn("[mod-sync] direct install failed: " + e.Message);
                }
            }

            // The target is loaded/locked or could not be loaded. Stage the
            // verified bytes for the preloader patcher; do not block joining.
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var newPath = target + ".new";
            File.WriteAllBytes(newPath, buffer);
            File.WriteAllText(newPath + ".sha256", sha);
            entry.State = EntryState.Restart;
            entry.Progress = 1f;
            log.l("[mod-sync] staged " + entry.DisplayName + " for restart");
            FinishActive();
        } catch (Exception e) {
            Fail("安装失败：" + e.Message);
        }
    }

    static void FinishActive() {
        active = null;
        buffer = null;
        received = 0;
        activeSha = null;
        activeRelative = null;
        Pump();
    }

    static void Fail(string message) {
        if (active != null) {
            active.State = EntryState.Failed;
            active.Error = message;
            active.Progress = 0f;
            log.warn("[mod-sync] " + message);
        }
        active = null;
        buffer = null;
        received = 0;
        activeSha = null;
        activeRelative = null;
        Pump();
    }

    static bool TryLoadAll(string path, out string error) {
        error = "";
        try {
            var bytes = File.ReadAllBytes(path);
            var assembly = Assembly.Load(bytes);
            bool added = false;
            foreach (var type in assembly.GetTypes()) {
                foreach (BepInPlugin attribute in type.GetCustomAttributes(typeof(BepInPlugin), false).Cast<BepInPlugin>()) {
                    if (string.IsNullOrEmpty(attribute.GUID) || Chainloader.PluginInfos.ContainsKey(attribute.GUID)) continue;
                    if (!(Chainloader.ManagerObject.AddComponent(type) is BaseUnityPlugin instance)) continue;
                    var info = instance.Info;
                    try {
                        typeof(PluginInfo).GetProperty("Location", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?.SetValue(info, path);
                    } catch { }
                    Chainloader.PluginInfos[attribute.GUID] = info;
                    added = true;
                }
            }
            if (!added) error = "程序集内没有可加载的新 BepInPlugin。";
            return added;
        } catch (Exception e) {
            error = e.Message;
            return false;
        }
    }

    static bool TryResolveHostPath(string relative, out string full) {
        full = null;
        try {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains("..")) return false;
            var root = PluginRoot();
            full = Path.GetFullPath(Path.Combine(Paths.PluginPath, relative));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && File.Exists(full);
        } catch { return false; }
    }

    static bool TryResolveClientPath(Entry entry, string relative, out string target) {
        target = null;
        try {
            if (Chainloader.PluginInfos.TryGetValue(entry.Guid, out var info)
                && !string.IsNullOrEmpty(info.Location)) {
                target = Path.GetFullPath(info.Location);
                return target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            }
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains("..")) return false;
            var root = PluginRoot();
            target = Path.GetFullPath(Path.Combine(Paths.PluginPath, relative));
            return target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    static string PluginRoot() {
        return Path.GetFullPath(Paths.PluginPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    static void SendError(ScavPlayer player, string message) {
        try {
            var writer = Net.CreateWriter(PortMsgId.CLIENT_ModSyncError);
            writer.Put(message ?? "同步失败。");
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, player);
        } catch { }
    }

    static string Sha256(byte[] bytes) =>
        BitConverter.ToString(SHA256.Create().ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

    static string Clean(string value) =>
        (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
#endif
