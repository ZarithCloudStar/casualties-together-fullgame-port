using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Together;

// Transport-independent failover. The release-page request deliberately does
// not follow redirects: GitHub's Location is the version-bearing result.
internal static class UpdateSourceProbe {
    internal sealed class Response {
        internal long status;
        internal string text, location, error;
    }
    internal sealed class Result {
        internal ReleaseUpdate.Info info;
        internal string sourceLabel;
        internal readonly List<string> failures = new();
    }
    internal static string ReleasePageFor(string source) {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 5 || parts[0] != "repos" || parts[3] != "releases" || parts[4] != "latest" ||
            !SafePart(parts[1]) || !SafePart(parts[2])) return null;
        return "https://github.com/" + parts[1] + "/" + parts[2] + "/releases/latest";
    }
    static bool SafePart(string value) {
        if (string.IsNullOrEmpty(value) || value == "." || value == "..") return false;
        foreach (char c in value) if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') &&
            !(c >= '0' && c <= '9') && c != '-' && c != '_' && c != '.') return false;
        return true;
    }
    internal static ReleaseUpdate.Info FromRedirect(string page, string location) {
        if (string.IsNullOrWhiteSpace(location) || location.Length > 2048) throw new FormatException("发布页没有提供有效的版本跳转。");
        var origin = new Uri(page);
        string[] originParts = origin.AbsolutePath.Trim('/').Split('/');
        if (origin.Scheme != "https" || origin.Host != "github.com" || !origin.IsDefaultPort ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            originParts.Length != 4 || originParts[2] != "releases" || originParts[3] != "latest" ||
            !SafePart(originParts[0]) || !SafePart(originParts[1])) throw new FormatException("备用发布页地址无效。");
        var target = new Uri(origin, location);
        if (target.Scheme != "https" || !target.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !target.IsDefaultPort || target.UserInfo.Length != 0 || target.Query.Length != 0 || target.Fragment.Length != 0)
            throw new FormatException("发布页跳转的地址不受支持。");
        string prefix = origin.AbsolutePath.Substring(0, origin.AbsolutePath.Length - "latest".Length) + "tag/";
        if (!target.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("发布页跳转不属于所检查仓库的正式发布。");
        string version = Uri.UnescapeDataString(target.AbsolutePath.Substring(prefix.Length));
        ReleaseUpdate.Compare(version, version); // Strict version syntax; rejects paths/login/HTML.
        return new ReleaseUpdate.Info {
            version = version.TrimStart('v', 'V'), fromGitHub = true, url = target.AbsoluteUri,
            notes = "已通过 GitHub 发布页确认版本。完整更新说明请点击下方“GitHub 发布页”查看。",
        };
    }
    internal static async Task<Result> CheckAsync(string source, Func<string, bool, Task<Response>> send, Action<string> progress = null) {
        var result = new Result();
        try {
            progress?.Invoke("正在检查更新源…");
            var response = await send(source, true);
            RequireSuccess(response);
            result.info = ReleaseUpdate.Parse(response.text);
            result.sourceLabel = ReleasePageFor(source) != null ? "GitHub 发布接口" : "自定义更新源";
            return result;
        } catch (Exception ex) { result.failures.Add("主源：" + ex.Message); }
        string page = ReleasePageFor(source);
        if (page == null) return result; // Never mix another repository into custom manifests.
        try {
            progress?.Invoke("正在尝试备用发布页…");
            var response = await send(page, false);
            if (response == null || (response.status != 301 && response.status != 302 && response.status != 303 && response.status != 307 && response.status != 308))
                throw new InvalidOperationException(response?.error ?? ("发布页未返回版本跳转（" + response?.status + "）。"));
            result.info = FromRedirect(page, response.location);
            result.sourceLabel = "GitHub 发布页（备用）";
        } catch (Exception ex) { result.failures.Add("备用源：" + ex.Message); }
        return result;
    }
    static void RequireSuccess(Response response) {
        if (response == null || response.status < 200 || response.status >= 300 || !string.IsNullOrEmpty(response.error))
            throw new InvalidOperationException(response?.error ?? ("HTTP " + response?.status));
    }
}
