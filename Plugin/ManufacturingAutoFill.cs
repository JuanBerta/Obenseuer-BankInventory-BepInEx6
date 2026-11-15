using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BankInventoryMod
{
/// <summary>
/// Auto-fill: when Fill is pressed on a liquid station (barrel, fermenter), first move the containers the fill
/// recipe needs from your inventory and then the bank into the station, and then the game's Fill runs as usual.
/// </summary>
internal static class ManufacturingAutoFill
{
	internal static void Patch(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(manufacturingProcess), "OpenLid"),
			prefix: new HarmonyMethod(typeof(ManufacturingAutoFill), nameof(OpenLidPrefix)));
		// The Fill button is only enabled when the containers are already in the station. Enable it when
		// your inventory and the bank can supply them; OpenLid then moves them in before filling.
		harmony.Patch(AccessTools.Method(typeof(manufacturingProcess), "FillRecipeAvailable"),
			postfix: new HarmonyMethod(typeof(ManufacturingAutoFill), nameof(FillAvailablePostfix)));
	}

	// The game asks FillRecipeAvailable every frame while the station is open. Planning the bank is not free,
	// so the answer is reused for a short time for the same station.
	private static manufacturingProcess cachedFor;
	private static float cachedUntil;
	private static bool cachedResult;

	private static void FillAvailablePostfix(manufacturingProcess __instance, ref bool __result)
	{
		if (__result) return;
		try
		{
			if (__instance == null) return;
			if (__instance.stage != manufacturingProcess.Stage.Ready && __instance.stage != manufacturingProcess.Stage.Failed) return;
			Recipe fill = __instance.SelectedFillRecipe;
			if (fill == null) return;
			if (ReferenceEquals(__instance, cachedFor) && Time.unscaledTime < cachedUntil)
			{
				__result = cachedResult;
				return;
			}
			int stillMissing, missingBefore;
			List<CraftFromBank.Move> moves = CraftFromBank.PlanStation(__instance, fill, 1, out stillMissing, out missingBefore, includeInventory: true);
			cachedResult = moves.Count > 0 && stillMissing == 0;
			cachedFor = __instance;
			cachedUntil = Time.unscaledTime + 0.25f;
			__result = cachedResult;
		}
		catch (Exception) { }
	}

	private static void OpenLidPrefix(manufacturingProcess __instance)
	{
		try
		{
			if (__instance == null) return;
			// Only the Fill step (Ready or Failed); Abort during processing also calls OpenLid.
			if (__instance.stage != manufacturingProcess.Stage.Ready && __instance.stage != manufacturingProcess.Stage.Failed) return;
			Recipe fill = __instance.SelectedFillRecipe;
			if (fill == null) return;
			// Check the station's own contents, not FillRecipeAvailable (our postfix makes that true when the bank can supply it).
			int stillNow, beforeNow;
			CraftFromBank.PlanStation(__instance, fill, 1, out stillNow, out beforeNow, includeInventory: false);
			if (beforeNow == 0) return;
			string msg = CraftFromBank.FillStation(__instance, fill, 1, includeInventory: true);
			var bepLog = CraftFromBank.DiagLog;
			if (bepLog != null) bepLog.LogInfo("Auto fill: " + msg);
		}
		catch (Exception e)
		{
			var bepLog = CraftFromBank.DiagLog;
			if (bepLog != null) bepLog.LogWarning("Auto fill failed: " + e.Message);
		}
	}
}
}
