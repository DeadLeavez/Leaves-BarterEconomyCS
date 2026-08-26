# Leaves-BarterEconomy — Handoff

Last updated: 2026-08-14 (added the status web page at `/bartereconomy/ui` — see §9). Prior:
2026-08-02 spring-cleaning pass (style, comment trim → DEV_NOTES, and removed flea pricing /
traderCategoryMultipliers / debugFastResupplySeconds — see §7). Read `4.0 Mods\HANDOFF.md`
first for project-wide rules.

---

## 1. What the mod does

Replaces traders' cash trades with **item barters**: every trade above a value cutoff gets a
generated barter scheme (give N of item X + M of item Y) drawn from a value-tiered pool of
payment items. Heavily configurable: which traders, which categories can be asked for, tier
math, cash-trade chances, and ammo-in-boxes. Trades regenerate on trader resupply (or on a fixed
seed — `readme.md` covers the player-facing behavior).

Nearly everything lives in one class: `src/BarterEconomy.cs` (~1000 lines). `Config.cs`
mirrors `config.jsonc`; `ModMetadata.cs` is boilerplate.

## 2. Lifecycle (read the class-level comment — it's the project's best DI writeup)

`[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 1)]`, and
the class implements **both `IOnLoad` and `IOnUpdate`**. Singleton is *required*: SPT's DI
registers an injectable under every interface it implements as independent calls, so without
Singleton the class is constructed twice — OnLoad sets up state on one instance, the periodic
update loop calls OnUpdate on the *other*, never-loaded instance, and the mod silently never
updates traders (no crash, no log). The comment at the top of `BarterEconomy.cs` explains the
mechanism in full.

- **`OnLoad`**: loads config + `itemBlacklist.jsonc` + `manualTieredItems.jsonc`. The trader/item
  database reads happen later, in `EnsureLateSetup` on the first `OnUpdate` (see the paragraph below):
  snapshot `originalTraders`, print trader ids, warn to load after trader-adding mods, build the
  shared tier list (`GenerateBarterList()`), call `BuildAmmoBoxMap`.
- **`OnUpdate`** (SPT calls it periodically; gated by `timeBetweenUpdates` logic): for each
  trader in `tradersToUpdate` whose resupply time passed, restore original assort, run
  `ModifyTrader`, refresh ragfair. Traders are processed strictly **one at a time** — several
  fields (`assortParentLookup`, log buffers) rely on that.

## 3. Valuation — the price choke point

`GetItemValue(id)` is THE single point every valuation flows through (tier list build, trade
values, barter generation) and returns the item's handbook price. Cash-currency conversion is the
one deliberate exception (direct handbookHelper call — same result, just explicit at the call site).

> **Removed 2026-08-02:** the `useFleaPrices` / `config/fleaPrices.json` path (value items by a
> tarkov.dev flea snapshot instead of handbook). It was experimental, never used, and shipped off —
> ripped out in the spring-cleaning pass. `GetItemValue` is now handbook-only.

**Stack-count trap (bit us live):** `HandbookHelper.GetTemplatePriceForItems` sums ONE price
per item entry, ignoring `StackObjectsCount`. `GetTradeValue` therefore hand-sums
`GetItemValue(child.Template) * (child.Upd?.StackObjectsCount ?? 1)` for children. The trade
ROOT deliberately does **not** multiply — its stack count is the trader's *stock quantity*,
not the item's contents.

## 4. Barter generation flow

```
ModifyTrader(traderId)                      (once per resupplied trader)
  ├─ restore original assort; build assortParentLookup (once — see §6)
  ├─ snapshot Items via ToList() (AdjustTrade appends copies while iterating)
  └─ for each trade:
       ├─ ammoInBoxes && loose-ammo money trade? → ConvertAmmoTradeToBox (§5)
       ├─ IsMoneyTrade / barter override checks (copyOverrideExistingBarters keeps the
       │    original barter AND adds a generated copy)
       ├─ GetTradeValue(assort, trade, children)   ← stack-aware, §3
       ├─ below valueCutoff → stays cash (money scheme untouched)
       ├─ allTradesAsCash / cashTradeChance → cash at cashTradeMultiplier in cashTradeCurrency
       └─ GenerateBarter(value, itemId, loyaltyLevel)
            ├─ tradeValueOverrides honored first
            ├─ walk value down the tier list (valueStep / maxsteps / randomTierdownSteps —
            │    randomness so high-value items don't always demand the same top-tier items)
            ├─ pick items from the tier list (respecting maxDifferentItems,
            │    maxNumItems ± maxRandomNumItems, tradeItemBlacklist, no duplicates)
            └─ emit List<BarterScheme>
```

`savedBarters` / `saveloadBarters`: generated barters persist to `_savedbarters/` in the live
mod folder and are reloaded instead of regenerated (seed-stability across restarts). **Delete
`_savedbarters/*.json` to force regeneration** — e.g. after changing the blacklist or
valuation config; this was the "blacklist isn't applying" incident's second half (first half:
editing live config instead of workspace).

`saveloadLockHours` (default 72 = 3 days) turns the freeze into a **timed lock**: saved barters
hold for that many hours, then the next *actual* trader resupply (`ModifyTrader` only runs on a
real resupply — see §2) re-rolls and re-locks for another window. Per-trader last-reroll
timestamps persist to the sidecar `_savedbarters/_rerolltimes.json` (string traderId → unix
seconds) so the window survives restarts; the loader skips that file by name when reading the
per-trader assorts, and a saved assort with no recorded time is baselined to load-time (fresh
window, no instant re-roll). `saveloadLockHours <= 0` = freeze forever (the original behaviour).

## 5. Ammo in boxes (`ammoInBoxes`)

Goal: traders sell factory ammo boxes instead of loose rounds, and because a full box's value
clears `valueCutoff`, ammo actually barterizes.

- `BuildAmmoBoxMap`: scans the item DB once for AmmoBox-class (`543be5cb4bdc2deb348b4568`)
  templates and reads each box's `StackSlots` filter (which cartridge, how many rounds) →
  `ammoToBox: ammo id → (boxId, roundsPerBox)`. First box per ammo wins; boxes from other
  mods join automatically since the scan reads the live DB. Every real vanilla cartridge has
  a box except the Volna-R rubber slug; ammo without a box keeps its cash trade.
- `ConvertAmmoTradeToBox` rewrites the assort entry in place: box template instead of rounds;
  the per-purchase buy-cap **divided** by roundsPerBox (box *stock* is left as-is — LimitedTraders
  handles stock, so we removed the redundant division 2026-08-02); and — critical — adds the
  cartridges as **child assort entries** (slotId `"cartridges"`,
  parented to the box). Without the child the game sells EMPTY boxes, which are also
  near-worthless so they dodge barterization (found live). The children register in
  `assortParentLookup` so valuation sees box + rounds.
- **Stack splitting (2026-07-17):** the cartridge fill goes through
  `ItemHelper.AddCartridgesToAmmoBox(list, boxTemplate)` — the same helper the game's own loot
  generation uses — NOT a hand-built single cartridge item. A cartridge stack can't exceed the
  ammo's `StackMaxSize`, so a 120-round 5.45 box must be **2×60**, not one stack of 120; the old
  hand-rolled item produced the impossible over-sized stack. The helper takes a list whose `[0]`
  is the box and appends the correctly-split cartridge items onto it (we skip `[0]` and add the
  rest). It fills with the box template's own default round — safe because every ammo box in the
  DB lists exactly one ammo type (verified: 0 of 213 boxes list more than one), so the box's
  default always equals the loose round the trade was selling. `roundsPerBox` (box capacity) is
  still used for the price/stock/cap scaling below.
- The original money scheme priced ONE round, so `ConvertAmmoTradeToBox` also multiplies the
  cash scheme by roundsPerBox — this is the fallback price for cheap ammo that stays below
  `valueCutoff` (found live: 16-round BZhT packs selling for 71₽, the price of one bullet).

Each bullet was a separate live-found bug; the code comments document each.

### ⚠️ 5.1 Copied trades and quest gating — why we never duplicate a quest-locked trade (2026-07-17)

**Symptom:** quest-locked ammo boxes (a boxed round that's a quest reward) were purchasable
*before* the gating quest was done. Subtle tell: it was fine right after a fresh boot but the
"OP ammo" came back **after the first trader resupply/reset**.

**Root cause — SPT serves quest gating from a cache hydrated exactly once.**
The copy paths (`allTradesAsCash`, `copyOverrideExistingBarters`) clone a trade and mint it a
**new assort id** (`CopyItemWithChildren` → `generateNewItemIds`). Loyalty can travel with a copy
(the call sites copy `LoyalLevelItems[newId]`), but quest gating **can't**, and the reason is
deeper than "we forgot to copy it":

- `TraderAssortHelper.GetAssort` hides locked trades using `AssortHelper.StripLockedQuestAssort`
  against **`MergedQuestAssorts`** — a lookup built by `HydrateMergedQuestAssorts` merging every
  trader's `QuestAssort`, and cached with `_mergedQuestAssorts ??= …`. It is hydrated **once** and
  **never rebuilt**. (Confirmed by IL: the getter is a plain `??=` lazy cache.)
- So any assort id that didn't exist at hydration time can never be quest-gated. Copy ids are
  exactly that — and worse, they're **re-minted on every `OnUpdate`**, i.e. on every trader
  resupply. Writing the lock onto the copy id (the first attempt at this fix, `InheritQuestLock` +
  a per-update `QuestAssort` reset) appeared to work, but only until the first resupply: the copy
  id then changed, the once-hydrated cache still referenced the *old* id, and every quest-locked
  box leaked. That's the "worked earlier, OP ammo after a reset" report.
- `ResetExpiredTrader` itself is not the culprit for the item side: `GetPristineTraderAssorts`
  clones the **live** assort (`GetTraderAssortsByTraderId` → the live `Trader.Assort`), so a reset
  re-clones our current boxes rather than restoring vanilla loose ammo. The only thing a reset
  breaks is quest gating, via the stale merged cache above.

**Fix:** don't fight the cache — **never duplicate a quest-locked trade.** `IsQuestLocked`
(checks the trade's id across the trader's `QuestAssort` buckets) guards both copy sites; a
quest-locked trade is left as its single in-place entry, which keeps its **original id** — the one
that *was* present at hydration and stays valid in the merged cache forever, across any number of
resupplies. Practical effect: a quest-locked box just isn't offered as an extra cash/barter copy;
it falls through to the normal barterization on its own id and stays correctly locked. (The
earlier `InheritQuestLock` helper and the `QuestAssort` per-update reset were removed — they can't
work given a once-hydrated cache.)

**How it was proven** (repeatable, decisive — this is the regression test): force fast trader
resupply (this used the since-removed `debugFastResupplySeconds` knob — to re-run it you'd shorten
the `TraderConfig` resupply interval another way) so the trader resupplies every ~40 s, then poll
`/client/trading/api/getTraderAssort/<traderId>` for a profile that hasn't done the gating quests,
counting boxes whose template is served under any *non-original* id.
- Buggy build: 0/9 leaking on the first poll → **9/9 leaking within one resupply cycle**, and
  stays there.
- Fixed build: **0/9 across many resupply cycles** (>110 s), boxes still sold and still stack-split
  correctly. (Response is zlib + `["data"]`; gating is per-profile so use a session cookie; server
  is HTTPS self-signed so `curl -sk`.)

**General rule for this codebase:** any SPT structure served through a lazily-hydrated cache
(`?? =`) — merged quest assorts, handbook prices, preset cache, locale — cannot be corrected by
mutating the underlying data at runtime after first use. Either arrange to be in the data *before*
first hydration (stable ids present at load), or avoid needing the post-hydration change at all
(what we do here).

## 6. Performance notes (why some code looks indirect)

- **`FindChildren` + `assortParentLookup`**: SPT's `ItemHelper.FindAndReturnChildrenByAssort`
  rebuilds a full ParentId index over the entire multi-thousand-item assort on *every call*,
  and it was being called 1–2× per trade — this alone made trader updates ~10× slower than
  the TS original. The lookup is now built once per trader and shared. (It also re-adds the
  root item, matching the removed TS helper's behavior.)
- **Log buffering**: `writeLog` output is buffered per trader (`logPreamble` + per-trade
  `logBlocks`, flushed once, sorted by loyalty level) because per-line `appendFile` — each an
  open+append+close plus directory check — was the actual cost of a trader update.
- **`getLocale` calls** go through LeavesUtils' locale cache — see LeavesUtil handoff §3.2;
  this mod was the reason that cache exists.

## 7. Removed in the cleanup pass (2026-08-02)

Three features were ripped out as never-used chores — noted here so their absence isn't a mystery:

- **`traderCategoryMultipliers`** — per-trader category value overrides (e.g. Therapist valuing meds
  1.5×). Too fiddly to configure, never used. Removed the config key, the per-trader tier lists
  (`traderBarterLists`/`activeBarterList`), and the override math. There's now one shared tier list.
- **`useFleaPrices` / `fleaPrices.json`** — experimental tarkov.dev flea valuation. See §3.
- **`debugFastResupplySeconds`** — test-only resupply override. Was the sole tie to the obsolete
  `ConfigServer` (gone in 4.1), so dropping it also simplifies the eventual 4.1 port.
  **Update 2026-08-14:** `ConfigServer` is back (injected again) — the status web page's
  `GetTraderStatuses` reads `TraderConfig.UpdateTime` to get each trader's resupply cycle bounds.
  So the 4.1 port again has to deal with the `ConfigServer` change here, same as TreasureBox/QR/Shiny.

## 8. Config keys worth explaining (rest are self-describing in config.jsonc)

- `tradersToBarter` — trader ids to process. Load order note from OnLoad applies: this mod
  should load *after* trader-adding mods so their ids exist.
- `valueCutoff` — below this handbook value a trade stays cash (per-round for unboxed ammo — see §5).
- `barterCategories` (with per-category `multi`) / `traderCategoriesBlacklist` — what can be
  ASKED FOR as payment vs. which trader categories never barterize.
- `overrideExistingBarters` / `copyOverrideExistingBarters` — replace vanilla barters vs.
  keep them and add a generated variant alongside.
- `writeLogLocale` — language used for the human-readable barter log in `output/`.
- `dumpTiersToFile` — writes the generated tier list with names to `output/`; diagnostic
  only, never read back.

## 9. Files

| File | Role |
|---|---|
| `src/BarterEconomy.cs` | Everything (§2–§8). Exposes for the web API: `GetItemTiers()` (id→tier), `GetTraderStatuses()` (per-trader resupply + save-load lock window, DTOs `BarterStatusReport`/`TraderStatus`), `GetTraderTrades(traderId)` (one trader's live trades categorized by handbook tree + full payment schemes, DTOs `TraderTradesReport`/`TradeInfo`/`PaymentPart`; helpers `EnsureHandbookMaps`/`CategoryChain`/`ValidIcon`), `GetAllTradersWithTrades()`/`SearchTrades()`/`GetTradesSellingItem()` (Fence excluded, Ref relabelled via `DisplayNickname`). |
| `src/BarterEconomyApi.cs` | `IHttpListener`, prefix `/bartereconomy/`. Serves the web pages + read-only JSON. Page routes: `GET /ui` (Timers), `GET /ui/trades` (Trades), `GET /ui/search` (cross-trader Search), `GET /ui/tiers` (Tier-list), `GET /ui/common.js` (shared client helpers, served from `common.txt`). Data: `GET /api/status`, `GET /api/traders` (every trader with trades, for the selector; **Fence excluded**), `GET /api/trades?trader=<id>` (one trader's categorized trades; 400 via `IsMongoIdShape`), `GET /api/trades/search?q=<text>` (cross-trader search, search-only), `GET /api/tiers` → `{id:tier}` (payment tier list; also consumed by LootFuckery's dump viewer as a soft dep). Add future endpoints here. |
| `assets/ui/trades.html` | The Trades page at `/bartereconomy/ui/trades`: a trader selector (from `/api/traders`; barter-managed traders first, non-managed like Arena shown dashed; **Fence excluded entirely**) then that trader's every trade grouped by the **handbook category tree** — nested `<details>` (top category → leaf sub-category), collapsed by default (one trader can have ~1770 trades). Each row shows the sold item, a `barter`/`cash` tag, loyalty level, and the full payment scheme (each item ×count, currency shown with its symbol incl. GP). **Hide money trades** toggle drops cash trades (all 4 money ids), but is skipped for **Ref** (`REF_ID`; SPT data nicknames him "Arena" — relabelled to "Ref" via `DisplayNickname`) since he's almost all GP/Lega. **Loyalty** dropdown (4 checkboxes LL1–LL4) filters by level; loyalty shows on each row far-right as `LL<n> ★…`. Filter toggles never collapse manually-opened categories (only a search-box change re-derives open state). Per-trader data from `/api/trades?trader=<id>`, cached client-side. Category icons come from the handbook `Icon` field, **sanitised by `ValidIcon`** (drops the bare `/files/handbook/` and any icon missing from `SPT_Data/images/handbook`) so the page never fires a 404 the SPT middleware would log as red `[UNHANDLED][/files/handbook/]`. |
| `assets/ui/search.html` | The cross-trader **Search** page at `/bartereconomy/ui/search` (its own sidebar entry). Search-only root step: empty query shows nothing; otherwise hits `/api/trades/search`, grouped by trader. **Barter chain:** every non-currency payment item is clickable (`.part-item`, id/name from `partHtml` in common.txt); clicking one calls `/api/trades/sells?item=<tpl>` and appends a "To get X" step below (keeping the steps above), whose payments are themselves clickable — chain as deep as you like. Clicking from step k drops any steps below k so the chain stays one path; each step has an ✕ to prune from there. A new text search resets the chain. Same Hide-money (Ref-exempt) + Loyalty filters, applied client-side across all steps. |
| `assets/ui/common.txt` | Shared client helpers for the Trades + Search pages (constants `REF_ID`/`CURRENCY`, `escapeHtml`/`matches`/`fmtCount`/`cleanName`/`iconHtml`/`partHtml`/`tradeHtml`). Served at `/bartereconomy/ui/common.js` (`.txt` on disk to dodge ModValidator, like rail.txt); loaded as a plain `<script>` before each page's inline script so its top-level declarations are visible. |
| `assets/ui/tiers.html` | The Tier-list page at `/bartereconomy/ui/tiers`: every payment item grouped by value tier (high→low), each shown with a clean `[T<n>]` tag + one client-side search box (type `[T5]` to isolate a tier). Data = the tier map (`/api/tiers`, authoritative) joined with item names from LeavesUtils' `/webui/db/index`. Names are cleaned client-side (`cleanName`) to strip BSG `<color>` markup **and** the in-name `[T#]` tag that the `showItemTiers` config option appends — so the tag shows once, whether or not that option is on. The general `/webui/db/query` endpoint (parent/name-regex/desc-regex) is the reusable building block behind this class of page. |
| `assets/ui/bartereconomy.html` | The status web page served at `/bartereconomy/ui`: a card per managed trader showing next resupply + next barter re-roll as live countdowns. Uses LeavesUtils' shared `/webui/base.css` + nav rail; polls `/api/status`. Deployed by the `assets\**` build glob; read fresh per request (live edits show without a rebuild). Trader portraits come from the server's `avatar` field (`Base.Avatar` — a hash, **not** the trader id). **Re-roll timing:** a re-roll only fires on an actual resupply, so the page shows the re-roll as the next resupply on/after the lock ends — exact when the lock is up by the next scheduled resupply, otherwise a `≥ lockEnds` floor plus the trader's randomized cycle range (`[resupplyMin, resupplyMax]`, which differ in this install — resupply length is randomized per cycle, so the exact re-roll time is unknowable). |
| `src/Config.cs` | Config record; documents each knob. |
| `config/config.jsonc` | The knobs. Shipped: `ammoInBoxes: true`. |
| `config/itemBlacklist.jsonc` | Items never used as barter payment. |
| `config/manualTieredItems.jsonc` | Hand-pinned tier placements overriding value math. |
| `output/` | Diagnostic dumps + barter logs. |
| `DEV_NOTES.md` (gitignored) | Bug-history prose lifted out of the source in the cleanup pass. |
| `_savedbarters/` (runtime, live folder) | Persisted generated barters — delete to force regen (§4). |

## 10. Git note

This mod folder is its own git repo (tags up to 1.1.7 = the TS releases). The C# port has not
been committed/tagged there yet.
