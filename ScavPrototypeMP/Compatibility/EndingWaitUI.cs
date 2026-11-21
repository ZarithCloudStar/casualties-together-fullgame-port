using UnityEngine;

namespace Together;

// The ending cutscene intentionally darkens the whole screen. While a client
// waits for the host to finish reading, draw this independently of the game's
// canvases so the black frame never looks like a frozen client.
internal sealed class EndingWaitUI : CasualtiesTogetherSingleton {
    void OnGUI() {
        if (!EndingFlow.WaitingForOthers) return;
        float width = Mathf.Min(760f, Screen.width - 80f);
        float height = 118f;
        var rect = new Rect((Screen.width - width) * .5f, (Screen.height - height) * .5f, width, height);
        GUI.color = new Color(0f, 0f, 0f, .78f);
        GUI.Box(rect, GUIContent.none);
        GUI.color = Color.white;
        var style = new GUIStyle(GUI.skin.label) {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 21,
            wordWrap = true,
            normal = { textColor = Color.white },
        };
        GUI.Label(rect, "有玩家仍在阅读结局剧情，请稍候……\nA player is still reading the ending. Please wait...", style);
    }
}
