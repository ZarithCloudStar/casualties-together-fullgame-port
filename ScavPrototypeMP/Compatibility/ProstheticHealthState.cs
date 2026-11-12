#if FULLGAME
using System;
using System.Text;
using UnityEngine;

namespace Together;

// Keep the native prosthetic definition, including custom appearance/attributes.
// Reconstructing only hasProsthetic would leave repair and limb force incorrect.
internal unsafe struct ProstheticHealthState {
    internal unsafe struct Text128 {
        fixed byte bytes[128];
        public Text128(string value) {
            this = default;
            byte[] encoded = Encoding.UTF8.GetBytes(value ?? "");
            if (encoded.Length >= 128) throw new ArgumentException("Prosthetic identifier exceeds 127 UTF-8 bytes");
            fixed (byte* p = bytes) for (int i = 0; i < encoded.Length; i++) p[i] = encoded[i];
        }
        public override string ToString() {
            fixed (byte* p = bytes) {
                int n = 0; while (n < 128 && p[n] != 0) n++;
                return Encoding.UTF8.GetString(p, n);
            }
        }
    }
    byte hasProsthetic;
    Text128 itemId, destroyItem, appearancePath;
    float forceChange, armorChange, r, g, b;
    int maxHoles;

    public ProstheticHealthState(Limb.ProstheticInfo info) {
        this = default;
        // Native non-prosthetic limbs also store their medical-panel base color
        // here. Preserve those values rather than replacing a normal limb with
        // a zeroed definition when hasProsthetic is false.
        hasProsthetic = info.hasProsthetic ? (byte)1 : (byte)0;
        itemId = new Text128(info.itemId); destroyItem = new Text128(info.destroyItem);
        appearancePath = new Text128(info.appearancePath);
        forceChange = info.forceChange; armorChange = info.armorChange;
        maxHoles = info.maxHoles; r = info.r; g = info.g; b = info.b;
    }
    public void Apply(Limb limb) {
        var info = new Limb.ProstheticInfo {
            hasProsthetic = hasProsthetic != 0, itemId = itemId.ToString(), destroyItem = destroyItem.ToString(),
            appearancePath = appearancePath.ToString(), forceChange = forceChange,
            armorChange = armorChange, maxHoles = maxHoles, r = r, g = g, b = b
        };
        var old = limb.prostheticInfo;
        limb.prostheticInfo = info;
        // Presentation only, per limb. AttachProsthetic would reattach/heal the
        // whole extremity and unlock achievements every time a snapshot arrives.
        if (limb.mat != null && (old.hasProsthetic != info.hasProsthetic || old.appearancePath != info.appearancePath)) {
            var sprite = info.hasProsthetic ? Resources.Load<Sprite>(info.appearancePath) : null;
            if (sprite != null) limb.mat.SetTexture("_OverlayTex", sprite.texture);
            limb.mat.SetFloat("_OverlayVis", sprite != null ? 1f : 0f);
        }
    }
}
#endif
