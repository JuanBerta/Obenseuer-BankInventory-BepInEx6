using System.Reflection;
using BepInEx;
using BepInEx.Unity.Mono;
using BepInEx.Unity.Mono.Configuration;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BankInventoryMod
{
	[BepInPlugin("com.juanberta.obenseuer.bankinventory", "Obenseuer Bank Inventory", "1.0.0")]
	public class BankInventoryPlugin : BaseUnityPlugin
	{
		private BankStorage bank;
		private bool bankOpen;

		private ConfigEntry<KeyboardShortcut> openKey;
		private ConfigEntry<KeyboardShortcut> nextPageKey;
		private ConfigEntry<KeyboardShortcut> prevPageKey;
		private ConfigEntry<int> baseCapacity;
		private ConfigEntry<int> pageSize;

		private static BankInventoryPlugin _instance;
		public static BankInventoryPlugin Instance { get { return _instance; } }

		private void Awake()
		{
			_instance = this;
			openKey = Config.Bind("General", "OpenBankKey", new KeyboardShortcut(KeyCode.O),
				"Key that opens/closes the bank inventory. Default O because B is used by the game for Head Lamp Use.");
			nextPageKey = Config.Bind("General", "NextPageKey", new KeyboardShortcut(KeyCode.RightBracket),
				"Key to go to the next bank page.");
			prevPageKey = Config.Bind("General", "PreviousPageKey", new KeyboardShortcut(KeyCode.LeftBracket),
				"Key to go to the previous bank page.");
			baseCapacity = Config.Bind("Storage", "BaseCapacity", 30,
				"Initial number of slots. The bank expands automatically when it becomes full.");
			pageSize = Config.Bind("Storage", "PageSize", 88,
				"How many slots are shown per page (the game UI grid supports 88 slots).");

			SceneManager.sceneLoaded += OnSceneLoaded;
			EnsureBankExists();

			// Harmony patches:
			//  1) Storage.TakeAll() empties the bank data but never refreshes the page
			//     state or the slot UIs, which leaves ghost icons ("overflow") and a
			//     ForeignSlots/page desync. Postfix re-syncs everything.
			//  2) MoveToOtherInventory() silently returns false when an item stack has a
			//     null ItemReference (SlotController.cs:666); Take All can leave some
			//     backpack stacks in exactly that state, so "put back" fails until Sort
			//     rebuilds every reference. Prefix rebuilds the reference up-front.
			try
			{
				Harmony harmony = new Harmony("com.juanberta.obenseuer.bankinventory.takeallfix");
				harmony.Patch(AccessTools.Method(typeof(Storage), "TakeAll"),
					postfix: new HarmonyMethod(typeof(BankPatches), nameof(BankPatches.StorageTakeAllPostfix)));
				harmony.Patch(AccessTools.Method(typeof(SlotController), "MoveToOtherInventory",
						new System.Type[] { typeof(SlotController), typeof(int), typeof(int).MakeByRefType() }),
					prefix: new HarmonyMethod(typeof(BankPatches), nameof(BankPatches.MoveToOtherInventoryPrefix)));
			}
			catch (System.Exception e)
			{
				Logger.LogError("Failed to install BankInventory harmony patches: " + e);
			}
		}

		private void OnDestroy()
		{
			SceneManager.sceneLoaded -= OnSceneLoaded;
		}

		private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
		{
			EnsureBankExists();
		}

		/// <summary>The bank object is created once and kept alive across scenes through
		/// DontDestroyOnLoad + the save system's DontDestroyOnLoadObjects list (its data is
		/// serialized through the special save hooks into Globals.tnmt).</summary>
		private void EnsureBankExists()
		{
			if (bank == null)
			{
				GameObject go = new GameObject("BankInventory_Storage");
				go.SetActive(false); // keep the game's Storage.Start() away until the world exists
				bank = go.AddComponent<BankStorage>();
				bank.Setup(baseCapacity.Value, pageSize.Value);
				Object.DontDestroyOnLoad(go);
				SaveController.DontDestroyOnLoadObjects.Add(go);
			}

			if (!bank.gameObject.activeSelf && Inventory.instance != null)
			{
				bank.gameObject.SetActive(true);
			}
		}

		private void Update()
		{
			EnsureBankExists();

			if (Inventory.instance == null || GameUIController.instance == null)
				return;

			if (openKey.Value.IsDown())
			{
				if (!bankOpen)
					TryOpenBank();
				else
					CloseBank();
				return;
			}

			if (!bankOpen)
				return;

			// Page navigation.
			DetectPageKeys();

			// Infinite storage: if every slot is occupied, grow by one page immediately.
			// This also guarantees the player can never "drop" items on the floor when
			// dragging them into a full bank (there is always at least one free slot).
			if (bank.IsStorageFull())
			{
				bank.ExpandByPage(pageSize.Value);
				BindAllSlots();

				// ExpandByPage() replaces the Slots array (Array.Resize). The game keeps
				// rendering/interacting through Inventory.instance.ForeignSlots, which
				// still points at the pre-expansion array here; re-point it at the new
				// array so UI, F-key moves and storage navigation see the fresh slots.
				if (Inventory.instance != null)
					Inventory.instance.ForeignSlots = bank.Slots;

				// Jump to the newly added page so the player immediately sees the fresh
				// empty slots. Otherwise items dragged into the full page land in hidden
				// slots with no visual feedback, which looks like "bank is full".
				bank.CurrentPage = bank.TotalPages - 1;
				ApplyPageVisibility();
				UpdateTitle();
			}

			// If the game closed the storage panel by itself (Esc, another UI flow...),
			// release our UI bindings too.
			if (Inventory.instance.storagePanelUI != null &&
			    !Inventory.instance.storagePanelUI.gameObject.activeInHierarchy)
			{
				CloseBank();
			}
		}

		private bool TryOpenBank()
		{
			if (GameUIController.instance.IsAnyMenuOpen())
				return false; // don't hijack other open menus (character, map, ...)

			if (Inventory.instance == null ||
			    Inventory.instance.inventoryPanelUI == null ||
			    Inventory.instance.storagePanelUI == null)
				return false;

			bankOpen = true;
			bank.CurrentPage = 0;
			bank.title = "Bank Inventory";

			GameUIController.instance.ShowGameMenu(false, true, MenuPanels.Storage, MenuPageNames.Inventory);
			Inventory.instance.ForeignSlots = bank.Slots;

			// Create the slot UIs once, then show only the current page.
			BindAllSlots();
			ApplyPageVisibility();
			UpdateTitle();
			EnsureQuickStackButton();
			return true;
		}

		private void CloseBank()
		{
			if (!bankOpen)
				return;

			bankOpen = false;
			UnbindAllSlots();

			if (Inventory.instance != null && Inventory.instance.ForeignSlots == bank.Slots)
				Inventory.instance.ForeignSlots = null;

			if (GameUIController.instance != null)
				GameUIController.instance.HideGameMenu();
		}

		private void ChangePage(int delta)
		{
			int total = bank.TotalPages;
			if (total <= 1)
				return;
			bank.CurrentPage = (bank.CurrentPage + delta + total) % total;
			// Keep the game's foreign-slot reference aligned with the current array
			// (safety net; normally it is already the latest bank.Slots).
			if (Inventory.instance != null)
				Inventory.instance.ForeignSlots = bank.Slots;
			ApplyPageVisibility();
			UpdateTitle();
		}

		/// <summary>Detect the page-turn keys. Uses both the configured BepInEx
		/// KeyboardShortcut and a character-based check on Input.inputString: on
		/// non-US keyboard layouts (e.g. Spanish) '[' and ']' require AltGr chords
		/// and Unity reports different KeyCodes, so KeyCode.LeftBracket/RightBracket
		/// shortcuts never fire even though the right characters are typed.</summary>
		private void DetectPageKeys()
		{
			bool next = nextPageKey.Value.IsDown();
			bool prev = prevPageKey.Value.IsDown();

			string input = UnityEngine.Input.inputString;
			for (int i = 0; i < input.Length; i++)
			{
				if (input[i] == ']')
					next = true;
				else if (input[i] == '[')
					prev = true;
			}

			if (next)
				ChangePage(1);
			if (prev)
				ChangePage(-1);
		}

		/// <summary>Instantiate a slot UI for every slot that does not have one yet.</summary>
		private void BindAllSlots()
		{
			if (Inventory.instance == null || Inventory.instance.storagePanelUI == null)
				return;

			Transform panel = Inventory.instance.storagePanelUI.storageSlotPanel.transform;
			for (int i = 0; i < bank.Slots.Length; i++)
			{
				SlotController sc = bank.Slots[i];
				if (sc.slot == null)
				{
					sc.SetAllowedCategories(bank.AllowedCategories);
					sc.SetForbiddenCategories(bank.ForbiddenCategories);
					sc.CaptureSlot(panel);
				}

				// The bank slot UI is instantiated from the player inventory prefab,
				// which may carry playerSlot=true. The game decides "cross storage"
				// dragging through Slot.playerSlot (Slot.IsOtherStorage), so bank slots
				// must always be marked as non-player slots.
				if (sc.slot != null)
					sc.slot.playerSlot = false;
			}
		}

		/// <summary>Show only the slots belonging to the current page, hide the rest.
		/// Hidden slots keep their UI objects (so "Take All" and other game code that
		/// touches every slot never hits a null slot) but are not laid out by the grid.</summary>
		private void ApplyPageVisibility()
		{
			int start = bank.GetPageStart(bank.CurrentPage);
			int end = bank.GetPageEnd(bank.CurrentPage);

			for (int i = 0; i < bank.Slots.Length; i++)
			{
				SlotController sc = bank.Slots[i];
				if (sc != null && sc.slot != null && sc.slot.gameObject != null)
					sc.slot.gameObject.SetActive(i >= start && i < end);
			}
		}

		/// <summary>Destroy all bank slot UIs (used when closing the bank).</summary>
		private void UnbindAllSlots()
		{
			if (bank == null)
				return;

			for (int i = 0; i < bank.Slots.Length; i++)
			{
				SlotController sc = bank.Slots[i];
				if (sc != null)
				{
					if (sc.slot != null)
					{
						sc.RelaseSlot(destroy: true);
						sc.slot = null; // clear so a later CaptureSlot re-instantiates a fresh UI
					}
					sc.ResetAllowedCategories();
					sc.ResetForbiddenCategories();
				}
			}
		}

		/// <summary>Refresh the panel header with the current page info.</summary>
		private void UpdateTitle()
		{
			int total = bank.TotalPages;
			bank.title = "Bank Inventory \u2014 Page " + (bank.CurrentPage + 1) + "/" + total;

			if (Inventory.instance != null && Inventory.instance.storagePanelUI != null)
				Inventory.instance.storagePanelUI.UpdateStoragePanelUI(bank);
		}

		/// <summary>Called by the Harmony postfix after the game's Take All empties the
		/// bank: restore page visibility and the header title.</summary>
		public void RefreshAfterTakeAll(BankStorage bank)
		{
			ApplyPageVisibility();
			UpdateTitle();
		}

		private const string QuickStackButtonName = "BankInventory_QuickStackButton";

		/// <summary>Create (once) the Quick Stack button on the storage panel header,
		/// next to the existing Sort / Take All buttons, and keep its visibility in
		/// sync with the template button.</summary>
		private void EnsureQuickStackButton()
		{
			if (Inventory.instance == null || Inventory.instance.storagePanelUI == null)
				return;

			StoragePanelUI ui = Inventory.instance.storagePanelUI;
			Transform root = ui.transform;
			Transform existing = FindDeep(root, QuickStackButtonName);
			if (existing != null)
			{
				// TradePanel flows hide sort/takeAll; keep our button in the same state.
				if (ui.takeAllButton != null)
					existing.gameObject.SetActive(ui.takeAllButton.activeSelf);
				return;
			}

			GameObject template = ui.takeAllButton != null ? ui.takeAllButton : ui.sortButton;
			if (template == null)
				return;

			GameObject btn = Instantiate(template);
			btn.name = QuickStackButtonName;
			btn.SetActive(true);

			Transform parent = template.transform.parent;
			if (parent != null)
				btn.transform.SetParent(parent, false);
			else
				btn.transform.SetParent(root, false);

			int sibling = template.transform.GetSiblingIndex() + 1;
			if (btn.transform.parent != null && sibling < btn.transform.parent.childCount)
				btn.transform.SetSiblingIndex(sibling);

			SetButtonText(btn, "QUICK STACK");

			// Bind the click event through reflection so the mod does not depend on the
			// UnityEngine.UI Button member signature (works across Unity UI versions).
			Component btnComp = btn.GetComponent("Button");
			if (btnComp == null)
			{
				Component[] comps = btn.GetComponentsInChildren<Component>(true);
				for (int i = 0; i < comps.Length; i++)
				{
					if (comps[i].GetType().Name == "Button")
					{
						btnComp = comps[i];
						break;
					}
				}
			}
			if (btnComp != null)
			{
				PropertyInfo prop = btnComp.GetType().GetProperty("onClick");
				if (prop != null)
				{
					object evt = prop.GetValue(btnComp, null);
					MethodInfo removeAll = evt.GetType().GetMethod("RemoveAllListeners", System.Type.EmptyTypes);
					if (removeAll != null)
						removeAll.Invoke(evt, null);
					MethodInfo add = evt.GetType().GetMethod("AddListener",
						new System.Type[] { typeof(UnityEngine.Events.UnityAction) });
					if (add != null)
						add.Invoke(evt, new object[] { (UnityEngine.Events.UnityAction)QuickStack });
				}
			}
		}

		/// <summary>Quick Stack: move identical (same item id) stacks from the player
		/// inventory into the currently open storage, filling existing stacks up to the
		/// item's stack limit. Items with no matching stack in the storage stay put.</summary>
		private void QuickStack()
		{
			if (Inventory.instance == null)
				return;

			SlotController[] foreign = Inventory.instance.ForeignSlots;
			if (foreign == null || foreign.Length == 0)
				return;

			SlotController[] player = Inventory.instance.AllSlots();
			bool changed = false;

			for (int p = 0; p < player.Length; p++)
			{
				SlotController ps = player[p];
				if (ps == null)
					continue;

				ItemStack pstack = ps.itemStack;
				if (pstack == null || pstack.itemId == -1 || pstack.itemAmount <= 0)
					continue;

				for (int f = 0; f < foreign.Length; f++)
				{
					SlotController ts = foreign[f];
					if (ts == null || ts == ps)
						continue;

					ItemStack tstack = ts.itemStack;
					if (tstack == null || tstack.itemId != pstack.itemId || tstack.itemAmount <= 0)
						continue;

					Item item = tstack.itemReference != null ? tstack.itemReference.Item : null;
					if (item == null)
						continue;

					int limit = item.Stackable == 0 ? 1 : item.Stackable;
					int space = limit - tstack.itemAmount;
					if (space <= 0)
						continue;

					int move = Mathf.Min(space, pstack.itemAmount);
					int moved;
					if (ps.MoveToOtherInventory(ts, move, out moved) && moved > 0)
					{
						changed = true;
						if (pstack.itemAmount <= 0)
							break;
					}
				}
			}

			if (changed)
			{
				// Refresh both inventories so the moved amounts are shown immediately.
				Inventory.instance.UpdateAllSlotsNow();
				if (bank != null && bankOpen)
					UpdateTitle();
			}
		}

		/// <summary>Recursive transform lookup by name.</summary>
		private static Transform FindDeep(Transform root, string name)
		{
			if (root == null)
				return null;
			if (root.name == name)
				return root;
			for (int i = 0; i < root.childCount; i++)
			{
				Transform found = FindDeep(root.GetChild(i), name);
				if (found != null)
					return found;
			}
			return null;
		}

		/// <summary>Set a UI text label without depending on the TextMeshPro assembly.</summary>
		private static void SetButtonText(GameObject go, string text)
		{
			if (go == null)
				return;
			Component[] comps = go.GetComponentsInChildren<Component>(true);
			for (int i = 0; i < comps.Length; i++)
			{
				System.Type t = comps[i].GetType();
				if (t.Name == "TextMeshProUGUI" || t.Name == "Text")
				{
					PropertyInfo prop = t.GetProperty("text");
					if (prop != null)
					{
						prop.SetValue(comps[i], text, null);
						break;
					}
				}
			}
		}
	}

	/// <summary>Harmony patches that fix the "Take All" page/UI desync and the
	/// "put back fails until Sort is pressed" item-reference issue.</summary>
	internal static class BankPatches
	{
		/// <summary>After the game's Storage.TakeAll() empties the bank, re-sync the
		/// page state, the ForeignSlots binding and every slot UI (the game only clears
		/// item stacks and never refreshes the visible slots, leaving ghost icons and an
		/// "overflow" look; hidden-page slots also keep stale icons until touched again).</summary>
		internal static void StorageTakeAllPostfix(Storage __instance)
		{
			BankStorage bank = __instance as BankStorage;
			if (bank == null || Inventory.instance == null)
				return;

			// Keep the game's foreign-slot reference aligned with the current array.
			Inventory.instance.ForeignSlots = bank.Slots;

			// Safety clamp of the page pointer (never leave an out-of-range page).
			if (bank.CurrentPage < 0 || bank.CurrentPage >= bank.TotalPages)
				bank.CurrentPage = 0;

			// Refresh the header (page counter).
			if (Inventory.instance.storagePanelUI != null)
				Inventory.instance.storagePanelUI.UpdateStoragePanelUI(bank);

			// Force every slot UI to rebuild from its (now empty) stack. TakeAll calls
			// RemoveItem() and returns before UpdateSlot when a stack is fully moved, so
			// ghost icons would remain without this explicit refresh.
			for (int i = 0; i < bank.Slots.Length; i++)
			{
				SlotController sc = bank.Slots[i];
				if (sc != null && sc.slot != null)
					sc.slot.UpdateSlot();
			}

			// Re-apply page visibility so only the current page stays active.
			if (BankInventoryPlugin.Instance != null)
				BankInventoryPlugin.Instance.RefreshAfterTakeAll(bank);
		}

		/// <summary>Before any move-into-storage operation, rebuild a null item
		/// reference. TakeAll leaves some backpack stacks without an ItemReference and
		/// MoveToOtherInventory() then silently fails (SlotController.cs:666). Sort
		/// "fixes" it by re-adding every item; this patch fixes the root cause instead.</summary>
		internal static void MoveToOtherInventoryPrefix(SlotController __instance)
		{
			ItemStack stack = __instance.itemStack;
			if (stack != null && stack.itemId > -1 && stack.itemAmount > 0 && stack.itemReference == null)
			{
				stack.SetItemReference();
			}
		}
	}
}
