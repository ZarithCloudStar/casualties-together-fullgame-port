using CasualtiesTogetherUtils;
using UnityEngine;

namespace Together;


public class Spectator : CasualtiesTogetherSingletonWorldOnly {

    static Body targetBody;

    internal static Body ResolveBody(Component component) {
        if (component == null) return null;
        if (component is Body body) return body;
        if (component is NetBody netBody) return netBody.body;
        if (Util.TryGetBodyFromObject(component, out body)) return body;
        return component.GetComponentInParent<Body>() ?? component.GetComponentInChildren<Body>();
    }

    public static void SetCameraFollow(Transform target) {
        var previousTarget = targetBody;
        targetBody = ResolveBody(target);
        var cam = PlayerCamera.main;
        if (cam == null) return;
        var body = targetBody != null ? targetBody : ScavPlayer.LocalPlayer?.body;
        if (body == null) return;
        cam.body = body;
        // Normal OnGUI repeatedly clears an already-idle spectator camera. Its
        // medical panel can be inspecting a peer independently of camera.body.
        // Bind health only on an actual spectator selection or return from one.
        if (WoundView.view != null && (targetBody != null || previousTarget != null)) {
            if (Net.IsRunning && WoundView.view.gameObject.activeSelf && WoundView.view.body != body)
                log.l($"[woundview] spectator target change active={UIInGame.SpectatorModeActive} previous={PlayerCamera_ToggleWoundView_MultiplayerPatch.DescribeBody(WoundView.view.body)} next={PlayerCamera_ToggleWoundView_MultiplayerPatch.DescribeBody(body)}");
            WoundView.view.body = body;
        }
        if (MoodleManager.main != null) MoodleManager.main.body = body;
        if (targetBody != null && UIInGame.SpectatorModeActive && !Con._DEV_FREECAM)
            FollowCamera(cam);
    }

    public static Transform GetCameraFollow() => targetBody != null ? targetBody.transform : null;

    internal static void ClearLocalInput(PlayerCamera cam) {
        // Only clear owned input. Remote input belongs to the network snapshot.
        var owned = ScavPlayer.LocalPlayer?.body;
        if (owned != null) { owned.moveDir = Vector2.zero; owned.crouching = false; }
        cam.wantsJumpInput = false;
        cam.timeHeldThrow = 0;
        cam.radialOpen = false;
    }

    // Rebind while the removed Body is still a valid Unity object. Waiting for
    // LateUpdate leaves vanilla Update/GUI readers pointing at a destroyed body.
    internal static void OnBodyRemoved(Body removed) {
        var cam = PlayerCamera.main;
        if (!Net.IsRunning || !UIInGame.SpectatorModeActive || cam == null ||
            (targetBody != removed && cam.body != removed)) return;
        SetCameraFollow(null);
        UIInGame.Spectator_ClearTargets();
        UIInGame.Spectator_SelectDefaultTarget();
        log.l("[spectator] removed target; camera rebound to " + (cam.body != null ? cam.body.name : "no body"));
    }

    internal static bool FollowCamera(PlayerCamera cam) {
        if (!UIInGame.SpectatorModeActive || Con._DEV_FREECAM || cam == null) return false;
        if (targetBody == null || !targetBody.gameObject.activeInHierarchy) {
            SetCameraFollow(null);
            UIInGame.Spectator_SelectDefaultTarget();
            if (targetBody == null) return false;
        }
        cam.body = targetBody;
        // Native 1.0.1 adds the spectator's mouse offset and smooths toward
        // limb positions. Watch the body's current network pose immediately.
        cam.customLookPos = null;
        var p = targetBody.transform.position;
        cam.transform.position = new Vector3(p.x, p.y, -3f);
        return true;
    }


}


