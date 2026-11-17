#if FULLGAME
using CasualtiesTogetherUtils;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together;

// Client-only presentation for CrystalEnemy ("manifestation"). The native
// CrystalEnemy.Update is server-only, so the client otherwise never gets the
// windup/lunge line or the enemy's chosen color.
internal sealed class CrystalEnemyClientVisual : MonoBehaviour {
    CrystalEnemy enemy;
    LineRenderer line;
    SpriteRenderer sprite;
    Light2D light;
    float until;
    Color color = Color.white;

    void Awake() {
        enemy = GetComponent<CrystalEnemy>();
        sprite = GetComponent<SpriteRenderer>();
        light = GetComponent<Light2D>();
        if (transform.childCount > 0) line = transform.GetChild(0).GetComponent<LineRenderer>();
        if (line != null) {
            line.startColor = Color.clear;
            line.endColor = Color.clear;
        }
    }

    internal static void PlayAttack(CrystalEnemy enemy) {
        if (enemy == null || !Net.IsClient) return;
        enemy.GetOrAddComponent<CrystalEnemyClientVisual>().Play();
    }

    void Play() {
        if (line == null || enemy == null) return;
        color = sprite != null ? sprite.color : Color.white;
        Vector2 start = enemy.transform.position;
        Vector2 end = start + (Vector2)enemy.transform.up * 64f;
        line.SetPosition(0, start);
        line.SetPosition(1, end);
        line.startColor = color;
        line.endColor = color;
        line.widthMultiplier = 0.75f;
        until = Time.time + 0.35f;
        Sound.Play("crystalenemywindup", start, twoDimensional: false);
    }

    void Update() {
        if (line == null || !Net.IsClient) return;
        if (Time.time >= until) {
            line.startColor = Color.clear;
            line.endColor = Color.clear;
            return;
        }
        float k = Mathf.Clamp01((until - Time.time) / 0.35f);
        Color c = color; c.a *= k;
        line.startColor = c;
        line.endColor = c;
        line.widthMultiplier = 0.75f * k;
    }
}
#endif
