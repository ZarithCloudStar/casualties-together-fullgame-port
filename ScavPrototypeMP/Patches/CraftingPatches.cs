using System;
using System.Collections.Generic;
using HarmonyLib;
using KrokV5Optimization;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryCraft))]
public static class PlayerCamera_TryCraft_MultiplayerPatch
{
	static bool Prefix(PlayerCamera __instance)
	{
		if (!Net.IsRunning)
			return true;
		CraftCommit.TryClick(__instance);
		return false;
	}

	public static List<Item> CheckIfTheseItemsAreEnoughForTheRecipe(Recipe recipe, List<Item> items)
	{
		List<Item> list = new List<Item>();
		List<Item> used = new List<Item>();
		foreach (RecipeItem ingredient in recipe.items)
		{
			Item matchingItem = CraftCommit.MatchIngredient(ingredient, items, used);
			if (matchingItem == null)
				return null;
			list.Add(matchingItem);
			if (ingredient.destroyItem && !ingredient.isLiquid)
				used.Add(matchingItem);
		}
		return list;
	}

	internal static CraftSpawn RecipeResult_SpawnResult(int recipeInt, RecipeResult rres, Body i_am_steve)
	{
		var made = new List<GameObject>();
		try
		{
			if (rres == null)
				return CraftSpawn.Fail(made);
			int num = i_am_steve.skills.INT - recipeInt;
			float num2 = 1f;
			if (num < 0 && UnityEngine.Random.value < 0.5f)
			{
				switch (num)
				{
					case -1:
						break;
					case -3:
						i_am_steve.DoGoreSound();
						for (int i = 5; i <= 8; i += 3)
						{
							i_am_steve.limbs[i].pain += 40f;
							i_am_steve.limbs[i].skinHealth -= 15f;
							i_am_steve.limbs[i].bleedAmount += UnityEngine.Random.Range(2f, 5f);
						}
						return CraftSpawn.None();
					default:
						return CraftSpawn.None();
				}
				num2 = UnityEngine.Random.Range(0.2f, 0.9f);
			}

			Item resultitem = null;
			for (int j = 0; j < rres.amount; j++)
			{
				if (rres.isLiquid)
				{
					bool filled = false;
					foreach (Item item in i_am_steve.GetAllItemsThorough())
					{
						if (item == null || !item.TryGetComponent(out WaterContainerItem component))
							continue;
						LiquidSanitize.Clean(component, "CraftResult");
						if (component.stack.Count == 1 && component.stack[0].liquidId == rres.id && component.SpaceLeft >= rres.resultCondition * num2)
						{
							component.AddLiquid(rres.id, rres.resultCondition * num2);
							// 1.0.1: filling can push the carried bottle/container over weight.
							if (item.TryGetParentContainer(out var cont) &&
								(cont.AboveHoldingWeight() || item.totalWeight > cont.maxWeightPerItem))
								cont.UnloadItem(item);
							filled = true;
							return CraftSpawn.Made(item);
						}
					}
					if (!filled)
					{
						GameObject gameObject = Utils.Create("craftingbottle", i_am_steve.transform.position, 0f);
						if (gameObject == null)
							return CraftSpawn.Fail(made);
						made.Add(gameObject);
						Item component = gameObject.GetComponent<Item>();
						component.condition = rres.resultCondition;
						i_am_steve.AutoPickUpItem(component);
						component.GetComponent<WaterContainerItem>().AddLiquid(rres.id, rres.resultCondition * num2);
						UnityEngine.Object.Destroy(gameObject, 300f);
						resultitem = component;
					}
				}
				else
				{
					GameObject created = Utils.Create(rres.id, i_am_steve.transform.position, 0f);
					if (created == null)
						return CraftSpawn.Fail(made);
					made.Add(created);
					Item component2 = created.GetComponent<Item>();
					if (component2 == null)
						return CraftSpawn.Fail(made);
					component2.condition = rres.resultCondition * num2;
					i_am_steve.AutoPickUpItem(component2);
					if ((bool)component2.battery)
						component2.battery.UnloadBattery(skipSpawn: true);
					if (!rres.dontDrainResultLiquid && component2.TryGetComponent<WaterContainerItem>(out var component3))
					{
						component3.stack = new List<LiquidStack>();
						component2.condition = 0f;
					}
					resultitem = component2;
				}
			}
			if (resultitem == null)
				return rres.amount <= 0 ? CraftSpawn.None() : CraftSpawn.Fail(made);
			return CraftSpawn.Made(resultitem);
		}
		catch (Exception e)
		{
			Plugin.log.LogWarning("craft spawn " + e.Message);
			return CraftSpawn.Fail(made);
		}
	}

	[ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_TryCraft)]
	static void Server_PlayerCamera_TryCraft(knetid clientId, NetDataReader reader)
	{
		uint session = reader.GetUInt();
		uint attempt = reader.GetUInt();
		ushort recipeIndex = reader.GetUShort();
		ushort[] materialItemIds = reader.GetUShortArray();
		CraftCommit.HandleServer(clientId, session, attempt, recipeIndex, materialItemIds);
	}

	[ClientReceiver((byte)NetmsgId.CLIENT_CraftReceipt)]
	static void Client_CraftReceipt(knetid _, NetDataReader reader)
	{
		CraftCommit.OnReceipt(reader);
	}
}

[HarmonyPatch(typeof(RecipeItem), nameof(RecipeItem.DoesUseItemType))]
static class RecipeItem_LiquidFilterPatch
{
	static void Prefix(RecipeItem __instance, Item item)
	{
		if (__instance.isLiquid && item != null && item.TryGetComponent(out WaterContainerItem water))
			LiquidSanitize.Clean(water, "RecipeItem.DoesUseItemType");
	}
}

#if TESTING
// Run with --mp-liquid-stack-test after Item.SetupItems initializes recipe ingredients.
[HarmonyPatch(typeof(Item), nameof(Item.SetupItems))]
static class EmptyLiquidStackRegression
{
	static bool ran;

	static void Postfix()
	{
		if (ran || Array.IndexOf(Environment.GetCommandLineArgs(), "--mp-liquid-stack-test") < 0)
			return;
		ran = true;
		var bad = new GameObject("legacy liquid container");
		var good = new GameObject("valid liquid container");
		bad.SetActive(false);
		good.SetActive(false);
		try
		{
			var badItem = bad.AddComponent<Item>();
			var goodItem = good.AddComponent<Item>();
			var badContainer = bad.AddComponent<WaterContainerItem>();
			var goodContainer = good.AddComponent<WaterContainerItem>();
			badContainer.stack = new List<LiquidStack> {
				new LiquidStack("", 200f), new LiquidStack(null, 20f), null,
				new LiquidStack("__unknown_liquid_test__", 20f), new LiquidStack("water", float.NaN)
			};
			goodContainer.stack = new List<LiquidStack> { new LiquidStack("water", 20f) };
			var ingredient = new RecipeItem { isLiquid = true, quality = new CraftingQuality("water", 1f) };
			var candidates = new List<Item> { badItem, goodItem };
			var exclusions = new List<Item>();
			if (LiquidSanitize.Clean(badContainer, nameof(EmptyLiquidStackRegression)) != 5 ||
				ingredient.GetMatchingItem(candidates, exclusions) != goodItem)
				throw new InvalidOperationException("A malformed liquid stack blocked a valid recipe match.");

			badContainer.stack.Add(new LiquidStack("", 200f));
			if (CraftCommit.MatchIngredient(ingredient, candidates, exclusions) != goodItem)
				throw new InvalidOperationException("The recipe matcher failed to skip a malformed candidate.");
			Plugin.log.LogInfo("[liquid-stack-test] PASS");
		}
		finally
		{
			UnityEngine.Object.Destroy(bad);
			UnityEngine.Object.Destroy(good);
		}
	}
}
#endif
