using System;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Harmony finalizer on ToolTip.ValueToString. The game's version throws a NullReferenceException when an
/// item has an owner and shop data but the owner's shop isn't loaded (TradePanel.GetShopData returns null).
/// The exception aborts the item info panel update, which leaves the previous item's icon on screen.
/// This catches that one failure and shows the estimated value instead, so the panel finishes updating.
/// </summary>
internal static class TooltipGuard
{
	private static bool warned;

	internal static void Patch(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(ToolTip), "ValueToString", new[] { typeof(ItemData) }),
			finalizer: new HarmonyMethod(typeof(TooltipGuard), nameof(Finalizer)));
	}

	private static Exception Finalizer(Exception __exception, ref string __result, ItemData itemData)
	{
		if (__exception == null) return null;
		try
		{
			__result = "Estimated value: " + itemData.item.GetItemSalePrice(itemData.meta) + " OC";
		}
		catch (Exception)
		{
			__result = "Estimated value: ?";
		}
		if (!warned)
		{
			warned = true;
			Debug.LogWarning("[BankInventory] Tooltip value fallback used for item " + (itemData != null && itemData.item != null ? itemData.item.ID.ToString() : "?") + " (" + __exception.GetType().Name + ").");
		}
		return null;
	}
}
