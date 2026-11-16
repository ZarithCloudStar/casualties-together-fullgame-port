using System;

// Keep the skin menu usable in English while following the game's Chinese locale.
internal static class SkinLocale {
    internal static string T(string english, string chinese) {
        string locale = Locale.currentLangName ?? "";
        bool useChinese = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
            locale.Equals("CN", StringComparison.OrdinalIgnoreCase) ||
            locale.Equals("TW", StringComparison.OrdinalIgnoreCase) ||
            locale.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0 ||
            locale.Contains("中文");
        return useChinese ? chinese : english;
    }
}
