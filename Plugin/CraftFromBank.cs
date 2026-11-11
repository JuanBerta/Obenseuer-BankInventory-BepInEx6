using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using OS.Items;
using UnityEngine;

namespace BankInventoryMod
{
	/// <summary>
	/// Take ingredients from the bank: while a crafting station is open with a recipe shown, a button moves only
	/// the missing ingredients from the bank into the station's input slots (the game's own matching decides what
	/// counts: item groups, liquids in containers, ammo). At a furniture workbench it moves the missing materials
	/// into your inventory. The items really move, so the game (and other crafting mods) craft as usual.
	/// </summary>
	internal static class CraftFromBank
	{
		internal static ConfigEntry<bool> Enabled;
		internal static ConfigEntry<bool> AutoFill;
		internal static ConfigEntry<bool> UseStolen;
		private static ManualLogSource log;

		internal static void Init(ConfigFile cfg, ManualLogSource logger, GameObject host)
		{
			log = logger;
			Enabled = cfg.Bind("Crafting", "TakeIngredientsButton", true,
				"While a crafting station or furniture workbench is open, show a button that moves the missing ingredients from the bank into the station (or your inventory, for furniture).");
			AutoFill = cfg.Bind("Crafting", "AutoFillOnRecipe", false,
				"Move the missing ingredients automatically whenever you pick a recipe (or change the amount) at a crafting station.");
			UseStolen = cfg.Bind("Crafting", "UseStolenItems", false,
				"Also use items in the bank that are owned by someone else (stolen).");
			try { new Harmony("com.juanberta.obenseuer.bankinventory.craft").PatchAll(typeof(CraftFromBank.Hooks)); }
			catch (Exception e) { logger.LogWarning("Craft from bank patches: " + e.Message); }
			host.AddComponent<CraftFromBankUI>();
		}

		// ---- What the player is looking at ----

		private static Recipe lastRecipe;
		private static CraftingBase lastStation;
		internal static int ShownMultiplier = 1;
		private static RecipeInfoUI recipePanel;

		// The recipe to fill for: the one the info panel shows, or else the last one you hovered or
		// picked at this same station (some stations only show the panel while you hover a recipe).
		internal static Recipe ShownRecipe
		{
			get
			{
				try
				{
					CraftingBase cur = CraftingBase.currentCraftingBase;
					if (cur == null || cur != lastStation) return null;
					if (recipePanel != null && recipePanel.gameObject.activeInHierarchy && recipePanel.CurrentRecipe() != null)
						return recipePanel.CurrentRecipe();
					return lastRecipe;
				}
				catch (Exception) { return null; }
			}
		}

		internal static BuildingSystem.FurnitureInfo ShownFurniture;
		private static Recipe autoRecipe;
		private static int autoMultiplier;
		internal static bool AutoPending;

		internal static CraftingBase Station
		{
			get
			{
				try { return CraftingBase.currentCraftingBase != null && CraftingBase.currentCraftingBase.InputOutputStorage != null ? CraftingBase.currentCraftingBase : null; }
				catch (Exception) { return null; }
			}
		}

		internal static bool WorkbenchOpen
		{
			get
			{
				try
				{
					if (ShopController.instance == null || !ShopController.instance.menuIsOpen) return false;
					return Traverse.Create(ShopController.instance).Field("currentCrafting").GetValue<object>() is FurnitureCrafting;
				}
				catch (Exception) { return false; }
			}
		}

		internal static TaskItemBlueprint ShownBlueprint
		{
			get { return ShownFurniture != null ? ShownFurniture.taskItem as TaskItemBlueprint : null; }
		}

		// ---- Bank stacks that may be used ----

		private static IEnumerable<SlotController> BankSlots()
		{
			BankStorage bank = BankStorage.Instance;
			if (bank == null || bank.Slots == null) yield break;
			foreach (SlotController sc in bank.Slots)
			{
				if (sc == null || sc.itemStack == null || sc.itemStack.itemId < 0 || sc.itemStack.itemAmount <= 0) continue;
				if (sc.itemStack.itemId == 14930) continue;   // the Backpack item is never moved
				if (!UseStolen.Value && sc.itemStack.ownerId > 1) continue;
				if (sc.itemStack.itemReference == null) sc.itemStack.SetItemReference();
				if (sc.itemStack.itemReference == null || sc.itemStack.itemReference.Item == null) continue;
				yield return sc;
			}
		}

		// ---- Crafting station: plan with the game's own requirement matching ----

		internal struct Move { public SlotController From; public int Amount; }

		private static Recipe.RecipeRequirementData Copy(Recipe.RecipeRequirementData r)
		{
			return new Recipe.RecipeRequirementData
			{
				requiredItem = r.requiredItem, requiredAmount = r.requiredAmount, isGroup = r.isGroup,
				groupAllowMixed = r.groupAllowMixed, groupData = r.groupData, groupActiveItemID = r.groupActiveItemID
			};
		}

		// What to move from the bank so the station has everything; missing = requirements the bank can't cover.
		internal static List<Move> PlanStation(CraftingBase station, Recipe recipe, int multiplier, out int stillMissing, out int missingBefore)
		{
			var moves = new List<Move>();
			stillMissing = 0; missingBefore = 0;
			if (station == null || recipe == null) return moves;
			if (recipe.Input == null) recipe.SetItems();
			if (recipe.Input == null) return moves;
			Recipe.RecipeRequirementData[] reqs = CraftingBaseHelperFunctions.CreateRequirements(recipe, true);
			int mult = Mathf.Max(1, multiplier);
			foreach (var r in reqs) r.requiredAmount *= mult;
			foreach (ItemStack st in station.InputSlots)
			{
				if (st == null || st.itemId < 0 || st.itemAmount <= 0) continue;
				for (int j = 0; j < reqs.Length; j++) reqs[j] = CraftingBaseHelperFunctions.ProcessRequirementWithItemStack(reqs[j], st, recipe);
			}
			var used = new Dictionary<SlotController, int>();
			foreach (var r in reqs)
			{
				if (r.requiredAmount <= 0.0001f) continue;
				missingBefore++;
				foreach (SlotController sc in BankSlots())
				{
					if (r.requiredAmount <= 0.0001f) break;
					int taken; used.TryGetValue(sc, out taken);
					int left = sc.itemStack.itemAmount - taken;
					if (left <= 0) continue;
					var test = Copy(r);
					float before = test.requiredAmount;
					test.requiredAmount = 1e9f;   // how much the whole stack could give
					test = CraftingBaseHelperFunctions.ProcessRequirementWithItemStack(test, sc.itemStack, recipe);
					float give = 1e9f - test.requiredAmount;
					if (give <= 0.0001f) continue;
					float per = give / sc.itemStack.itemAmount;
					int k = Mathf.Min(left, Mathf.CeilToInt(before / per - 0.0001f));
					if (k <= 0) continue;
					if (r.isGroup && !r.groupAllowMixed && r.groupActiveItemID < 0) r.groupActiveItemID = sc.itemStack.itemId;
					moves.Add(new Move { From = sc, Amount = k });
					used[sc] = taken + k;
					r.requiredAmount = Mathf.Max(0f, before - k * per);
				}
				if (r.requiredAmount > 0.0001f) stillMissing++;
			}
			return moves;
		}

		internal static string FillStation(CraftingBase station, Recipe recipe, int multiplier)
		{
			int still, before;
			List<Move> moves = PlanStation(station, recipe, multiplier, out still, out before);
			if (before == 0) return "The station already has everything for this recipe.";
			if (moves.Count == 0) return "The bank has none of the missing ingredients.";
			Storage target = station.InputOutputStorage;
			int movedStacks = 0, full = 0;
			foreach (Move m in moves)
			{
				ItemStack st = m.From.itemStack;
				Item item = st.itemReference.Item;
				object[] meta = Item.CloneMeta(st.Meta);
				int remaining = target.AddItemAndReturnRemaining(item, m.Amount, st.ownerId, meta);
				int moved = m.Amount - Mathf.Max(0, remaining);
				if (moved <= 0) { full++; continue; }
				Shrink(m.From, moved);
				movedStacks++;
			}
			Refresh(target);
			try { station.CheckItems(true); } catch (Exception) { }
			if (movedStacks == 0) return "The station has no room for the ingredients.";
			string msg = "Moved " + movedStacks + " stack" + (movedStacks > 1 ? "s" : "") + " from the bank.";
			if (full > 0) msg += " The station ran out of room.";
			else if (still > 0) msg += " The bank didn't have everything (" + still + " ingredient" + (still > 1 ? "s" : "") + " still missing).";
			return msg;
		}

		// ---- Furniture workbench: blueprint materials into the inventory ----

		internal static List<Move> PlanBlueprint(TaskItemBlueprint bp, out int stillMissing, out int missingBefore)
		{
			var moves = new List<Move>();
			stillMissing = 0; missingBefore = 0;
			if (bp == null || bp.resourceItems == null || Inventory.instance == null) return moves;
			var used = new Dictionary<SlotController, int>();
			foreach (var res in bp.resourceItems)
			{
				if (res == null || res.item == null || res.item.Item == null) continue;   // furniture parts aren't items
				int have = Mathf.FloorToInt(Inventory.instance.CountItems(res.item));
				int need = res.amount - have;
				if (need <= 0) continue;
				missingBefore++;
				foreach (SlotController sc in BankSlots())
				{
					if (need <= 0) break;
					if (sc.itemStack.itemId != res.item.ID) continue;
					int taken; used.TryGetValue(sc, out taken);
					int k = Mathf.Min(need, sc.itemStack.itemAmount - taken);
					if (k <= 0) continue;
					moves.Add(new Move { From = sc, Amount = k });
					used[sc] = taken + k;
					need -= k;
				}
				if (need > 0) stillMissing++;
			}
			return moves;
		}

		internal static string FillBlueprint(TaskItemBlueprint bp)
		{
			int still, before;
			List<Move> moves = PlanBlueprint(bp, out still, out before);
			if (before == 0) return "You already carry all the materials.";
			if (moves.Count == 0) return "The bank has none of the missing materials.";
			int movedStacks = 0, full = 0;
			foreach (Move m in moves)
			{
				ItemStack st = m.From.itemStack;
				Item item = st.itemReference.Item;
				if (!Inventory.instance.InventoryIsNotFull(item.ID, st.ownerId, m.Amount, st.Meta)) { full++; continue; }
				int remaining = ItemOperations.AddItemAndReturnRemaining(item, st.ownerId, null, Item.CloneMeta(st.Meta), m.Amount);
				int moved = m.Amount - Mathf.Max(0, remaining);
				if (moved <= 0) { full++; continue; }
				Shrink(m.From, moved);
				movedStacks++;
			}
			try { Inventory.instance.UpdateAllSlots(); } catch (Exception) { }
			if (movedStacks == 0) return "Your inventory has no room for the materials.";
			string msg = "Moved " + movedStacks + " stack" + (movedStacks > 1 ? "s" : "") + " from the bank to your inventory.";
			if (full > 0) msg += " Your inventory ran out of room.";
			else if (still > 0) msg += " The bank didn't have everything.";
			return msg;
		}

		// ---- Helpers ----

		private static void Shrink(SlotController from, int moved)
		{
			ItemStack st = from.itemStack;
			st.itemAmount -= moved;
			if (st.itemAmount <= 0) from.RemoveItem();
			else if (from.slot != null) from.slot.UpdateSlot();
		}

		private static void Refresh(Storage target)
		{
			try
			{
				if (Inventory.instance != null)
				{
					if (Inventory.instance.storagePanelUI != null) Inventory.instance.storagePanelUI.UpdateStoragePanelUI(target);
					Inventory.instance.UpdateAllSlots();
				}
			}
			catch (Exception e) { if (log != null) log.LogWarning("Craft from bank refresh: " + e.Message); }
		}

		internal static void Notify(string text)
		{
			if (Notifications.instance != null) Notifications.instance.CreateNotification("Bank", text);
		}

		internal static void TickAuto()
		{
			if (!AutoPending) return;
			AutoPending = false;
			CraftingBase st = Station;
			if (st == null || ShownRecipe == null) return;
			int still, before;
			if (PlanStation(st, ShownRecipe, ShownMultiplier, out still, out before).Count == 0) return;
			Notify(FillStation(st, ShownRecipe, ShownMultiplier));
		}

		internal static class Hooks
		{
			[HarmonyPatch(typeof(RecipeInfoUI), "ShowRecipe", new Type[] { typeof(Recipe), typeof(int), typeof(float), typeof(float), typeof(bool), typeof(bool) })]
			[HarmonyPostfix]
			private static void ShowRecipe_Postfix(RecipeInfoUI __instance, Recipe recipe, int multiplier)
			{
				if (recipe == null || CraftingBase.currentCraftingBase == null) return;
				recipePanel = __instance;
				lastRecipe = recipe;
				lastStation = CraftingBase.currentCraftingBase;
				ShownMultiplier = Mathf.Max(1, multiplier);
				if (Enabled.Value && AutoFill.Value && recipe != null && CraftingBase.currentCraftingBase != null && (recipe != autoRecipe || ShownMultiplier != autoMultiplier))
				{
					autoRecipe = recipe;
					autoMultiplier = ShownMultiplier;
					AutoPending = true;   // done next frame, not while the game builds its panel
				}
			}

			[HarmonyPatch(typeof(FurnitureInfoUI), "UpdateFurnitureInfo")]
			[HarmonyPostfix]
			private static void FurnitureInfo_Postfix(BuildingSystem.FurnitureInfo furnitureInfo)
			{
				ShownFurniture = furnitureInfo;
			}
		}
	}

	/// <summary>The "Take ingredients from bank" button, at the top of the screen while a station is open.</summary>
	internal class CraftFromBankUI : MonoBehaviour
	{
		private GUIStyle style, hint;
		private float nextCheck;
		private string label;
		private bool usable;
		private int kind;   // 1 = station, 2 = workbench

		private static Texture2D Tex(Color fill, Color edge)
		{
			var t = new Texture2D(8, 8) { hideFlags = HideFlags.HideAndDontSave };
			for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) t.SetPixel(x, y, x == 0 || y == 0 || x == 7 || y == 7 ? edge : fill);
			t.Apply();
			return t;
		}

		private void Update()
		{
			try { CraftFromBank.TickAuto(); } catch (Exception) { }
			if (Time.unscaledTime < nextCheck) return;
			nextCheck = Time.unscaledTime + 0.3f;
			kind = 0; label = null; usable = false;
			if (!CraftFromBank.Enabled.Value || BankStorage.Instance == null) return;
			try
			{
				int still, before;
				CraftingBase st = CraftFromBank.Station;
				if (st != null && CraftFromBank.ShownRecipe != null)
				{
					kind = 1;
					var plan = CraftFromBank.PlanStation(st, CraftFromBank.ShownRecipe, CraftFromBank.ShownMultiplier, out still, out before);
					usable = plan.Count > 0;
					string name = CraftFromBank.ShownRecipe.Name;
					label = before == 0 ? name + ": station has all ingredients" : usable ? "Take ingredients for " + name + " from bank (" + plan.Count + ")" : name + ": bank has none of the missing ingredients";
				}
				else if (CraftFromBank.WorkbenchOpen && CraftFromBank.ShownBlueprint != null)
				{
					kind = 2;
					var plan = CraftFromBank.PlanBlueprint(CraftFromBank.ShownBlueprint, out still, out before);
					usable = plan.Count > 0;
					label = before == 0 ? "You carry all the materials" : usable ? "Take materials from bank (" + plan.Count + ")" : "Bank has none of the missing materials";
				}
			}
			catch (Exception) { kind = 0; }
		}

		private void OnGUI()
		{
			if (kind == 0 || label == null || !Cursor.visible) return;
			if (style == null || style.normal.background == null)
			{
				style = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold, padding = new RectOffset(14, 14, 7, 7), border = new RectOffset(2, 2, 2, 2) };
				style.normal.background = Tex(new Color(0.42f, 0.29f, 0.08f, 0.95f), new Color(0.79f, 0.6f, 0.23f));
				style.hover.background = Tex(new Color(0.6f, 0.42f, 0.12f, 0.95f), new Color(0.95f, 0.75f, 0.35f));
				style.active.background = Tex(new Color(0.79f, 0.6f, 0.23f, 0.95f), new Color(0.95f, 0.75f, 0.35f));
				style.normal.textColor = style.hover.textColor = new Color(0.98f, 0.93f, 0.8f);
				style.active.textColor = new Color(0.14f, 0.11f, 0.08f);
				hint = new GUIStyle(style);
				hint.normal.background = hint.hover.background = hint.active.background = Tex(new Color(0.1f, 0.09f, 0.08f, 0.85f), new Color(0.35f, 0.32f, 0.27f));
				hint.normal.textColor = hint.hover.textColor = hint.active.textColor = new Color(0.66f, 0.62f, 0.53f);
			}
			GUIStyle s = usable ? style : hint;
			Vector2 size = s.CalcSize(new GUIContent(label));
			Rect r = new Rect((Screen.width - size.x) / 2f, 12f, size.x, size.y);
			if (GUI.Button(r, label, s) && usable)
			{
				try
				{
					string msg = kind == 1 ? CraftFromBank.FillStation(CraftFromBank.Station, CraftFromBank.ShownRecipe, CraftFromBank.ShownMultiplier)
					                       : CraftFromBank.FillBlueprint(CraftFromBank.ShownBlueprint);
					CraftFromBank.Notify(msg);
				}
				catch (Exception e) { CraftFromBank.Notify("Couldn't take the items: " + e.Message); }
				nextCheck = 0f;
			}
		}
	}
}
