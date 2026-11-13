using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OS.Items;
using UnityEngine;

namespace BankInventoryMod
{
	/// <summary>
	/// Search box for storages: while the bank is open, typing filters the bank to the matching stacks from
	/// every page (by item name, the liquid inside, or category). Any other open storage (backpack, crates,
	/// fridges...) gets the same box: matching slots are outlined and the rest are dimmed (display only, nothing
	/// moves). While the box has focus, game and mod hotkeys are muted so typing doesn't drop items, switch tabs
	/// or close the menu.
	/// </summary>
	internal class BankSearch : MonoBehaviour
	{
		private const string ControlName = "BankInventorySearch";

		internal static string Filter = "";
		internal static bool Typing;
		internal static bool Active { get { return InBank && !string.IsNullOrEmpty(Filter); } }   // the bank's filtered view
		internal static bool InBank;

		// Diegetic UI asks for its paper look through a shared flag (no dependency either way).
		internal static bool PaperLook
		{
			get { object f = System.AppDomain.CurrentDomain.GetData("Obenseuer.DiegeticTheme.BankInventory"); return f is bool && (bool)f; }
		}
		private bool paperBuilt;
		private string text = "";
		private GUIStyle field, placeholder, clear;
		private bool wasOpen;
		private bool bankMode;
		private object lastStorage;
		private int found;
		private float nextRefresh;
		private Texture2D dimTex, markTex;

		internal static void Init(GameObject host, Harmony harmony)
		{
			InputMute.Patch(harmony);
			host.AddComponent<BankSearch>();
		}

		internal static bool Matches(ItemStack st)
		{
			if (st == null || st.itemId < 0 || st.itemAmount <= 0) return false;
			try
			{
				if (st.itemReference == null) st.SetItemReference();
				Item item = st.itemReference != null ? st.itemReference.Item : null;
				if (item == null) return false;
				if (Has(item.Title)) return true;
				ItemLiquidData liquid = st.GetMetaOfType<ItemLiquidData>();
				if (liquid != null && liquid.liquidItem != null && liquid.liquidItem.Item != null && Has(liquid.liquidItem.Item.Title)) return true;
				if (item.Categories != null && item.Categories.Any(Has)) return true;
			}
			catch (Exception) { }
			return false;
		}

		private static bool Has(string s) { return !string.IsNullOrEmpty(s) && s.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0; }

		private static BankInventoryPlugin Plugin { get { return BankInventoryPlugin.Instance; } }

		private void Update()
		{
			bool bank = Plugin != null && Plugin.BankOpen && Cursor.visible;
			bool storage = !bank && Cursor.visible && StorageShowing();
			bool open = bank || storage;
			object current = open ? CurrentStorage() : null;
			// a new storage (or closing the menu) starts with an empty box
			if ((!open && wasOpen) || bank != bankMode || (open && wasOpen && current != lastStorage)) { text = ""; Filter = ""; Typing = false; }
			wasOpen = open;
			bankMode = bank;
			InBank = bank;
			lastStorage = current;
			if (!open) { Typing = false; return; }
			if (!bank) return;
			// Keep the filtered view current as items are taken out or put in.
			if (Active && Time.unscaledTime >= nextRefresh)
			{
				nextRefresh = Time.unscaledTime + 0.25f;
				Plugin.RefreshView();
			}
		}

		private static object CurrentStorage()
		{
			try { return Traverse.Create(Inventory.instance.storagePanelUI).Field("currentStorage").GetValue(); } catch (Exception) { return null; }
		}

		// A storage panel is on screen (the menu is open on the Inventory page with a storage beside it).
		private static bool StorageShowing()
		{
			try
			{
				if (GameUIController.instance == null || !GameUIController.instance.IsMenuVisible) return false;
				StoragePanelUI ui = Inventory.instance != null ? Inventory.instance.storagePanelUI : null;
				if (ui == null || ui.storageSlotPanel == null || !ui.storageSlotPanel.activeInHierarchy) return false;
				for (Transform p = ui.transform; p != null; p = p.parent)
				{
					foreach (CanvasGroup cg in p.GetComponents<CanvasGroup>())
						if (cg.enabled && cg.alpha < 0.05f) return false;
					Canvas c = p.GetComponent<Canvas>();
					if (c != null && !c.enabled) return false;
				}
				return CurrentStorage() != null;
			}
			catch (Exception) { return false; }
		}

		private static Rect PanelRect() { return ScreenRect(Inventory.instance.storagePanelUI.transform as RectTransform); }

		private static Rect ScreenRect(RectTransform rt)
		{
			var corners = new Vector3[4];
			rt.GetWorldCorners(corners);
			Canvas canvas = rt.GetComponentInParent<Canvas>();
			Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
			Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
			Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
			return new Rect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
		}

		private static Texture2D Tex(Color fill, Color edge)
		{
			var t = new Texture2D(8, 8) { hideFlags = HideFlags.HideAndDontSave };
			for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) t.SetPixel(x, y, x == 0 || y == 0 || x == 7 || y == 7 ? edge : fill);
			t.Apply();
			return t;
		}

		private void Styles()
		{
			bool paper = PaperLook;
			if (field != null && field.normal.background != null && paper == paperBuilt) return;
			paperBuilt = paper;
			field = new GUIStyle(GUI.skin.textField) { fontSize = 15, padding = new RectOffset(10, 30, 6, 6), border = new RectOffset(2, 2, 2, 2) };
			field.normal.background = field.hover.background = Tex(new Color(0.1f, 0.09f, 0.08f, 0.92f), new Color(0.55f, 0.42f, 0.17f));
			field.focused.background = Tex(new Color(0.13f, 0.11f, 0.09f, 0.96f), new Color(0.95f, 0.75f, 0.35f));
			field.normal.textColor = field.hover.textColor = field.focused.textColor = new Color(0.98f, 0.93f, 0.8f);
			placeholder = new GUIStyle(field);
			placeholder.normal.background = null;
			placeholder.normal.textColor = new Color(0.62f, 0.57f, 0.48f);
			placeholder.fontStyle = FontStyle.Italic;
			clear = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
			clear.normal.textColor = clear.hover.textColor = new Color(0.79f, 0.6f, 0.23f);
			if (paper)
			{
				// a receipt-white slip with ink text
				field.normal.background = field.hover.background = Tex(new Color32(241, 238, 230, 245), new Color32(84, 72, 52, 255));
				field.focused.background = Tex(new Color32(250, 248, 242, 250), new Color32(122, 43, 28, 255));
				field.normal.textColor = field.hover.textColor = field.focused.textColor = new Color32(43, 36, 24, 255);
				placeholder.normal.textColor = new Color32(120, 108, 86, 255);
				clear.normal.textColor = clear.hover.textColor = new Color32(122, 43, 28, 255);
			}
		}

		// Outline the matching slots of the open storage and dim the rest.
		private void Dim()
		{
			if (dimTex == null) { dimTex = Tex(new Color(0f, 0f, 0f, 0.72f), new Color(0f, 0f, 0f, 0.72f)); }
			bool paper = PaperLook;
			Color mark = paper ? new Color32(122, 43, 28, 255) : new Color32(242, 191, 89, 255);
			if (markTex == null) { markTex = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave }; markTex.SetPixel(0, 0, Color.white); markTex.Apply(); }
			found = 0;
			Slot[] slots;
			try { slots = Inventory.instance.storagePanelUI.storageSlotPanel.GetComponentsInChildren<Slot>(false); } catch (Exception) { return; }
			Color keep = GUI.color;
			foreach (Slot slot in slots)
			{
				if (slot == null) continue;
				Rect r;
				try { r = ScreenRect(slot.transform as RectTransform); } catch (Exception) { continue; }
				// the slot's controller holds the real stack (the item widget's own copy can be empty)
				ItemStack st = null;
				try { st = slot.slotcontrol != null ? slot.slotcontrol.itemStack : null; } catch (Exception) { }
				if (st == null || st.itemId < 0)
					try { ItemData data = slot.GetItemItemData(); if (data != null) st = data.stack; } catch (Exception) { }
				if (Matches(st))
				{
					found++;
					GUI.color = mark;
					GUI.DrawTexture(new Rect(r.x, r.y, r.width, 3f), markTex);
					GUI.DrawTexture(new Rect(r.x, r.yMax - 3f, r.width, 3f), markTex);
					GUI.DrawTexture(new Rect(r.x, r.y, 3f, r.height), markTex);
					GUI.DrawTexture(new Rect(r.xMax - 3f, r.y, 3f, r.height), markTex);
					GUI.color = keep;
				}
				else GUI.DrawTexture(r, dimTex);
			}
			GUI.color = keep;
		}

		private void OnGUI()
		{
			if (!wasOpen || Inventory.instance == null || Inventory.instance.storagePanelUI == null) return;
			Styles();
			Rect panel;
			try { panel = PanelRect(); } catch (Exception) { return; }
			float w = Mathf.Clamp(panel.width - 20f, 220f, 520f);
			var box = new Rect(panel.x + (panel.width - w) / 2f, 12f, w, 32f);
			Event e = Event.current;

			bool focused = GUI.GetNameOfFocusedControl() == ControlName;
			if (focused && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
			{
				if (e.keyCode == KeyCode.Escape) text = "";
				GUI.FocusControl(null);
				e.Use();
			}
			if (e.type == EventType.MouseDown && !box.Contains(e.mousePosition) && focused) GUI.FocusControl(null);

			GUI.SetNextControlName(ControlName);
			text = GUI.TextField(box, text ?? "", 60, field);
			if (string.IsNullOrEmpty(text) && GUI.GetNameOfFocusedControl() != ControlName)
				GUI.Label(box, bankMode ? "Search the bank (all pages)..." : "Search this storage...", placeholder);
			if (!bankMode && !string.IsNullOrEmpty(Filter))
			{
				if (e.type == EventType.Repaint) Dim();
				GUI.Label(new Rect(box.xMax - 128f, box.y, 96f, box.height), found + " found", placeholder);
			}
			if (!string.IsNullOrEmpty(text) && GUI.Button(new Rect(box.xMax - 28f, box.y, 28f, box.height), "x", clear))
			{
				text = "";
				GUI.FocusControl(null);
			}

			Typing = GUI.GetNameOfFocusedControl() == ControlName;
			string f = (text ?? "").Trim();
			if (f != Filter)
			{
				Filter = f;
				if (bankMode && Plugin != null) Plugin.RefreshView();
			}
		}
	}

	/// <summary>While the bank search box has focus, game key bindings and raw key reads report nothing.</summary>
	internal static class InputMute
	{
		private static readonly string[] ManagerNames = { "IsKeyDown", "IsKeyUp", "IsKey", "GetKeyDown", "GetKeyDownKeyboardOnly", "GetKeyUp", "GetKey", "GetKeyWithHold", "GetAuxiliaryReloadDown" };

		internal static void Patch(Harmony harmony)
		{
			var prefix = new HarmonyMethod(typeof(InputMute), nameof(BoolPrefix));
			foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(InputManager)).Where(m => ManagerNames.Contains(m.Name) && m.ReturnType == typeof(bool)))
			{
				try { harmony.Patch(m, prefix: prefix); } catch (Exception) { }
			}
			foreach (string n in new[] { "GetKeyDown", "GetKeyUp", "GetKey" })
				foreach (Type arg in new[] { typeof(KeyCode), typeof(string) })
				{
					MethodInfo m = AccessTools.Method(typeof(Input), n, new[] { arg });
					if (m == null) continue;
					try { harmony.Patch(m, prefix: prefix); } catch (Exception) { }
				}
		}

		private static bool BoolPrefix(ref bool __result)
		{
			if (!BankSearch.Typing && !CraftFromBankUI.EditingAmount) return true;
			__result = false;
			return false;
		}
	}
}
