#if FULLGAME
using CasualtiesTogetherUtils;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

// Reuse the multiplayer tab's existing settings rows, layout and lifecycle.
internal sealed class LobbyCharacterSelection : MonoBehaviour {
    readonly Button[] buttons = new Button[3];
    readonly TextMeshProUGUI[] labels = new TextMeshProUGUI[3];
    readonly GameObject[] rows = new GameObject[3];
    TextMeshProUGUI heading;
    readonly TextMeshProUGUI[] chipLabels = new TextMeshProUGUI[2];
    bool lastUnchipped;
    int lastChoice = -1;
    string lastLocale;

    static readonly string[] names = { "Experiment", "Milky", "Dune" };

    internal static void Build(ref float height) {
        SettingsMenu_Start_MultiplayerPatch.CreateSettingButton(CharacterSelection.Text("character_title", "Choose your character"),
            ref height, out var header, out var headerButton, out var headerText);
        SettingsMenu_Start_MultiplayerPatch.SetSettingNameAndTooltip(header,
            CharacterSelection.Text("character_title", "Choose your character"), CharacterSelection.DeferredMessage);
        if (headerButton != null) headerButton.interactable = false;
        var panel = header.AddComponent<LobbyCharacterSelection>();
        panel.heading = headerText;
        for (int i = 0; i < 3; i++) {
            int chr = i;
            SettingsMenu_Start_MultiplayerPatch.CreateSettingButton(Name(chr), ref height, out var row, out var button, out var label);
            panel.rows[i] = row;
            panel.buttons[i] = button;
            panel.labels[i] = label;
            if (button != null) button.onClick.AddListener(() => CharacterSelection.DeferUiAction("settings choice", () => CharacterSelection.Choose(chr)));
            else log.warn($"CHARACTER: character button {chr} is unavailable; use the character command.");
        }
        for (int i = 0; i < 2; i++) {
            bool unchipped = i == 1;
            SettingsMenu_Start_MultiplayerPatch.CreateSettingButton(ChipState.Label(unchipped), ref height, out var row, out var button, out var label);
            panel.chipLabels[i] = label;
            SettingsMenu_Start_MultiplayerPatch.SetSettingNameAndTooltip(row, ChipState.Label(unchipped), CharacterSelection.Text("chip_hint", "Saved per player for this save; used by multiplayer ending rules."));
            if (button != null) button.onClick.AddListener(() => CharacterSelection.DeferUiAction("settings chip", () => ChipState.Choose(unchipped)));
        }
        panel.Refresh();
        height += 30;
    }

    static string VanillaText(string key, string fallback) {
        if (Locale.currentLang != null && Locale.currentLang.other.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) {
            var text = Locale.GetOther(key);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return fallback;
    }

    static int SafeIndex(int chr) => Math.Max(0, Math.Min(2, chr));
    internal static string Name(int chr) { chr = SafeIndex(chr); return VanillaText($"char{chr}name", names[chr]); }
    internal static string CleanName(int chr) {
        chr = SafeIndex(chr);
        string value = Name(chr).Trim();
        if (value.StartsWith("[", StringComparison.Ordinal)) {
            int end = value.IndexOf(']');
            if (end < 0) return names[chr];
            value = value.Substring(end + 1).Trim();
        }
        value = value.Trim(' ', '\t', '\r', '\n', '"', '\'', '“', '”', '‘', '’');
        // Canonical names also keep the roster consistent across native locales.
        return Array.Find(names, name => string.Equals(value, name, StringComparison.OrdinalIgnoreCase)) ?? names[chr];
    }
    internal static string Description(int chr) { chr = SafeIndex(chr); return VanillaText($"char{chr}desc",
        CharacterSelection.Text($"character_desc_{chr}", $"Play as {names[chr]}.")); }

    void Update() {
        if (lastChoice != CharacterSelection.Local || lastLocale != Locale.currentLangName || lastUnchipped != ChipState.LocalUnchipped) Refresh();
    }

    void Refresh() {
        lastChoice = CharacterSelection.Local;
        lastUnchipped = ChipState.LocalUnchipped;
        for (int i = 0; i < 2; i++) if (chipLabels[i] != null)
            chipLabels[i].text = (lastUnchipped == (i == 1) ? "[" + CharacterSelection.Text("character_selected", "Selected") + "] " : "") + ChipState.Label(i == 1);
        lastLocale = Locale.currentLangName;
        bool forced = CharacterSelection.CommandLineOverride.HasValue;
        if (heading != null) heading.text = CharacterSelection.Text("character_title", "Choose your character") +
            (forced ? " (" + CharacterSelection.Text("character_cli_locked", "Command-line override") + ")" : "");
        for (int i = 0; i < 3; i++) {
            string name = Name(i);
            SettingsMenu_Start_MultiplayerPatch.SetSettingNameAndTooltip(rows[i], name, Description(i));
            if (labels[i] != null) labels[i].text = i == lastChoice
                ? $"[{CharacterSelection.Text("character_selected", "Selected")}] {name}" : name;
            if (buttons[i] != null) {
                buttons[i].interactable = !forced;
                if (buttons[i].TryGetComponent<Image>(out var image)) image.color = i == lastChoice ? Color.green : Color.white;
            }
        }
    }
}
#endif
