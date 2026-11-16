#if FULLGAME
using UnityEngine;

namespace Together;

// Client-only pose playback. GuardScript.Update remains disabled: no AI,
// movement decisions, attacks, damage or loot are simulated here.
internal sealed class EndingGuardPresentation : MonoBehaviour {
    GuardScript guard;
    bool hasPose, hasLaser, dead, melee, wasMelee, deathStarted, teslaPlaying;
    bool lineOn, dotOn, stunned;
    int sprite;
    float scale, arms, head, shownArms, shownHead, length, shownLength, meleeStarted;
    Vector2 position, shownPosition, dot, shownDot;

    internal void ReceivePose(GuardScript g, Vector2 pos, float facing, float aim, float look,
        bool died, bool swinging, int bodySprite) {
        guard = g;
        bool snap = !hasPose || Mathf.Sign(scale) != Mathf.Sign(facing) || (shownPosition - pos).sqrMagnitude > 400f;
        position = pos; scale = facing; arms = aim; head = look;
        dead = died; melee = swinging; sprite = bodySprite;
        if (snap) {
            shownPosition = pos; shownArms = aim; shownHead = look;
        }
        hasPose = true;
    }

    internal void ReceiveLaser(float distance, Vector2 localDot, byte flags) {
        length = distance; dot = localDot;
        lineOn = (flags & 1) != 0; dotOn = (flags & 2) != 0; stunned = (flags & 4) != 0;
        if (!hasLaser) { shownLength = length; shownDot = dot; }
        hasLaser = true;
        ApplyLaser();
    }

    void LateUpdate() {
        if (!hasPose || guard == null || !Net.IsClient || !EndingGuardSync.Active) return;
        float blend = 1f - Mathf.Exp(-20f * Time.deltaTime);
        shownPosition = Vector2.Lerp(shownPosition, position, blend);
        shownArms = Mathf.LerpAngle(shownArms, arms, blend);
        shownHead = Mathf.LerpAngle(shownHead, head, blend);
        guard.transform.position = shownPosition;
        guard.transform.localScale = new Vector3(scale, 1, 1);
        ApplyAnimation();
        // Melee/death clips own their transforms and body sprite while playing.
        if (!dead && !melee) {
            guard.arms.rotation = Quaternion.Euler(0, 0, shownArms);
            guard.head.rotation = Quaternion.Euler(0, 0, shownHead);
            guard.myBody.sprite = sprite >= 0 && sprite < guard.bodyMoveSprites.Length
                ? guard.bodyMoveSprites[sprite] : guard.bodyStaySprite;
        }
        if (guard.armSprite != null && !dead && !melee)
            guard.armSprite.sprite = stunned ? guard.armsStun : guard.armsNormal;
        if (hasLaser) {
            shownLength = Mathf.Lerp(shownLength, length, blend);
            shownDot = Vector2.Lerp(shownDot, dot, blend);
        }
        ApplyLaser();
    }

    void ApplyLaser() {
        if (guard == null) return;
        if (guard.laserLine != null) {
            if (hasLaser) {
                var size = guard.laserLine.transform.localScale;
                size.x = 1f; size.y = shownLength;
                guard.laserLine.transform.localScale = size;
            }
            guard.laserLine.enabled = !dead && !melee && (!hasLaser || lineOn);
        }
        if (guard.laserDot != null) {
            if (hasLaser) {
                var local = guard.laserDot.transform.localPosition;
                local.x = shownDot.x; local.y = shownDot.y;
                guard.laserDot.transform.localPosition = local;
            }
            guard.laserDot.enabled = !dead && !melee && (!hasLaser || dotOn);
        }
    }

    void ApplyAnimation() {
        // A registry snapshot can arrive before the native Start populated these.
        if (guard.animator == null) return;
        guard.doingMelee = melee && !dead;
        if (dead) {
            guard.didDie = true;
            if (guard.col != null) guard.col.enabled = false;
            if (!deathStarted) {
                deathStarted = true;
                guard.animator.enabled = true;
                guard.animator.Play("GuardFall");
            }
            StopTesla();
            return;
        }
        if (melee && !wasMelee) {
            meleeStarted = Time.time;
            guard.animator.enabled = true;
            guard.animator.Play("GuardSwing", -1, 0f);
        } else if (!melee && wasMelee) {
            guard.animator.Update(1f);
            guard.animator.enabled = false;
            shownArms = guard.arms.eulerAngles.z;
            shownHead = guard.head.eulerAngles.z;
        }
        wasMelee = melee;
        float elapsed = Time.time - meleeStarted;
        if (melee && elapsed >= .225f && elapsed < .725f && !teslaPlaying) {
            teslaPlaying = true;
            if (guard.tesla != null) guard.tesla.Play();
            Sound.Play("guardswing", guard.transform.position);
        } else if (!melee || elapsed >= .725f) StopTesla();
    }

    void StopTesla() {
        if (!teslaPlaying) return;
        teslaPlaying = false;
        if (guard.tesla != null) guard.tesla.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }
}
#endif
