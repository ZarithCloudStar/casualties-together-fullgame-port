using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Together;

// Pure release/manifest rules, shared by the in-game checker and offline tests.
// Publication identity is separate from Plugin.MOD_VERSION (the network version).
internal static class ReleaseUpdate {
    internal const string Repository = "ZarithCloudStar/casualties-together-fullgame-port";
    internal const string ReleasePage = "https://github.com/" + Repository + "/releases/latest";
    internal const string DefaultSource = "https://api.github.com/repos/" + Repository + "/releases/latest";
    internal const string DownloadPage = "https://pan.baidu.com/s/1OAnsVoL1jW4rkk2Gxcg0uw?pwd=wzsw";
    internal const string DownloadCode = "wzsw", CommunityGroup = "130925719";
    internal const int MaxManifestChars = 1024 * 1024;

    internal sealed class Info {
        internal string version = "", build = "", url = ReleasePage, notes = "", title = "";
        internal long publishedAt;
        internal bool fromGitHub;
    }
    static readonly Regex VersionPattern = new(@"^[vV]?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-cu-port)?(?:-bugfix(\d+))?(?:-(alpha|beta|rc|preview|dev)(?:[.-]?(\d+))?)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsWebUrl(string value, bool allowLoopback = false) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Scheme == Uri.UriSchemeHttps || (allowLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    internal static string NormalizeSource(string source) {
        source = (source ?? "").Trim();
        if (source.Length == 0) return DefaultSource;
        if (!IsWebUrl(source, true)) throw new FormatException("更新源须使用 HTTPS 地址；本地演示允许回环 HTTP 地址。");
        var uri = new Uri(source);
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 4 && parts[2] == "releases" && parts[3] == "latest")
                return "https://api.github.com/repos/" + parts[0] + "/" + parts[1] + "/releases/latest";
        }
        return source;
    }
    internal static Info Parse(string text) {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("更新清单为空。");
        if (text.Length > MaxManifestChars) throw new FormatException("更新清单过大。");
        var root = JObject.Parse(text);
        if (root.Value<bool?>("draft") == true || root.Value<bool?>("prerelease") == true)
            throw new FormatException("此更新源未提供正式发布版本。");
        var info = new Info {
            fromGitHub = root["tag_name"] != null && root["version"] == null,
            version = ((string)root["version"] ?? (string)root["tag_name"] ?? "").Trim().TrimStart('v', 'V'),
            build = ((string)root["build"] ?? "").Trim(),
            title = (string)root["name"] ?? "",
            notes = (string)root["notes"] ?? (string)root["body"] ?? "",
        };
        if (!TryVersion(info.version, out _)) throw new FormatException("发布版本号无法识别，请使用 5.5.0-cu-port-bugfix2 等格式。");
        if (info.build.Length > 64 || info.build.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new FormatException("发布构建标识无效。");
        // Old release notes contain literal backslash sequences. Display them as
        // paragraphs, never interpret remote text as IMGUI rich text or commands.
        info.notes = info.notes.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\r\n", "\n");
        if (info.notes.Length > 16000) info.notes = info.notes.Substring(0, 16000) + "\n\n说明较长，请到发布页查看完整内容。";
        string link = ((string)root["download_url"] ?? (string)root["html_url"] ?? "").Trim();
        if (link.Length == 0 && root["assets"] is JArray assets) {
            foreach (var asset in assets) {
                string candidate = (string)asset["browser_download_url"];
                if (IsWebUrl(candidate)) { link = candidate; break; }
            }
        }
        if (link.Length != 0 && !IsWebUrl(link)) throw new FormatException("发布信息中的下载地址无效。");
        info.url = link.Length == 0 ? ReleasePage : link;
        var published = root["published_at"] ?? root["publishedAt"];
        if (published?.Type == JTokenType.Integer) {
            info.publishedAt = (long)published;
            try { DateTimeOffset.FromUnixTimeSeconds(info.publishedAt); }
            catch { throw new FormatException("发布时间无效。"); }
        } else if (published != null && DateTimeOffset.TryParse(published.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            info.publishedAt = when.ToUnixTimeSeconds();
        return info;
    }
    // Returns remote relative to local; different/older tags are never blindly
    // treated as updates. Build hashes have no chronological ordering.
    internal static int Compare(string remote, string local) {
        if (!TryVersion(remote, out var a) || !TryVersion(local, out var b)) throw new FormatException("无法比较本机与远端的发布版本。");
        for (int i = 0; i < a.Length; i++) { int c = a[i].CompareTo(b[i]); if (c != 0) return c; }
        return 0;
    }
    static bool TryVersion(string text, out long[] parts) {
        parts = null;
        var match = VersionPattern.Match((text ?? "").Trim());
        if (!match.Success) return false;
        var result = new long[7];
        for (int i = 1; i <= 5; i++)
            if (match.Groups[i].Success && !long.TryParse(match.Groups[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out result[i - 1])) return false;
        string channel = match.Groups[6].Value.ToLowerInvariant();
        result[5] = channel == "" ? 4 : channel == "rc" ? 3 : channel == "beta" || channel == "preview" ? 2 : channel == "alpha" ? 1 : 0;
        if (match.Groups[7].Success && !long.TryParse(match.Groups[7].Value, out result[6])) return false;
        parts = result; return true;
    }
}
