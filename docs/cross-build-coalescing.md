# Cross-build co-op: coalescing enum divergence (1.00 Game Pass ↔ 1.02 Steam)

Status: **design agreed, implementation in progress.** Everything below is verified against the two
decompiled builds (`decompiled\1.00-gamepass`, `decompiled\1.02`) rather than inferred.

## Why the two builds cannot meet today

`EObjectType` is serialized — its numeric values go into saves and across the wire — and both builds
declare it with **implicit numbering** (only `None = -1` is explicit), so values are purely positional.
Ids 0…55 agree exactly. Then they diverge and never re-converge:

| value | Game Pass (1.00) | Steam (1.02) |
|---|---|---|
| 0 … 55 | `CashCounter` … `TournamentPrizeShelf` | identical |
| **56** | `PSPreOrderPlaytable` | **`FoodBowl`** |
| 57–61 | `PSPreOrderCardTable`, `PSPreOrderPackOpener`, `XboxPreOrderPlaytable/CardTable/PackOpener` | — |
| 62 | `DeluxeInfiniteScentMachine` | — |
| `MAX` | 63 | 57 |

So value 56 means two different objects, and 57–62 exist only on 1.00. The mod's runtime enum catalogs
reflect it: 1032 members on Game Pass vs 1026 on Steam (net 6 = 7 extra minus 1).

1.02 has **no `PreOrderManager`, no `DeluxeEditionManager`, no `PlatformManager`** — the entire
pre-order/deluxe feature was removed, not replaced.

## What those types actually are

A full-tree grep shows each is mentioned **only** in its entitlement manager and the enum itself —
nothing in the game branches on them, so they carry no behaviour of their own:

- `PreOrderManager` — `Grant()` adds 5000 coins and sets `isPreOrderGranted`; `IsPreOrderFurniture` /
  `IsFurnitureForCurrentPlatform` classify the six pre-order types.
- `DeluxeEditionManager` — `Grant()` adds 20000 coins, spawns the **normal** `Workbench` in a package box
  and 64 ascension packs; `IsDeluxeFurniture` recognises `DeluxeInfiniteScentMachine`.
- `FoodBowl` — a new 1.02 placeable with `FoodBowlSaveData` (`fillAmount`, `isBoxed`, pos/rot) stored in
  `CPlayerData.m_FoodBowlSaveDataList` / `ShelfManager.m_FoodBowlSaveDataList`. **Nothing consumes it** —
  no spawn of `EObjectType.FoodBowl`, no interaction, no UI, and 1.02 has no pet system at all
  (no `Pet`/`Cat`/`Dog`/`Animal`/`FeedPet`/`petFood` anywhere). It is inert save plumbing.

## The rule: coalesce on ingest, never on egress

- **Host is the richer side** (Xbox host, Steam client): the client coalesces what it ingests. It renders
  a pre-order table as the vanilla one, the host keeps the truth, nothing is lost.
- **Client is the richer side** (Xbox client, Steam host): **refuse, before the send**. Coalescing there
  would send `PlayTable`, the host would store a plain table, and the echo would reconcile the client's
  *own* object down permanently — a silent, irreversible downgrade. Refuse instead.

Identity stays the host's. A client may not restate an object's type to the host: bind identity at
creation and never let a move carry the type, or a coalesced object would rewrite the host's real one.

## The alias table

Prefer the matching name whenever the peer has it; otherwise coalesce:

| 1.00-only name | → vanilla equivalent |
|---|---|
| `PSPreOrderPlaytable`, `XboxPreOrderPlaytable` | `PlayTable` (11) |
| `PSPreOrderCardTable`, `XboxPreOrderCardTable` | `VintageCardTable` (23) |
| `PSPreOrderPackOpener`, `XboxPreOrderPackOpener` | `AutoPackOpener1` (34) |
| `DeluxeInfiniteScentMachine` | `AutoCleanser1` (31) |
| `FoodBowl` (1.02-only) | **none — dropped, with a visible notice** |

The mod already compares enum identity **by name** and already translates the wire *and transferred
saves* by name (`CatalogIdMap` / `EnumValueMap`), so this is a table in the existing translation plus
making the key check alias-aware — not a new mechanism.

## One gate, not two

Every furniture type enters a world exactly once: a purchase. The money and the object come out of the
**same method**:

```csharp
public void EvaluateCartCheckout(float totalCost, int index)
{
    CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(totalCost));  // money, QUEUED
    FurniturePurchaseData d = InventoryBase.GetFurniturePurchaseData(index);
    SpawnPackageItemBox(d.objectType);                                 // object
    ...
}
```

- The phone's buy button only *opens* the shop (`OpenChildScreen(...)`) — not a second path.
- **Furniture Overhaul funnels into the same method** (`FurnitureShopScreen` → `Purchase.Buy` →
  `Purchase.cs: val.EvaluateCartCheckout(...)`), and its other shop patches are presentation/lookup only.
- `FurnitureShopUIScreen` exists in **both** builds, unlike `PlatformManager` (1.00 only).

So one prefix on `FurnitureShopUIScreen.EvaluateCartCheckout`, refusing before money and object, covers
vanilla and Furniture Overhaul on both builds. It must never name a game type: resolve the type and
method reflectively, in the style of `DebugToolsPatches.TryPatch`. Refusal is visible on the mod's own
status line (a game popup would require naming `ENotEnoughResourceText`).

Hooking deeper is wrong: the money event is queued *before* the spawn, so refusing at the spawn would
take the player's money and give them nothing.

## No configuration

The cross-build gate (`AllowCrossBuildJoin`) is already the experimental opt-in, so consent and
responsibility already sit in the same place. A second switch would only add a way to be surprised.

## Testing

`[Hidden] ForceEntitlements = true` (hand-edit the .cfg) makes `PlatformManager.HasEntitlement` answer
yes to every store entitlement, so the deluxe/pre-order furniture is purchasable even without owning it —
enough to place one of each type and exercise the coalescing. Harmless no-op on 1.02, which has no
`PlatformManager` (it logs that and does nothing).

## Open risks

- Aliasing loses the pre-order **skin**. Behaviour is identical (nothing branches on those types), so
  this is appearance only.
- `DeluxeInfiniteScentMachine` → `AutoCleanser1` is the one place a *functional* difference may be lost:
  "infinite" is presumably a property on its prefab, so the vanilla cleanser may need refills. Verify in
  game with `ForceEntitlements`.
- The build fingerprint gate stays: the Unity and save-format differences are real even with the enums
  reconciled.
- Patch-level release: the alias set rides the existing catalog/identity exchange, so there is no
  handshake change.
