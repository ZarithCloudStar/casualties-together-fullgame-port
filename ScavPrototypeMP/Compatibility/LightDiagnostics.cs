using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Together;

// Low-volume production diagnostics. Detailed probes remain in debug builds.
internal static class LightDiagnostics
{
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, long> Next = new Dictionary<string, long>(StringComparer.Ordinal);
    private static ConfigEntry<bool> enabled;
    private static ConfigEntry<float> interval;
    private static bool initialized;

    [CTPostInit]
    private static void Init()
    {
        if (initialized || Plugin.main == null)
            return;

        initialized = true;
        enabled = Plugin.main.Config.Bind("Diagnostics", "LightweightEvents", true,
            "Record low-volume multiplayer lifecycle events in the session log.");
        interval = Plugin.main.Config.Bind("Diagnostics", "EventIntervalSeconds", 5f,
            "Minimum interval for repeated lightweight diagnostic events.");
    }

    internal static void Event(string name, string details = "", float? minimumInterval = null)
    {
        try
        {
            if (enabled != null && !enabled.Value)
                return;

            long now = Stopwatch.GetTimestamp();
            float seconds = minimumInterval ?? (interval != null ? interval.Value : 5f);
            if (seconds < 0f)
                seconds = 0f;
            long wait = (long)(seconds * Stopwatch.Frequency);

            lock (Gate)
            {
                if (Next.TryGetValue(name, out long next) && now < next)
                    return;
                Next[name] = now + wait;
            }

            SessionLog.Event("DIAG " + SessionLog.OneLine(name), SessionLog.OneLine(details));
        }
        catch
        {
            // Diagnostics must never affect gameplay or shutdown.
        }
    }

    internal static void Protocol(string details) => Event("protocol", details, 30f);
    internal static void UseAck(string details) => Event("use-ack", details, 10f);
    internal static void Skin(string details) => Event("skin", details, 30f);
    internal static void HostTransfer(string details) => Event("host-transfer", details, 10f);
}
