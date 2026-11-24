using System;
using System.Collections.Generic;
using Together;

namespace KrokV5Optimization;

internal static class LiquidSanitize
{
    internal static int Clean(WaterContainerItem container, string source = null)
    {
        if (container == null)
            return 0;
        container.stack ??= new List<LiquidStack>();
        return Clean(container.stack, source);
    }

    internal static int Clean(IList<LiquidStack> stack, string source = null)
    {
        if (stack == null)
            return 0;

        int removed = 0;
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            LiquidStack entry = stack[i];
            if (entry == null || !IsKnownId(entry.liquidId) || !IsValidAmount(entry.amount))
            {
                stack.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
            Plugin.log.LogWarning($"[liquid] removed {removed} invalid stack(s){(string.IsNullOrEmpty(source) ? "" : " from " + source)}");
        return removed;
    }

    internal static void Clean(NonDescriptCan can)
    {
        if (can.liquidIds == null || can.liquidWeights == null)
        {
            can.liquidIds?.Clear();
            can.liquidWeights?.Clear();
            return;
        }

        int count = Math.Min(can.liquidIds.Count, can.liquidWeights.Count);
        can.liquidIds.RemoveRange(count, can.liquidIds.Count - count);
        can.liquidWeights.RemoveRange(count, can.liquidWeights.Count - count);
        for (int i = count - 1; i >= 0; i--)
        {
            if (IsKnownId(can.liquidIds[i]) && IsValidAmount(can.liquidWeights[i]))
                continue;
            can.liquidIds.RemoveAt(i);
            can.liquidWeights.RemoveAt(i);
        }
    }

    internal static bool IsKnownId(string id)
    {
        var registry = Liquids.Registry;
        return !string.IsNullOrEmpty(id) && registry != null && registry.TryGetValue(id, out var liquid) && liquid != null;
    }

    internal static bool IsValidAmount(float amount)
    {
        return amount > 0f && !float.IsNaN(amount) && !float.IsInfinity(amount);
    }

    internal static bool TryDecode(byte netId, float amount, out string id)
    {
        var names = Item_SetupItems_Listener.LiquidNetIdToId;
        id = names != null && netId < names.Length ? names[netId] : null;
        return IsValidAmount(amount) && IsKnownId(id);
    }
}
