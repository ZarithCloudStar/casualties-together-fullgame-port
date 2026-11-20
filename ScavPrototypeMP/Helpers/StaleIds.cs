using System.Collections.Generic;
using UnityEngine;

namespace Together;

internal static class StaleIds {
    internal const int FirstThreshold = 8;

    internal static HashSet<int> LiveIds<TObject>() where TObject : UnityEngine.Object {
        var live = new HashSet<int>();
        foreach (var item in Resources.FindObjectsOfTypeAll<TObject>())
            live.Add(item.GetInstanceID());
        return live;
    }

    internal static void Prune<TObject, TValue>(Dictionary<int, TValue> table, ref int threshold)
        where TObject : UnityEngine.Object {
        if (table.Count < threshold) return;
        RemoveMissing(table, LiveIds<TObject>());
        threshold = Mathf.Max(FirstThreshold, table.Count * 2);
    }

    internal static void RemoveMissing<TValue>(Dictionary<int, TValue> table, HashSet<int> live) {
        var gone = new List<int>();
        foreach (var id in table.Keys)
            if (!live.Contains(id)) gone.Add(id);
        foreach (var id in gone) table.Remove(id);
    }
}
