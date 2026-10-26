---
AIGC:
    Label: "1"
    ContentProducer: 001191440300708461136T1XGW3
    ProduceID: 89238829fb97168b9c4195c03c152aef_a93f32dbd09111f1a443525400393706
    ReservedCode1: nYMvb3mpsfup51QatdwQ+60p019HWq2Zy48SrV3RwSC/hvxbPBPGnW/XpylzlhbRTe/oICCl924FV2lZzgCD1lM8C6zotu29JPsRl/8zZqsHEb7GZYJnK5ZnTD2/pxDp8i+DgeF+kK2WJZZ1dPhfYRzhb6iC8yZIsJ6X5IUD8IMy4bH2V1J8Ord1t44=
    ContentPropagator: 001191440300708461136T1XGW3
    PropagateID: 89238829fb97168b9c4195c03c152aef_a93f32dbd09111f1a443525400393706
    ReservedCode2: nYMvb3mpsfup51QatdwQ+60p019HWq2Zy48SrV3RwSC/hvxbPBPGnW/XpylzlhbRTe/oICCl924FV2lZzgCD1lM8C6zotu29JPsRl/8zZqsHEb7GZYJnK5ZnTD2/pxDp8i+DgeF+kK2WJZZ1dPhfYRzhb6iC8yZIsJ6X5IUD8IMy4bH2V1J8Ord1t44=
---

# Obenseuer-BankInventory (BepInEx 6)
## Installation (BepInEx 6)

- Requires **BepInEx 6** (x64). Copy `bin/Release/Obenseuer-BankInventory.dll` into `BepInEx/plugins/Obenseuer-BankInventory/` under your Obenseuer install.
- This is the **BepInEx 6** port of the original BepInEx 5 mod. The original BepInEx 5 edition remains in the `Obenseuer-BankInventory` repository.


A BepInEx 6 mod for [Obenseuer](https://store.steampowered.com/app/1554640/Obenseuer/) that adds a **bank inventory**: press a key to open a storage UI where you can deposit and take items, with **infinite storage** (base capacity, auto-expanding, multi-page) and **full save support**.

## Features

- Open/close the bank with a hotkey (default `O`).
- Deposit items from your inventory by dragging them into the bank (or use the game's "take all" button to withdraw everything).
- **Infinite storage**: starts with 30 slots; whenever the storage becomes full it automatically grows by one page. You can never lose items to the floor because there is always at least one free slot.
- **Multiple pages**: the UI shows 88 slots per page; navigate with `[` / `]` (previous / next page).
- **Save support**: the bank is a global savable object (`Storage.global = true`), serialized into `Globals.tnmt` by the game's save system. It survives scene changes and save/load cycles.
- Everything is configurable in the BepInEx config file.

## Requirements

- [BepInEx 6](https://github.com/BepInEx/BepInEx) (x64) installed in the game folder (tested with 6.0.0-pre.2)
- Obenseuer (Windows, x64, .NET Framework 4.7.2 / net472 runtime)

## Installation

1. Build the mod (see below) or grab the release DLL.
2. Copy `Obenseuer-BankInventory.dll` into `BepInEx/plugins/` inside your Obenseuer folder.
3. Launch the game. Press `O` to open/close the bank.

## Configuration

The config file `BepInEx/config/com.juanberta.obenseuer.bankinventory.cfg` is generated on first run:

| Section | Key | Default | Description |
| --- | --- | --- | --- |
| General | `OpenBankKey` | `O` | Key that opens/closes the bank inventory (default `O`; `B` is used by the game's Head Lamp Use) |
| General | `NextPageKey` | `]` (RightBracket) | Key to go to the next bank page |
| General | `PreviousPageKey` | `[` (LeftBracket) | Key to go to the previous bank page |
| Storage | `BaseCapacity` | `30` | Initial number of slots. The bank expands automatically when it becomes full |
| Storage | `PageSize` | `88` | Slots shown per page (the game UI grid supports 88 slots) |

## Building

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Obenseuer-BankInventory.csproj /p:Configuration=Release /p:Platform=x64
```

Output: `bin\Release\Obenseuer-BankInventory.dll`

The project targets **net472 / x64 / LangVersion 7.3** (BepInEx 6 style) and references the game assemblies from `C:\Juegos\Obenseuer\Obenseuer_Data\Managed\` and BepInEx core from `C:\Juegos\Obenseuer\BepInEx\core\` (BepInEx.Core.dll / BepInEx.Unity.Mono.dll). Adjust the HintPaths if your game is installed elsewhere.

## How it works (design notes)

- `BankStorage` extends the game's `Storage` component. It reuses the game's own slot/storage machinery (`StorageSlot`, `SlotController`, stacking, dragging), so all vanilla behaviors (stack merging, item meta, perishables...) work out of the box.
- **Infinite growth**: `Update()` checks `Storage.IsStorageFull()` while the bank is open. When the storage is full, `ExpandByPage()` appends one page of fresh slots and refreshes every controller's `Siblings` array so new stacks can spread into the new slots. Because the storage is never actually full during play, the game's "drop to floor" overflow path is never hit when dragging items in.
- **Paging**: all slots get a slot UI created once when the bank opens; the current page's slots are `SetActive(true)` and the others are hidden. Hidden slots still keep their UI objects, which keeps vanilla code that iterates all slots (e.g. the "Take All" button) null-safe. Pages are switched with `[` / `]`, and the panel title shows `Page X/Y`.
- **Saving**: the bank GameObject is `DontDestroyOnLoad` and registered into `SaveController.DontDestroyOnLoadObjects`. It overrides `OnSavingGameSpecial()` / `OnLoadingGameSpecial()` to serialize via `SaveController.SerializeData/DeSerializeData(this, global=true)`, so its contents live in the global save (`Globals.tnmt`) and survive scene changes and save/load cycles.

## License

MIT
*ï¼ˆå†…å®¹ç”±AIç”Ÿæˆï¼Œä»…ä¾›å‚è€ƒï¼‰*
