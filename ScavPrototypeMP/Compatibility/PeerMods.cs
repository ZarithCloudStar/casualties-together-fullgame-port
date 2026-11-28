using System;
using System.Collections.Generic;

namespace Together;

// Admission-time plugin GUID sets. The optional GCE plugin uses these to tell
// whether a session is mixed (host or a client without GCE) and must keep only
// its local-safe modules. The sets are filled by JoinModNotice's existing
// manifest exchange; unknown peers are treated as having the mod.
internal static class PeerMods {
    static readonly HashSet<string> host = new HashSet<string>(StringComparer.Ordinal);
    static readonly Dictionary<knetid, HashSet<string>> clients = new Dictionary<knetid, HashSet<string>>();

    internal static bool HostKnown { get; private set; }

    internal static void SetHost(IEnumerable<string> ids) {
        host.Clear();
        foreach (string id in ids ?? Array.Empty<string>()) {
            if (!string.IsNullOrEmpty(id)) host.Add(id);
        }
        HostKnown = true;
    }

    internal static void SetClient(knetid clientId, IEnumerable<string> ids) {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids ?? Array.Empty<string>()) {
            if (!string.IsNullOrEmpty(id)) set.Add(id);
        }
        clients[clientId] = set;
    }

    internal static void Clear() {
        host.Clear();
        clients.Clear();
        HostKnown = false;
    }

    // Unknown host (manifest not received yet) is not treated as missing.
    internal static bool HostHas(string guid) => !HostKnown || host.Contains(guid);

    internal static bool AllClientsHave(string guid) {
        if (clients.Count == 0) return true;
        bool all = true;
        List<knetid> stale = null;
        foreach (var pair in clients) {
            if (!ScavPlayer.ClientIdToPlayerDict.ContainsKey(pair.Key)) {
                (stale ??= new List<knetid>()).Add(pair.Key);
                continue;
            }
            if (!pair.Value.Contains(guid)) all = false;
        }
        if (stale != null) {
            foreach (var id in stale) clients.Remove(id);
        }
        return all;
    }
}
