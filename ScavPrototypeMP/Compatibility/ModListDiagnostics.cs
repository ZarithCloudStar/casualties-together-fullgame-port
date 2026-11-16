#if FULLGAME
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Bootstrap;
using LiteNetLib.Utils;

namespace Together;

// Diagnostics for the existing GUID-set policy. This does not declare which
// plugins are server-only, compare client versions, or add a new rejection rule.
internal static class ModListDiagnostics {
    internal const string MetadataKey = "cu_mod_details";
    internal const string RejectionPrefix = "This server requires different set of mods.";
    internal const int MaxRejectionBytes = 480;
    const int MaxMetadataBytes = 2400;
    static int cachedPluginCount = -1;
    static Dictionary<string, string> localDetails;

    static string Clean(string value, int max) {
        value = Multiplayer.SanitizeRichText(value ?? "");
        value = new string(value.Where(c => !char.IsControl(c)).ToArray());
        if (value.Length <= max) return value;
        int length = max;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value.Substring(0, length) + "…";
    }

    static string[] IDs(IEnumerable<string> ids) => (ids ?? Array.Empty<string>())
        .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();

    internal static Dictionary<string, string> LocalDetails() {
        if (localDetails != null && cachedPluginCount == Chainloader.PluginInfos.Count) return localDetails;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var info in Multiplayer.GetBepinPlugins()) {
            var mod = info.Metadata;
            result[mod.GUID] = $"{Clean(mod.Name, 64)} {Clean(mod.Version.ToString(), 24)} [{Clean(mod.GUID, 160)}]";
        }
        cachedPluginCount = Chainloader.PluginInfos.Count;
        return localDetails = result;
    }

    static string Label(string id, IReadOnlyDictionary<string, string> details) =>
        details != null && details.TryGetValue(id, out var label) ? Clean(label, 256) : Clean(id, 256);

    internal static string Describe(IEnumerable<string> host, IEnumerable<string> client,
        IReadOnlyDictionary<string, string> hostDetails, IReadOnlyDictionary<string, string> clientDetails, bool enforced) {
        var hostIDs = IDs(host); var clientIDs = IDs(client);
        var missing = hostIDs.Except(clientIDs, StringComparer.Ordinal).ToArray();
        var extra = clientIDs.Except(hostIDs, StringComparer.Ordinal).ToArray();
        var text = new StringBuilder(enforced ? "房主要求双方模组列表完全一致。" : "房主未强制统一模组列表；以下安装差异不代表全部必需，也不一定是入房失败原因。");
        if (missing.Length > 0) {
            text.Append(enforced ? "\n客机缺少（房主已安装）：" : "\n你未安装的房主模组（不一定必需）：");
            foreach (var id in missing) text.Append("\n- ").Append(Label(id, hostDetails));
        }
        if (extra.Length > 0) {
            text.Append("\n客机额外安装（房主没有）：");
            foreach (var id in extra) text.Append("\n- ").Append(Label(id, clientDetails));
        }
        if (missing.Length == 0 && extra.Length == 0) text.Append("\n双方模组 GUID 列表一致。");
        return LimitUtf8(text.ToString(), 8192, "\n…列表过长，请核对双方插件清单。");
    }

    internal static string LimitUtf8(string value, int maxBytes, string suffix) {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes) return value;
        int length = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(suffix));
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        string prefix = Encoding.UTF8.GetString(bytes, 0, length);
        int newline = prefix.LastIndexOf('\n');
        return (newline >= 0 ? prefix.Substring(0, newline) : prefix) + suffix;
    }

    internal static string Rejection(IEnumerable<string> host, IEnumerable<string> client,
        IReadOnlyDictionary<string, string> hostDetails, IReadOnlyDictionary<string, string> clientDetails = null) =>
        LimitUtf8(RejectionPrefix + "\n" + Describe(host, client, hostDetails, clientDetails, true),
            MaxRejectionBytes, "\n…更多差异请查看大厅的模组详情。");

    internal static string LobbyDetails(NetPublicServerInfo lobby) => Describe(lobby.modlist, Multiplayer.GetModGUIDs(),
        lobby.modDetails, LocalDetails(), lobby.enforcesModList);

    internal static string LobbySummary(NetPublicServerInfo lobby) {
        var host = IDs(lobby.modlist); var client = IDs(Multiplayer.GetModGUIDs());
        var missing = host.Except(client, StringComparer.Ordinal).ToArray();
        var extra = client.Except(host, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            return $"缺少 {missing.Length} 个模组：{Label(missing[0], lobby.modDetails)}" +
                (extra.Length > 0 ? $"；另多装 {extra.Length} 个。" : "") + "（模组按钮可查看详情）";
        return $"客机多装 {extra.Length} 个模组，房主要求列表一致。（模组按钮可查看详情）";
    }

    internal static string WriteLobbyMetadata() {
        var writer = new NetDataWriter();
        writer.Put((byte)1); writer.Put((ushort)0);
        ushort count = 0;
        foreach (var item in LocalDetails().OrderBy(item => item.Key, StringComparer.Ordinal)) {
            if (item.Key.Length > 256) continue;
            var entry = new NetDataWriter(); entry.Put(item.Key); entry.Put(item.Value);
            if (count >= 64 || writer.Length + entry.Length > MaxMetadataBytes) break;
            writer.Put(new ReadOnlySpan<byte>(entry.Data, 0, entry.Length)); count++;
        }
        int end = writer.Length;
        writer.SetPosition(1); writer.Put(count); writer.SetPosition(end);
        return Convert.ToBase64String(writer.CopyData());
    }

    internal static Dictionary<string, string> ReadLobbyMetadata(string encoded, IEnumerable<string> host) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try {
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 3200) return result;
            var raw = Convert.FromBase64String(encoded);
            if (raw.Length > MaxMetadataBytes) return result;
            var reader = new NetDataReader(raw);
            if (reader.GetByte() != 1) return result;
            int count = reader.GetUShort();
            if (count > 64) return result;
            var expected = new HashSet<string>(IDs(host), StringComparer.Ordinal);
            for (int i = 0; i < count; i++) {
                string id = reader.GetString(), label = reader.GetString();
                if (id.Length <= 256 && label.Length <= 512 && expected.Contains(id)) result[id] = Clean(label, 256);
            }
            if (!reader.EndOfData) result.Clear();
        } catch { result.Clear(); } // optional UI metadata never prevents joining
        return result;
    }
}
#endif
