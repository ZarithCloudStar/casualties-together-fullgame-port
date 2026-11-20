using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

// Version marker for multiplayer skin archives. Missing markers remain the
// original 1.0 format so existing slots and cached packages continue to load.
internal static class MpSkinProtocol
{
    internal const string MarkerEntry = "protocol.json";
    internal const string VersionField = "skinProtocol";
    internal const string Current = "1.1";
    internal const string Legacy = "1.0";

    private const int SupportedMajor = 1;
    private const int FirstMinorWithLeft = 1;
    private const int MaxMarkerBytes = 256;

    internal static string MarkerJson => "{\"" + VersionField + "\":\"" + Current + "\"}";

    internal static bool TryReadVersion(JObject root, out string version, out bool supportsLeft)
    {
        version = Legacy;
        supportsLeft = false;
        if (root == null)
            return false;

        JToken token = root[VersionField];
        if (token == null || token.Type == JTokenType.Null)
            return true;
        if (token.Type != JTokenType.String)
            return false;

        version = token.Value<string>().Trim();
        return TryParse(version, out supportsLeft);
    }

    internal static bool TryParse(string text, out bool supportsLeft)
    {
        supportsLeft = false;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Trim().Split('.');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            major != SupportedMajor)
            return false;

        supportsLeft = minor >= FirstMinorWithLeft;
        return true;
    }

    internal static bool TryReadMarker(System.IO.Compression.ZipArchive zip, out string version, out bool supportsLeft)
    {
        version = Legacy;
        supportsLeft = false;
        if (zip == null)
            return false;

        System.IO.Compression.ZipArchiveEntry marker = zip.GetEntry(MarkerEntry);
        if (marker == null)
            return true;
        if (marker.Length <= 0 || marker.Length > MaxMarkerBytes)
            return false;

        using (System.IO.Stream stream = marker.Open())
        using (System.IO.StreamReader reader = new System.IO.StreamReader(stream))
        {
            string text = reader.ReadToEnd();
            try
            {
                return TryReadVersion(JObject.Parse(text), out version, out supportsLeft);
            }
            catch
            {
                return false;
            }
        }
    }
}
