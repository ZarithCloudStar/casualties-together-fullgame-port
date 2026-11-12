using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Together;

namespace KrokV5Optimization;

/// <summary>
/// every server frame replaced the chunk-hash grid. 0 already means empty, so clear the existing grid.
/// </summary>
[HarmonyPatch(typeof(WorldChunkSync), "LateUpdate", new System.Type[0])]
internal static class FixChunkHashes
{
	internal static long Reused;

	// this file's counters on the 10s report.
	internal static string Evidence() => "chunkHash=" + Reused;

	// replace `new uint[w, h]` inside LateUpdate with Rent. other newobj calls stay.
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		var rent = AccessTools.Method(typeof(FixChunkHashes), nameof(Rent));
		bool replaced = false;
		bool integrated = false;
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.Calls(rent))
				integrated = true;
			if (instruction.opcode == OpCodes.Newobj && IsGridCtor(instruction.operand))
			{
				// Keep labels/exception metadata when replacing the allocation.
				instruction.opcode = OpCodes.Call;
				instruction.operand = rent;
				replaced = true;
				yield return instruction;
				continue;
			}
			yield return instruction;
		}
		if (replaced || integrated)
			OptimizerMain.Log.LogInfo("[KrokV5Opt] FixChunkHashes: WorldChunkSync.LateUpdate " + (integrated ? "already calls Rent" : "grid allocation replaced"));
		else
			OptimizerMain.Log.LogWarning("[KrokV5Opt] FixChunkHashes safely skipped: no uint[,] allocation or Rent call in WorldChunkSync.LateUpdate");
	}

	// true only for the 2d uint chunk-hash grid, not every array LateUpdate allocates.
	static bool IsGridCtor(object operand)
	{
		if (!(operand is System.Reflection.ConstructorInfo ctor))
			return false;
		System.Type type = ctor.DeclaringType;
		return type != null
		       && type.IsArray
		       && type.GetArrayRank() == 2
		       && type.GetElementType() == typeof(uint);
	}

	internal static uint[,] Rent(int width, int height)
	{
		uint[,] grid = WorldChunkSync.Server_ChunkHashes;
		if (grid == null || grid.GetLength(0) != width || grid.GetLength(1) != height)
			return new uint[width, height];
		System.Array.Clear(grid, 0, grid.Length);
		Reused++;
		return grid;
	}
}
