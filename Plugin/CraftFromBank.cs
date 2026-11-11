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
			TenementFromBank.Init(cfg, logger);
		}

		// ---- What the player is looking at ----

		private static Recipe lastRecipe;
		private static CraftingBase lastStation;
		internal static int ShownMultiplier = 1;
		internal static int Amount = 1;   // how many crafts / pieces of furniture to gather for
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

		internal static IEnumerable<SlotController> BankSlots()
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
			// Tools (utility items, tableware...) are needed once, not once per craft: the game marks them
			// with amount 0 when it asks for "non-executable" requirements.
			Recipe.RecipeRequirementData[] consumed = CraftingBaseHelperFunctions.CreateRequirements(recipe, false);
			int mult = Mathf.Max(1, multiplier);
			for (int i = 0; i < reqs.Length; i++)
				if (i >= consumed.Length || consumed[i].requiredAmount > 0f) reqs[i].requiredAmount *= mult;
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
					// How much the whole stack could give. Kept small enough for float precision
					// (1e9 - 1 is still 1e9 as a float, which made every stack look useless).
					const float probe = 100000f;
					test.requiredAmount = probe;
					test = CraftingBaseHelperFunctions.ProcessRequirementWithItemStack(test, sc.itemStack, recipe);
					float give = probe - test.requiredAmount;
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

		internal static List<Move> PlanBlueprint(TaskItemBlueprint bp, int count, out int stillMissing, out int missingBefore)
		{
			var moves = new List<Move>();
			stillMissing = 0; missingBefore = 0;
			if (bp == null || bp.resourceItems == null || Inventory.instance == null) return moves;
			var used = new Dictionary<SlotController, int>();
			foreach (var res in bp.resourceItems)
			{
				if (res == null || res.item == null || res.item.Item == null) continue;   // furniture parts aren't items
				int have = Mathf.FloorToInt(Inventory.instance.CountItems(res.item));
				int need = res.amount * Mathf.Max(1, count) - have;
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

		internal static string FillBlueprint(TaskItemBlueprint bp, int count)
		{
			int still, before;
			List<Move> moves = PlanBlueprint(bp, count, out still, out before);
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

		// ---- How many crafts the station + bank can cover ----

		internal const int MaxAmount = 999;

		internal static int MaxStation(CraftingBase station, Recipe recipe)
		{
			int still, before;
			Func<int, bool> ok = n => { PlanStation(station, recipe, n, out still, out before); return still == 0; };
			if (!ok(1)) return 0;
			int lo = 1, hi = 2;
			while (hi <= MaxAmount && ok(hi)) { lo = hi; hi *= 2; }
			if (hi > MaxAmount) { if (ok(MaxAmount)) return MaxAmount; hi = MaxAmount; }
			while (hi - lo > 1) { int mid = (lo + hi) / 2; if (ok(mid)) lo = mid; else hi = mid; }
			return lo;
		}

		internal static int MaxBlueprint(TaskItemBlueprint bp)
		{
			if (bp == null || bp.resourceItems == null || Inventory.instance == null) return 0;
			int max = MaxAmount; bool any = false;
			foreach (var res in bp.resourceItems)
			{
				if (res == null || res.item == null || res.item.Item == null || res.amount <= 0) continue;
				any = true;
				int have = Mathf.FloorToInt(Inventory.instance.CountItems(res.item)) + TenementFromBank.BankCount(res.item.ID);
				max = Mathf.Min(max, have / res.amount);
			}
			return any ? max : 0;
		}

		// ---- Back to the bank ----

		// Puts a stack into the bank, growing it by a page if it's full. Returns how many went in.
		private static int ToBank(Item item, int amount, int owner, object[] meta)
		{
			BankStorage bank = BankStorage.Instance;
			if (bank == null || amount <= 0) return 0;
			int remaining = bank.AddItemAndReturnRemaining(item, amount, owner, Item.CloneMeta(meta));
			for (int tries = 0; remaining > 0 && tries < 3; tries++)
			{
				bank.ExpandByPage(Mathf.Max(1, bank.PageSize));
				remaining = bank.AddItemAndReturnRemaining(item, remaining, owner, Item.CloneMeta(meta));
			}
			return amount - Mathf.Max(0, remaining);
		}

		private static IEnumerable<SlotController> Movable(SlotController[] slots)
		{
			if (slots == null) yield break;
			foreach (SlotController sc in slots)
			{
				if (sc == null || sc.itemStack == null || sc.itemStack.itemId < 0 || sc.itemStack.itemAmount <= 0) continue;
				if (sc.itemStack.itemId == 14930) continue;   // the Backpack item stays where it is
				if (sc.itemStack.itemReference == null) sc.itemStack.SetItemReference();
				if (sc.itemStack.itemReference == null || sc.itemStack.itemReference.Item == null) continue;
				yield return sc;
			}
		}

		internal static int StationReturnCount(CraftingBase station)
		{
			int n = 0;
			try { foreach (SlotController sc in Movable(station.InputOutputStorage.Slots)) n++; } catch (Exception) { }
			return n;
		}

		internal static string ReturnStation(CraftingBase station)
		{
			var list = new List<SlotController>(Movable(station.InputOutputStorage.Slots));
			int moved = 0;
			foreach (SlotController sc in list)
			{
				ItemStack st = sc.itemStack;
				int k = ToBank(st.itemReference.Item, st.itemAmount, st.ownerId, st.Meta);
				if (k <= 0) continue;
				Shrink(sc, k);
				moved++;
			}
			Refresh(station.InputOutputStorage);
			try { station.CheckItems(true); } catch (Exception) { }
			return moved == 0 ? "Nothing could be moved to the bank." : "Moved " + moved + " stack" + (moved > 1 ? "s" : "") + " from the station to the bank.";
		}

		private static List<SlotController> InventoryMaterials(TaskItemBlueprint bp)
		{
			var list = new List<SlotController>();
			if (bp == null || bp.resourceItems == null || Inventory.instance == null) return list;
			var ids = new HashSet<int>();
			foreach (var res in bp.resourceItems) if (res != null && res.item != null && res.item.Item != null) ids.Add(res.item.ID);
			foreach (SlotController sc in Movable(Inventory.instance.Slots))
				if (ids.Contains(sc.itemStack.itemId) && (UseStolen.Value || sc.itemStack.ownerId <= 1)) list.Add(sc);
			return list;
		}

		internal static int BlueprintReturnCount(TaskItemBlueprint bp) { return InventoryMaterials(bp).Count; }

		internal static string ReturnBlueprint(TaskItemBlueprint bp)
		{
			int moved = 0;
			foreach (SlotController sc in InventoryMaterials(bp))
			{
				ItemStack st = sc.itemStack;
				int k = ToBank(st.itemReference.Item, st.itemAmount, st.ownerId, st.Meta);
				if (k <= 0) continue;
				Shrink(sc, k);
				moved++;
			}
			try { Inventory.instance.UpdateAllSlots(); } catch (Exception) { }
			return moved == 0 ? "You carry none of this blueprint's materials." : "Moved " + moved + " stack" + (moved > 1 ? "s" : "") + " of materials to the bank.";
		}

		// ---- Helpers ----

		internal static void Shrink(SlotController from, int moved)
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
			if (PlanStation(st, ShownRecipe, Amount, out still, out before).Count == 0) return;
			Notify(FillStation(st, ShownRecipe, Amount));
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

	/// <summary>The bank bar at the top of the screen while a station or workbench is open:
	/// amount (- / + / Max), "Take ingredients" and "Return to bank".</summary>
	internal class CraftFromBankUI : MonoBehaviour
	{
		private GUIStyle style, hint, box;
		private bool paperBuilt;
		private float nextCheck;
		private string takeLabel, returnLabel;
		private bool takeUsable, returnUsable;
		private int kind;   // 1 = station, 2 = workbench
		private int max;
		private object lastTarget;

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
			Recalc();
		}

		private void Recalc()
		{
			kind = 0; takeLabel = returnLabel = null; takeUsable = returnUsable = false;
			if (!CraftFromBank.Enabled.Value || BankStorage.Instance == null) return;
			try
			{
				int still, before;
				CraftingBase st = CraftFromBank.Station;
				if (st != null && CraftFromBank.ShownRecipe != null)
				{
					kind = 1;
					Recipe r = CraftFromBank.ShownRecipe;
					if (!ReferenceEquals(r, lastTarget)) { lastTarget = r; CraftFromBank.Amount = 1; }
					max = CraftFromBank.MaxStation(st, r);
					int n = CraftFromBank.Amount;
					var plan = CraftFromBank.PlanStation(st, r, n, out still, out before);
					takeUsable = plan.Count > 0;
					string what = (n > 1 ? n + "x " : "") + r.Name;
					takeLabel = before == 0 ? "Station has everything for " + what : takeUsable ? "Take ingredients for " + what + " (" + plan.Count + ")" : what + ": bank has none of the missing ingredients";
					int back = CraftFromBank.StationReturnCount(st);
					returnUsable = back > 0;
					returnLabel = "Station items to bank" + (back > 0 ? " (" + back + ")" : "");
				}
				else if (CraftFromBank.WorkbenchOpen && CraftFromBank.ShownBlueprint != null)
				{
					kind = 2;
					TaskItemBlueprint bp = CraftFromBank.ShownBlueprint;
					if (!ReferenceEquals(bp, lastTarget)) { lastTarget = bp; CraftFromBank.Amount = 1; }
					max = CraftFromBank.MaxBlueprint(bp);
					int n = CraftFromBank.Amount;
					var plan = CraftFromBank.PlanBlueprint(bp, n, out still, out before);
					takeUsable = plan.Count > 0;
					string what = n > 1 ? " for " + n + "x" : "";
					takeLabel = before == 0 ? "You carry the materials" + what : takeUsable ? "Take materials" + what + " from bank (" + plan.Count + ")" : "Bank has none of the missing materials";
					int back = CraftFromBank.BlueprintReturnCount(bp);
					returnUsable = back > 0;
					returnLabel = "Materials to bank" + (back > 0 ? " (" + back + ")" : "");
				}
			}
			catch (Exception) { kind = 0; }
		}

		private void Styles()
		{
			bool paper = BankSearch.PaperLook;
			if (style != null && style.normal.background != null && paper == paperBuilt) return;
			paperBuilt = paper;
			style = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold, padding = new RectOffset(12, 12, 7, 7), border = new RectOffset(2, 2, 2, 2) };
			style.normal.background = Tex(new Color(0.42f, 0.29f, 0.08f, 0.95f), new Color(0.79f, 0.6f, 0.23f));
			style.hover.background = Tex(new Color(0.6f, 0.42f, 0.12f, 0.95f), new Color(0.95f, 0.75f, 0.35f));
			style.active.background = Tex(new Color(0.79f, 0.6f, 0.23f, 0.95f), new Color(0.95f, 0.75f, 0.35f));
			style.normal.textColor = style.hover.textColor = new Color(0.98f, 0.93f, 0.8f);
			style.active.textColor = new Color(0.14f, 0.11f, 0.08f);
			hint = new GUIStyle(style);
			hint.normal.background = hint.hover.background = hint.active.background = Tex(new Color(0.1f, 0.09f, 0.08f, 0.85f), new Color(0.35f, 0.32f, 0.27f));
			hint.normal.textColor = hint.hover.textColor = hint.active.textColor = new Color(0.66f, 0.62f, 0.53f);
			box = new GUIStyle(hint) { alignment = TextAnchor.MiddleCenter };
			box.normal.textColor = box.hover.textColor = box.active.textColor = new Color(0.98f, 0.93f, 0.8f);
			if (paper)
			{
				// cardboard buttons with cream text; greyed hints and the amount on manila paper with ink
				style.normal.background = Tex(new Color32(92, 70, 44, 245), new Color32(66, 50, 32, 255));
				style.hover.background = Tex(new Color32(120, 92, 58, 245), new Color32(201, 160, 72, 255));
				style.active.background = Tex(new Color32(201, 160, 72, 245), new Color32(122, 92, 40, 255));
				style.normal.textColor = style.hover.textColor = new Color32(240, 228, 205, 255);
				hint.normal.background = hint.hover.background = hint.active.background = Tex(new Color32(214, 196, 152, 235), new Color32(150, 132, 96, 255));
				hint.normal.textColor = hint.hover.textColor = hint.active.textColor = new Color32(110, 96, 72, 255);
				box.normal.background = box.hover.background = box.active.background = Tex(new Color32(232, 220, 188, 245), new Color32(150, 132, 96, 255));
				box.normal.textColor = box.hover.textColor = box.active.textColor = new Color32(43, 36, 24, 255);
			}
		}

		private bool Btn(ref float x, float y, string text, bool usable, GUIStyle forced = null)
		{
			GUIStyle s = forced ?? (usable ? style : hint);
			Vector2 size = s.CalcSize(new GUIContent(text));
			bool hit = GUI.Button(new Rect(x, y, size.x, size.y), text, s) && usable;
			x += size.x + 6f;
			return hit;
		}

		private float Width(string text, GUIStyle s) { return s.CalcSize(new GUIContent(text)).x + 6f; }

		private void OnGUI()
		{
			if (kind == 0 || takeLabel == null || !Cursor.visible) return;
			Styles();
			int n = CraftFromBank.Amount;
			string amountText = "x" + n;
			string maxText = "Max " + max;
			float total = Width("-", style) + Width("x999", box) + Width("+", style) + Width(maxText, style)
			            + Width(takeLabel, takeUsable ? style : hint) + 10f + Width(returnLabel, returnUsable ? style : hint);
			float x = Mathf.Max(8f, (Screen.width - total) / 2f), y = 12f;
			int step = Event.current.shift ? 10 : 1;
			bool changed = false;
			if (Btn(ref x, y, "-", n > 1)) { CraftFromBank.Amount = Mathf.Max(1, n - step); changed = true; }
			Vector2 asz = box.CalcSize(new GUIContent("x999"));
			GUI.Label(new Rect(x, y, asz.x, asz.y), amountText, box); x += asz.x + 6f;
			if (Btn(ref x, y, "+", n < CraftFromBank.MaxAmount)) { CraftFromBank.Amount = Mathf.Min(CraftFromBank.MaxAmount, n + step); changed = true; }
			if (Btn(ref x, y, maxText, max > 0 && max != n)) { CraftFromBank.Amount = max; changed = true; }
			bool take = Btn(ref x, y, takeLabel, takeUsable);
			x += 10f;
			bool back = Btn(ref x, y, returnLabel, returnUsable);
			if (take || back)
			{
				try
				{
					string msg;
					if (take) msg = kind == 1 ? CraftFromBank.FillStation(CraftFromBank.Station, CraftFromBank.ShownRecipe, CraftFromBank.Amount)
					                          : CraftFromBank.FillBlueprint(CraftFromBank.ShownBlueprint, CraftFromBank.Amount);
					else msg = kind == 1 ? CraftFromBank.ReturnStation(CraftFromBank.Station) : CraftFromBank.ReturnBlueprint(CraftFromBank.ShownBlueprint);
					CraftFromBank.Notify(msg);
				}
				catch (Exception e) { CraftFromBank.Notify("Couldn't move the items: " + e.Message); }
				changed = true;
			}
			if (changed) Recalc();
		}
	}

	/// <summary>
	/// Tenement upgrades and renovations can use materials kept in the bank. The game looks in the tenement's
	/// resource storages and your inventory; the bank fills whatever those don't have, and is only taken from
	/// for that shortfall when the upgrade is paid.
	/// </summary>
	internal static class TenementFromBank
	{
		internal static ConfigEntry<bool> Enabled;
		private static ManualLogSource log;

		internal static void Init(ConfigFile cfg, ManualLogSource logger)
		{
			log = logger;
			Enabled = cfg.Bind("Tenement", "UseBankForUpgrades", true,
				"Tenement upgrades and renovations can use materials from the bank (after the tenement storages and your inventory).");
			try { new Harmony("com.juanberta.obenseuer.bankinventory.tenement").PatchAll(typeof(TenementFromBank.Hooks)); }
			catch (Exception e) { logger.LogWarning("Tenement from bank patches: " + e.Message); }
		}

		internal static int BankCount(int id)
		{
			int n = 0;
			foreach (SlotController sc in CraftFromBank.BankSlots())
				if (sc.itemStack.itemId == id) n += sc.itemStack.itemAmount;
			return n;
		}

		private static int TakeFromBank(int id, int amount)
		{
			int taken = 0;
			var stacks = new List<SlotController>();
			foreach (SlotController sc in CraftFromBank.BankSlots()) if (sc.itemStack.itemId == id) stacks.Add(sc);
			foreach (SlotController sc in stacks)
			{
				if (taken >= amount) break;
				int k = Mathf.Min(amount - taken, sc.itemStack.itemAmount);
				if (k <= 0) continue;
				CraftFromBank.Shrink(sc, k);
				taken += k;
			}
			return taken;
		}

		// What the game itself will find: the tenement resource storages plus the inventory.
		private static float VanillaHave(TenementController tc, int id)
		{
			float n = 0f;
			var storages = Traverse.Create(tc).Field("tenementResourceStorages").GetValue<List<Storage>>();
			if (storages != null) foreach (Storage s in storages) if (s != null) n += s.GetItemAmount(id);
			if (Inventory.instance != null) n += Inventory.instance.GetItemAmountInInventory(id);
			return n;
		}

		internal static class Hooks
		{
			[HarmonyPatch(typeof(TenementController), "GetItemAmount")]
			[HarmonyPostfix]
			[HarmonyPriority(Priority.Low)]
			private static void GetItemAmount_Postfix(int id, ref float __result)
			{
				if (!Enabled.Value) return;
				try { __result += BankCount(id); } catch (Exception) { }
			}

			[HarmonyPatch(typeof(TenementController), "CheckForItem")]
			[HarmonyPostfix]
			[HarmonyPriority(Priority.Low)]
			private static void CheckForItem_Postfix(TenementController __instance, int id, float amount, TenementContractor contractor, ref bool __result)
			{
				if (__result || !Enabled.Value) return;
				try
				{
					int bank = BankCount(id);
					if (bank <= 0) return;
					// The game said no: see whether the bank covers the rest.
					float have = contractor == null || contractor == Traverse.Create(__instance).Field("currentSceneContractor").GetValue<TenementContractor>()
						? VanillaHave(__instance, id)
						: __instance.GetItemAmount(id, contractor) - bank;   // remote tenement: the game's snapshot
					if (have + bank >= amount) __result = true;
				}
				catch (Exception) { }
			}

			// Runs after other mods' prefixes (Craft From Storage pays from nearby chests itself when they
			// have enough); here only the part the tenement storages + inventory can't cover comes from the bank.
			[HarmonyPatch(typeof(TenementController), "RemoveRequiredItems")]
			[HarmonyPrefix]
			[HarmonyPriority(Priority.Low)]
			private static void RemoveRequiredItems_Prefix(TenementController __instance, int id, ref int amount, bool __runOriginal)
			{
				if (!__runOriginal || !Enabled.Value || amount <= 0) return;
				try
				{
					int shortfall = amount - Mathf.FloorToInt(VanillaHave(__instance, id) + 0.0001f);
					if (shortfall <= 0) return;
					int taken = TakeFromBank(id, shortfall);
					if (taken <= 0) return;
					amount -= taken;
					if (log != null) log.LogInfo("Tenement upgrade: took " + taken + " x item " + id + " from the bank.");
				}
				catch (Exception e) { if (log != null) log.LogWarning("Tenement from bank: " + e.Message); }
			}
		}
	}
}
