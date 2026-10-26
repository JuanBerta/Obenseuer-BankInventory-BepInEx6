using BepInEx;
using BepInEx.Unity.Mono;
using BepInEx.Unity.Mono.Configuration;
using BepInEx.Configuration;
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

		private void Awake()
		{
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
			if (nextPageKey.Value.IsDown())
				ChangePage(1);
			if (prevPageKey.Value.IsDown())
				ChangePage(-1);

			// Infinite storage: if every slot is occupied, grow by one page immediately.
			// This also guarantees the player can never "drop" items on the floor when
			// dragging them into a full bank (there is always at least one free slot).
			if (bank.IsStorageFull())
			{
				bank.ExpandByPage(pageSize.Value);
				BindAllSlots();
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
			ApplyPageVisibility();
			UpdateTitle();
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
	}
}
