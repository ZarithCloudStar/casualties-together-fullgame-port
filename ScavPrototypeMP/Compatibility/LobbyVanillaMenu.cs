#if FULLGAME
using CasualtiesTogetherUtils;
using UnityEngine;

namespace Together;

// The room owns "start" and "continue" now. Hide the vanilla run-settings
// Next/Resume entries while a lobby is active so neither the host nor a client
// can bypass the character/ready gate. Single-player keeps the vanilla menu.
internal static class LobbyVanillaMenu {
    static GameObject next;
    static GameObject resume;
    static bool nextWasActive;
    static bool resumeWasActive;
    static bool hidden;
    static PreRunScript observed;

    internal static void Tick() {
        if (!Util.GameIsFull()) return;
        if (!(Net.IsRunning && !Util.IsInWorld())) {
            Restore();
            return;
        }

        var menu = PreRunScript.instance;
        if (menu == null || menu.runSettingsScreen == null) return;
        if (!ReferenceEquals(menu, observed) || (next == null && resume == null)) {
            if (observed != null) Restore();
            observed = menu;
            Transform presets = menu.runSettingsScreen.transform.Find("Presets");
            Transform nextTransform = presets != null ? presets.Find("Start") : null;
            Transform resumeTransform = presets != null ? presets.Find("Load") : null;
            next = nextTransform != null ? nextTransform.gameObject : null;
            resume = resumeTransform != null ? resumeTransform.gameObject : null;
            if (next == null && resume == null) return;
            nextWasActive = next != null && next.activeSelf;
            resumeWasActive = resume != null && resume.activeSelf;
            hidden = true;
            log.l($"LOBBY: hiding vanilla run-settings options next={next != null} resume={resume != null}");
        }

        // The stock character page temporarily hides/restores the whole
        // Presets group. Keep the two lobby-unsafe entries off every frame.
        if (next != null && next.activeSelf) next.SetActive(false);
        if (resume != null && resume.activeSelf) resume.SetActive(false);
    }

    internal static void Restore() {
        observed = null;
        if (!hidden) {
            next = resume = null;
            return;
        }
        if (next != null) next.SetActive(nextWasActive);
        if (resume != null) resume.SetActive(resumeWasActive);
        next = resume = null;
        hidden = false;
        log.l("LOBBY: restored vanilla run-settings options");
    }
}
#endif
