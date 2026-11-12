#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;

namespace Together;

// Vanilla freecam only reads the arrow keys. Add WASD on top, using the same
// speed and shift multiplier, without touching the native arrow handling.
[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.Update))]
internal static class ConsoleScript_FreecamWasdPatch {
    static void Postfix() {
        var cam = PlayerCamera.main;
        var console = ConsoleScript.instance;
        if (cam == null || console == null || !cam.isFreecam || !Application.isFocused) return;
        Vector2 move = Vector2.zero;
        if (Input.GetKey(KeyCode.W)) move.y += 1f;
        if (Input.GetKey(KeyCode.S)) move.y -= 1f;
        if (Input.GetKey(KeyCode.D)) move.x += 1f;
        if (Input.GetKey(KeyCode.A)) move.x -= 1f;
        if (move == Vector2.zero) return;
        move *= Input.GetKey(KeyCode.LeftShift) ? 60f : 10f;
        console.freecamPos += move * Time.unscaledDeltaTime;
    }
}
#endif