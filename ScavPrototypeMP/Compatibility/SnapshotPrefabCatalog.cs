#if FULLGAME
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together;

internal sealed class SnapshotPrefabReference : MonoBehaviour { public string resource; }

// Discover the actual resource pool, including decorations in Special and
// background children in structures that this seed never generated.
internal static class SnapshotPrefabCatalog {
    internal const string Prefix = "CTNODE:";
    static bool ready;
    static readonly Dictionary<string, GameObject> roots = new();
    static Dictionary<string, Sprite> sprites;
    internal static string SpriteKey(Sprite sprite) => sprite.name + "|" + sprite.texture.name + "|" + sprite.rect.ToString("F3");
    internal static Sprite Sprite(string key) {
        if (sprites == null) sprites = Resources.LoadAll<Sprite>("").GroupBy(SpriteKey).ToDictionary(g => g.Key, g => g.First());
        return sprites.TryGetValue(key, out var sprite) ? sprite : null;
    }
    internal static string PathFor(GameObject root) {
        foreach (var prefix in new[] { "", "Structures/", "Special/", "Buildings/" })
            if (Resources.Load<GameObject>(prefix + root.name) == root) return prefix + root.name;
        return null;
    }
    internal static void Initialize() {
        if (ready) return;
        RememberSpiceVinePrefab(ScavMPBuildingSynchronizer.SpiceVineGround);
        RememberSpiceVinePrefab(ScavMPBuildingSynchronizer.SpiceVineCeiling);
        foreach (var root in Resources.LoadAll<GameObject>("")) {
            var path = PathFor(root);
            if (path == null) continue;
            foreach (var map in root.GetComponentsInChildren<Tilemap>(true))
                if (map.GetComponent<BuildingEntity>() != null) BackgroundSnapshot.TryRegisterPrefab(map.transform);
            var building = root.GetComponent<BuildingEntity>();
            if (building != null && !WorldObjectSnapshot.IsDynamicNode(root.transform) && root.GetComponent<Tilemap>() == null && Resources.Load<GameObject>(building.id ?? "") == null) {
                roots[path] = root;
                (root.GetComponent<SnapshotPrefabReference>() ?? root.AddComponent<SnapshotPrefabReference>()).resource = Prefix + Uri.EscapeDataString(path) + "|";
            }
        }
        ready = true;
    }
    static void RememberSpiceVinePrefab(string path) {
        var prefab = Resources.Load<GameObject>(path);
        if (prefab == null || !prefab.TryGetComponent<Climbable>(out _) ||
            !prefab.TryGetComponent<BuildingEntity>(out var building) ||
            !ScavMPBuildingSynchronizer.IsSpiceVine(building)) {
            log.warn($"[spicevine] Expected climbable building prefab at {path}");
            return;
        }
        (prefab.GetComponent<SnapshotPrefabReference>() ?? prefab.AddComponent<SnapshotPrefabReference>()).resource = path;
    }

    internal static void RegisterEntityPrefab(GameObject root) {
        var path = PathFor(root);
        if (path == null) throw new InvalidOperationException("Structure resource path unresolved: " + root.name);
        roots[path] = root;
        // GenerateEntityAtPos instantiates these children independently. Their
        // standalone ID can name a different variant (RestSpot's foodbox has
        // no DamagingCrate). Register at the actual generation boundary, leaving
        // scene-owned laboratory entities on their dedicated lifecycle.
        foreach (Transform child in root.transform) {
            if (child.GetComponent<Tilemap>() != null ||
                (child.GetComponent<BuildingEntity>() == null && child.GetComponent<Item>() == null)) continue;
            (child.GetComponent<SnapshotPrefabReference>() ?? child.gameObject.AddComponent<SnapshotPrefabReference>()).resource =
                Prefix + Uri.EscapeDataString(path) + "|" + child.GetSiblingIndex();
        }
    }
    internal static GameObject Resolve(string resource) {
        if (resource == null) return null;
        if (!resource.StartsWith(Prefix, StringComparison.Ordinal)) return Resources.Load<GameObject>(resource);
        var parts = resource.Substring(Prefix.Length).Split('|');
        if (parts.Length != 2) return null;
        var path = Uri.UnescapeDataString(parts[0]);
        if (!roots.TryGetValue(path, out var root) || root == null) root = Resources.Load<GameObject>(path);
        if (root == null) return null;
        roots[path] = root;
        var t = root.transform;
        if (parts[1].Length != 0) foreach (var step in parts[1].Split('/')) {
            if (!int.TryParse(step, out int index) || index < 0 || index >= t.childCount) return null;
            t = t.GetChild(index);
        }
        if (t.GetComponent<BuildingEntity>() == null && t.GetComponent<Item>() == null) return null;
        (t.GetComponent<SnapshotPrefabReference>() ?? t.gameObject.AddComponent<SnapshotPrefabReference>()).resource = resource;
        return t.gameObject;
    }
}
#endif
