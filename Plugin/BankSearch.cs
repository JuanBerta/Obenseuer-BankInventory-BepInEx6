using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OS.Items;
using UnityEngine;

namespace BankInventoryMod
{
	/// <summary>
	/// Search box for the bank: while the bank is open, typing filters the bank to the matching stacks from
	/// every page (by item name, the liquid inside, or category). While the box has focus, game and mod
	/// hotkeys are muted so typing doesn't drop items, switch tabs or close the bank.
	/// </summary>
	internal class BankSearch : MonoBehaviour
	{
		private const string ControlName = "BankInventorySearch";

		internal static string Filter = "";
		internal static bool Typing;
		internal static bool Active { get { return !string.IsNullOrEmpty(Filter); } }

		private string text = "";
		private GUIStyle field, placeholder, clear;
		private bool wasOpen;
		private float nextRefresh;

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
			bool open = Plugin != null && Plugin.BankOpen && Cursor.visible;
			if (!open && wasOpen) { text = ""; Filter = ""; Typing = false; }
			wasOpen = open;
			if (!open) { Typing = false; return; }
			// Keep the filtered view current as items are taken out or put in.
			if (Active && Time.unscaledTime >= nextRefresh)
			{
				nextRefresh = Time.unscaledTime + 0.25f;
				Plugin.RefreshView();
			}
		}

		private static Rect PanelRect()
		{
			var rt = Inventory.instance.storagePanelUI.transform as RectTransform;
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
			if (field != null && field.normal.background != null) return;
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
				GUI.Label(box, "Search the bank (all pages)...", placeholder);
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
				if (Plugin != null) Plugin.RefreshView();
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
			if (!BankSearch.Typing) return true;
			__result = false;
			return false;
		}
	}
}
