using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

namespace Together;

// Only successful responses are persisted, keyed by the exact normalized source.
// This prevents a local demo's bugfix99 from contaminating the production source.
internal static class UpdateResultCache {
    internal sealed class Entry {
        internal ReleaseUpdate.Info info;
        internal DateTimeOffset checkedAt;
        internal string sourceLabel;
    }
    internal static Entry Read(string file, string source, DateTimeOffset now) {
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > ReleaseUpdate.MaxManifestChars * 4L) throw new InvalidDataException("更新缓存过大。");
        var root = JObject.Parse(File.ReadAllText(file));
        if (root.Value<int?>("schema") != 1 || root.Value<string>("source") != source) return null;
        var when = DateTimeOffset.FromUnixTimeSeconds(root.Value<long>("checkedAtUnix"));
        if (when > now.AddMinutes(5) || now - when > TimeSpan.FromDays(7)) return null;
        if (!(root["manifest"] is JObject manifest)) throw new InvalidDataException("更新缓存缺少发布信息。");
        return new Entry { info = ReleaseUpdate.Parse(manifest.ToString()), checkedAt = when,
            sourceLabel = root.Value<string>("sourceLabel") == "GitHub 发布页（备用）" ? "GitHub 发布页（备用）" : "更新源" };
    }
    internal static void Write(string file, string source, ReleaseUpdate.Info info, string sourceLabel, DateTimeOffset now) {
        var manifest = new JObject { ["version"] = info.version, ["build"] = info.build, ["download_url"] = info.url,
            ["notes"] = info.notes, ["published_at"] = info.publishedAt };
        ReleaseUpdate.Parse(manifest.ToString());
        var root = new JObject { ["schema"] = 1, ["source"] = source, ["checkedAtUnix"] = now.ToUnixTimeSeconds(),
            ["sourceLabel"] = sourceLabel, ["manifest"] = manifest };
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try {
            byte[] bytes = Encoding.UTF8.GetBytes(root.ToString());
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            if (File.Exists(file)) File.Replace(temp, file, null, true);
            else File.Move(temp, file);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
