using System;
using System.Reflection;
using OS.Items;
using UnityEngine;

namespace BankInventoryMod
{
	/// <summary>
	/// The bank's persistent storage. Extends the game's <see cref="Storage"/> so it can be
	/// serialized by the game's save system (global saves -> Globals.tnmt).
	///
	/// Infinite storage design:
	/// - Starts with a configurable base capacity.
	/// - Whenever the storage is completely full it automatically expands by one page.
	/// - The UI only shows one page at a time; page navigation is handled by the plugin.
	/// - All slots get UI created on open (so game logic such as "Take All" that touches
	///   every slot stays safe); page switching only toggles visibility.
	/// </summary>
	public class BankStorage : Storage
	{
		public const string BankGUID = "BankInventory_Storage_001";

		private static BankStorage _instance;
		public static BankStorage Instance { get { return _instance; } }

		// Mod-only state, kept private so FullSerializer (which persists the game's public
		// fields of Storage) never stores them into the save file.
		private int _currentPage;

		public int CurrentPage
		{
			get { return _currentPage; }
			set { _currentPage = value; }
		}

		public int TotalPages
		{
			get { return Mathf.Max(1, Mathf.CeilToInt((float)Slots.Length / PageSize)); }
		}

		public int PageSize { get; set; }

		/// <summary>Create the bank object. The object is inactive until the plugin activates
		/// it once the game world (Inventory.instance) is available, so the game's Storage.Start()
		/// can safely initialize.</summary>
		public static BankStorage Create(int baseCapacity, int pageSize)
		{
			GameObject go = new GameObject("BankInventory_Storage");
			BankStorage bank = go.AddComponent<BankStorage>();
			bank.Setup(baseCapacity, pageSize);
			_instance = bank;
			return bank;
		}

		public void Setup(int baseCapacity, int pageSize)
		{
			GUID = BankGUID;
			global = true; // persist into Globals.tnmt (global save), independent of level
			title = "Bank Inventory";
			PageSize = Mathf.Max(1, pageSize);
			int cap = Mathf.Max(1, baseCapacity);

			storageSlots = new StorageSlot[cap];
			for (int i = 0; i < storageSlots.Length; i++)
				storageSlots[i] = new StorageSlot();

			RebuildSlots();

			// Give the base class a concrete (non-null) spawn settings so the coroutines the
			// game starts from Storage.Start() don't crash on null spawnSettings.
			spawnSettings = new SpawnSettings();

			// Storage.Initialize() reads startItems.Length without a null check. On dynamically
			// created objects (AddComponent) the [SerializeField] private ItemStack[] startItems
			// stays null, which makes the coroutine throw a NullReferenceException right after
			// the scene finishes loading (Storage+<Initialize>d__86.MoveNext()). Give it a
			// concrete empty array via reflection (the field is private on the base class).
			FieldInfo startItemsField = typeof(Storage).GetField("startItems", BindingFlags.NonPublic | BindingFlags.Instance);
			if (startItemsField != null)
			{
				startItemsField.SetValue(this, new ItemStack[0]);
			}
		}

		/// <summary>Save support: the bank object lives in the DontDestroyOnLoad scene, so the
		/// game serializes it through the "special" save hooks instead of the level-root scan.</summary>
		public override bool OnSavingGameSpecial()
		{
			return SaveController.SerializeData(this, global);
		}

		/// <summary>Load support: restore the bank from the global save data, then rebuild the
		/// slot controllers that mirror storageSlots.</summary>
		public override bool OnLoadingGameSpecial()
		{
			bool ok = SaveController.DeSerializeData(this, global);
			if (ok)
			{
				RebuildSlots();
			}
			return ok;
		}

		/// <summary>Rebuild SlotController instances from the current storageSlots array.
		/// Used after loading a save and during initial setup.</summary>
		public void RebuildSlots()
		{
			Slots = new SlotController[storageSlots.Length];
			for (int i = 0; i < Slots.Length; i++)
			{
				SlotController sc = new SlotController();
				sc.Siblings = Slots;
				sc.itemStack = storageSlots[i];
				if (sc.itemStack != null)
					sc.itemStack.SetItemReference();
				Slots[i] = sc;
			}
		}

		/// <summary>Expand the storage by one page. Existing SlotController instances are kept
		/// (their UI bindings stay valid) and every controller's Siblings array is refreshed so
		/// item stacks can spread into the newly added slots.</summary>
		public void ExpandByPage(int extraSlots)
		{
			if (extraSlots <= 0)
				return;

			int oldLen = Slots.Length;

			Array.Resize(ref storageSlots, oldLen + extraSlots);
			for (int i = oldLen; i < storageSlots.Length; i++)
				storageSlots[i] = new StorageSlot();

			Array.Resize(ref Slots, oldLen + extraSlots);
			for (int i = oldLen; i < Slots.Length; i++)
			{
				SlotController sc = new SlotController();
				sc.itemStack = storageSlots[i];
				Slots[i] = sc;
			}

			// Refresh Siblings on every slot so stacks can merge into the new slots too.
			for (int i = 0; i < Slots.Length; i++)
				Slots[i].Siblings = Slots;
		}

		/// <summary>Slot range (start index) for the given page.</summary>
		public int GetPageStart(int page)
		{
			return page * PageSize;
		}

		/// <summary>Slot range (exclusive end index) for the given page.</summary>
		public int GetPageEnd(int page)
		{
			return Mathf.Min((page + 1) * PageSize, Slots.Length);
		}
	}
}
