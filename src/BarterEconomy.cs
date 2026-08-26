using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Leaves_BarterEconomy;

// Singleton is mandatory (implements both IOnLoad + IOnUpdate). Loads at PostDBModLoader+1.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public partial class BarterEconomy(
    DatabaseService databaseService,
    ModHelper modHelper,
    JsonUtil jsonUtil,
    TimeUtil timeUtil,
    HandbookHelper handbookHelper,
    ItemHelper itemHelper,
    RagfairOfferGenerator ragfairOfferGenerator,
    ICloner cloner,
#pragma warning disable CS0618 // ConfigServer is obsolete but still the only way to reach TraderConfig in 4.0.13.
    ConfigServer configServer,
#pragma warning restore CS0618
    Leaves.TierList tierList)
    : IOnLoad, IOnUpdate
{
    // Vanilla item ids (pre-mod snapshot) - keeps modded items out of the barter currency pool.
    private HashSet<MongoId> vanillaItemIds = new();

    public void CaptureVanillaItemIds(IEnumerable<MongoId> ids)
    {
        vanillaItemIds = new HashSet<MongoId>(ids);
    }

    private BarterEconomyConfig config = null!;
    private List<MongoId> itemBlacklist = null!;
    private Dictionary<string, int> manualTieredItems = null!;
    private string locale = null!;
    private string modPath = null!;
    private string outputFolder = null!;

    private Dictionary<MongoId, Trader> originalTraders = null!;
    private Dictionary<MongoId, Trader> cachedTrades = null!;
    private List<MongoId> tradersToUpdate = null!;
    private List<long> tradersLastUpdate = null!;
    private Dictionary<MongoId, TraderAssort> savedBarters = null!;

    // traderID (as string) -> unix timestamp (seconds) of the last barter re-roll.
    // Persisted as a sidecar next to the saved assorts so the lock window survives server restarts.
    private Dictionary<string, long> savedBarterTimes = null!;

    // Sidecar file that lives inside _savedbarters alongside the per-trader assort files.
    // It is NOT a trader assort, so the loader below skips it by name when reading the saved barters.
    private const string RerollTimesFile = "_rerolltimes.json";

    // The tier list of items players can pay WITH, shared by every trader.
    private Dictionary<int, List<MongoId>> barterList = null!;

    private static readonly MongoId AmmoClass = "5485a8684bdc2da71d8b4567";
    private static readonly MongoId AmmoBoxClass = "543be5cb4bdc2deb348b4568";

    // One ammo type's box packaging plus the round's stats, built from the live item DB at setup.
    // Damage/Penetration are null when the ammo has no Properties (annotation is skipped for it).
    private sealed record AmmoBoxInfo(
        MongoId BoxId,
        int RoundsPerBox,
        int? Damage,
        int? Penetration
    );

    // ammo id -> its box packaging + stats.
    private readonly Dictionary<MongoId, AmmoBoxInfo> ammoToBox = new();

    private bool setUpCompleted;
    private string currentLogFile = "";

    // Log lines buffered and flushed once per trader - per-line file appends killed performance.
    private readonly List<string> logPreamble = new();
    private readonly List<(int Loyalty, List<string> Lines)> logBlocks = new();

    // Items-by-ParentId lookup for the current trader, built once per ModifyTrader (see FindChildren).
    private Dictionary<string, List<Item>> assortParentLookup = null!;

    public Task OnLoad()
    {
        modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        config = modHelper.GetJsonDataFromFile<BarterEconomyConfig>(modPath, "config/config.jsonc");
        itemBlacklist = modHelper.GetJsonDataFromFile<List<MongoId>>(modPath, "config/itemBlacklist.jsonc");

        // manualTieredItems.jsonc is authored as tier -> [item ids] so ids can be cut/pasted between tiers without editing each line.
        // Flatten it here into the itemId -> tier lookup the rest of the code expects.
        Dictionary<string, List<string>> tieredItemLists = modHelper.GetJsonDataFromFile<Dictionary<string, List<string>>>(modPath, "config/manualTieredItems.jsonc");
        manualTieredItems = new Dictionary<string, int>();
        foreach (KeyValuePair<string, List<string>> tierEntry in tieredItemLists)
        {
            int tier = int.Parse(tierEntry.Key);
            foreach (string itemId in tierEntry.Value)
            {
                manualTieredItems[itemId] = tier;
            }
        }

        locale = string.IsNullOrEmpty(config.WriteLogLocale) ? "en" : config.WriteLogLocale;
        outputFolder = System.IO.Path.Combine(modPath, "output");

        Leaves.Utils.Instance.printColor("[Barter Economy] - Barter Economy Starting", LogTextColor.Green);

        savedBarters = new Dictionary<MongoId, TraderAssort>();
        savedBarterTimes = new Dictionary<string, long>();
        if (config.SaveLoadBarters)
        {
            List<string> files = Leaves.Utils.Instance.getFilesInFolder(modPath, "_savedbarters");

            // getFilesInFolder returns absolute paths, so compare on the bare filename.
            // Otherwise the sidecar's "_rerolltimes" name gets shoved into savedBarters (keyed by MongoId) and crashes.
            // Load the sidecar BEFORE the assort loop: that loop's baseline check reads savedBarterTimes, and loading reassigns the whole dictionary.
            foreach (string file in files)
            {
                if (System.IO.Path.GetFileName(file) == RerollTimesFile)
                {
                    savedBarterTimes = modHelper.GetJsonDataFromFile<Dictionary<string, long>>(modPath, System.IO.Path.Combine("_savedbarters", RerollTimesFile));
                    break;
                }
            }

            long timeNow = timeUtil.GetTimeStamp();
            foreach (string file in files)
            {
                if (System.IO.Path.GetFileName(file) == RerollTimesFile)
                {
                    continue;
                }
                TraderAssort data = modHelper.GetJsonDataFromFile<TraderAssort>(modPath, System.IO.Path.Combine("_savedbarters", System.IO.Path.GetFileName(file)));
                string fileWithoutExtension = Leaves.Utils.Instance.getFileWithoutExtension(file);
                Leaves.Utils.Instance.printColor("[Barter Economy] Loaded saved data for: " + fileWithoutExtension);
                savedBarters[fileWithoutExtension] = data;

                // A save from before the lock timer existed (or a hand-dropped file) has no recorded reroll time.
                // Treat now as its baseline so the loaded barters honour a fresh lock window instead of instantly re-rolling on the next resupply.
                if (!savedBarterTimes.ContainsKey(fileWithoutExtension))
                {
                    savedBarterTimes[fileWithoutExtension] = timeNow;
                }
            }
        }

        setUpCompleted = true;

        return Task.CompletedTask;
    }

    // DB reads run here on the first OnUpdate, not OnLoad, so content mods that load after us are in.
    private bool lateSetupDone = false;

    private void EnsureLateSetup()
    {
        if (lateSetupDone)
        {
            return;
        }
        lateSetupDone = true;

        Dictionary<MongoId, Trader> traders = databaseService.GetTraders();
        originalTraders = cloner.Clone(traders)!;
        cachedTrades = cloner.Clone(traders)!;

        if (config.OnlyVanillaBarterItems)
        {
            int runtimeCount = databaseService.GetItems().Count;
            if (vanillaItemIds.Count == 0)
            {
                Leaves.Utils.Instance.printColor(
                    "[Barter Economy] WARNING: vanilla item snapshot missing - modded items may be used as barter currency.",
                    LogTextColor.Red);
            }
            else
            {
                Leaves.Utils.Instance.printColor(
                    $"[Barter Economy] Barter currency restricted to vanilla items: {vanillaItemIds.Count} vanilla of {runtimeCount} total ({runtimeCount - vanillaItemIds.Count} mod-added items excluded).",
                    LogTextColor.Yellow);
            }
        }

        tradersToUpdate = new List<MongoId>();
        tradersLastUpdate = new List<long>();

        // Print every trader id so players can find the ones they want in config.
        Leaves.Utils.Instance.printColor("[Barter Economy] All traders in the database:", LogTextColor.Yellow);
        foreach (MongoId traderID in traders.Keys)
        {
            Leaves.Utils.Instance.printColor($"[Barter Economy] Trader ID: \"{traderID}\" - Nickname: {Leaves.Utils.Instance.getTraderNickname(traderID)}", LogTextColor.Yellow);
        }

        foreach (MongoId traderID in config.TradersToBarter)
        {
            if (!traders.ContainsKey(traderID))
            {
                Leaves.Utils.Instance.printColor($"[Barter Economy] Trader {traderID} does not exist in the database.", LogTextColor.Red);
                continue;
            }

            tradersToUpdate.Add(traderID);
            tradersLastUpdate.Add(0);
        }

        barterList = GenerateBarterList();

        if (config.ShowItemTiers || config.ShowItemTiersInDescription)
        {
            AnnotateItemTierLocale();
        }

        if (config.AmmoInBoxes)
        {
            BuildAmmoBoxMap();
            if (config.AmmoBoxShowStats)
            {
                AnnotateAmmoBoxLocale();
            }
        }
    }

    public Task<bool> OnUpdate(long secondsSinceLastRun)
    {
        if (secondsSinceLastRun <= 30)
        {
            return Task.FromResult(false);
        }

        if (!setUpCompleted)
        {
            return Task.FromResult(false);
        }

        // First update = the earliest point the item/trader database is guaranteed complete.
        EnsureLateSetup();

        long timeNow = timeUtil.GetTimeStamp();
        Dictionary<MongoId, Trader> traders = databaseService.GetTraders();

        for (int traderNum = 0; traderNum < tradersToUpdate.Count; traderNum++)
        {
            MongoId traderID = tradersToUpdate[traderNum];
            if (timeNow <= tradersLastUpdate[traderNum])
            {
                continue;
            }

            if (config.WriteLog)
            {
                string date = timeUtil.GetDate();
                string time = timeUtil.GetTime();
                string nickname = Leaves.Utils.Instance.getTraderNickname(traderID);

                currentLogFile = $"trades__{date}_{time}_{nickname}.txt";
                WriteLogFileLine($"[Updating Trader:{nickname}]");
                WriteLogFileLine("-------------------------------------------");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            traders[traderID].Assort = cloner.Clone(cachedTrades[traderID].Assort)!;
            ModifyTrader(traderID);
            FlushLogFile();
            stopwatch.Stop();
            Leaves.Utils.Instance.printColor($"[Barter Economy] Updated Trader:[{Leaves.Utils.Instance.getTraderNickname(traderID)}]-[{traderID}]-[Time:{stopwatch.ElapsedMilliseconds}ms]", LogTextColor.Blue);

            ragfairOfferGenerator.GenerateFleaOffersForTrader(traderID);

            tradersLastUpdate[traderNum] = traders[traderID].Base.NextResupply ?? 0;
        }

        return Task.FromResult(true);
    }

    // The single point every valuation flows through (cash-currency conversion is the one exception).
    private double GetItemValue(MongoId id)
    {
        return handbookHelper.GetTemplatePrice(id);
    }

    // Builds the payment tier list: every allowed item bucketed into a value tier.
    private Dictionary<int, List<MongoId>> GenerateBarterList()
    {
        Dictionary<MongoId, TemplateItem> items = databaseService.GetItems();
        Dictionary<int, List<MongoId>> newBarterList = new();

        foreach (TemplateItem item in items.Values)
        {
            if (item.Properties?.QuestItem == true || item.Type != "Item")
            {
                continue;
            }

            if (itemBlacklist.Contains(item.Id))
            {
                continue;
            }

            // Mod items are never currency; the pre-mod snapshot catches them all (empty = skip filter).
            if (config.OnlyVanillaBarterItems && vanillaItemIds.Count > 0 && !vanillaItemIds.Contains(item.Id))
            {
                continue;
            }

            if (config.BarterCategories.TryGetValue(item.Parent.ToString(), out MultiEntry? category))
            {
                double multiplier = category.Multi;

                int value;
                if (manualTieredItems.TryGetValue(item.Id.ToString(), out int manualTier))
                {
                    value = manualTier;
                }
                else
                {
                    value = (int)Math.Ceiling((GetItemValue(item.Id) * multiplier) / config.ValueStep);
                    value = tierList.getClosestTier(config.Tiers, value);
                }

                if (!newBarterList.ContainsKey(value))
                {
                    newBarterList[value] = new List<MongoId>();
                }
                newBarterList[value].Add(item.Id);
            }
        }

        if (config.DumpTiersToFile)
        {
            WriteResult("barterList", ConvertTierListToNames(newBarterList));
        }

        return newBarterList;
    }

    private void BuildAmmoBoxMap()
    {
        foreach (TemplateItem item in databaseService.GetItems().Values)
        {
            if (item.Parent != AmmoBoxClass)
            {
                continue;
            }
            foreach (StackSlot stackSlot in item.Properties?.StackSlots ?? new List<StackSlot>())
            {
                int roundsPerBox = (int)(stackSlot.MaxCount ?? 0);
                foreach (SlotFilter filter in stackSlot.Properties?.Filters ?? new List<SlotFilter>())
                {
                    foreach (MongoId ammoId in filter.Filter ?? new HashSet<MongoId>())
                    {
                        bool haveBox = ammoToBox.TryGetValue(ammoId, out AmmoBoxInfo? existing);
                        if (haveBox && roundsPerBox <= existing!.RoundsPerBox)
                        {
                            continue;
                        }

                        // Grab the round's damage/penetration for the box name/desc annotation.
                        int? damage = null;
                        int? penetration = null;
                        if (databaseService.GetItems().TryGetValue(ammoId, out TemplateItem? ammo) && ammo.Properties != null)
                        {
                            damage = (int)(ammo.Properties.Damage ?? 0);
                            penetration = (int)(ammo.Properties.PenetrationPower ?? 0);
                        }

                        ammoToBox[ammoId] = new AmmoBoxInfo(item.Id, roundsPerBox, damage, penetration);
                    }
                }
            }
        }
        Leaves.Utils.Instance.printColor($"[Barter Economy] Ammo-in-boxes: {ammoToBox.Count} ammo types have a box and will be sold boxed.");
    }

    // Put each ammo box's round damage/penetration onto its name ("[dmg/pen]") and description.
    private void AnnotateAmmoBoxLocale()
    {
        // Snapshot box -> stats for the transformer, one entry per box (several ammo can share a box; last wins).
        // Boxes whose ammo had no Properties (null stats) are left unannotated.
        Dictionary<MongoId, AmmoBoxInfo> AmmoStats = new();
        foreach (AmmoBoxInfo info in ammoToBox.Values)
        {
            if (info.Damage == null || info.Penetration == null)
            {
                continue;
            }
            AmmoStats[info.BoxId] = info;
        }

        if (AmmoStats.Count == 0)
        {
            return;
        }

        Dictionary<string, string>? AmmoboxAnnotationTransform(Dictionary<string, string>? locale)
        {
            if (locale == null)
            {
                return locale;
            }
            foreach ((MongoId boxId, AmmoBoxInfo s) in AmmoStats)
            {
                string nameKey = $"{boxId} Name";
                if (locale.TryGetValue(nameKey, out string? name))
                {
                    locale[nameKey] = $"{name} [{s.Damage}/{s.Penetration}]";
                }

                string descKey = $"{boxId} Description";
                string statLines = $"Damage: {s.Damage}\nPenetration: {s.Penetration}";
                if (locale.TryGetValue(descKey, out string? desc) && !string.IsNullOrEmpty(desc))
                {
                    locale[descKey] = $"{desc}\n{statLines}";
                }
                else
                {
                    locale[descKey] = statLines;
                }
            }
            return locale;
        }

        databaseService.GetLocales().Global["en"].AddTransformer(AmmoboxAnnotationTransform);
        Leaves.Utils.Instance.printColor($"[Barter Economy] Annotated {AmmoStats.Count} ammo box name(s)/description(s) with damage/penetration.");
    }

    // Append the payment tier to each tier-list item's name ("Sledgehammer [T15]") and/or description.
    // Name annotation is a gamble (name labels may not render rich text); descriptions reliably do, so ShowItemTiersInDescription is the fallback.
    private void AnnotateItemTierLocale()
    {
        // Snapshot item id -> tier for the transformer (barterList can change on rebuild).
        Dictionary<MongoId, int> itemTiers = new();
        foreach ((int tier, List<MongoId> tierItems) in barterList)
        {
            foreach (MongoId id in tierItems)
            {
                itemTiers[id] = tier;
            }
        }

        if (itemTiers.Count == 0)
        {
            return;
        }

        // Precompute the "[T#]" tag per tier, colored if enabled: yellow (hue 0.15) up to red (hue 1.0).
        // Hue is spaced by RANK across the distinct tiers present, not by raw tier number, so crowded low tiers stay distinct instead of bunching in the yellow corner.
        SortedSet<int> distinctTiers = new();
        foreach (int tier in itemTiers.Values)
        {
            distinctTiers.Add(tier);
        }

        Dictionary<int, int> tierRank = new();
        int rank = 0;
        foreach (int tier in distinctTiers)
        {
            tierRank[tier] = rank;
            rank++;
        }
        int rankCount = distinctTiers.Count;

        Dictionary<int, string> tierTags = new();
        foreach (int tier in distinctTiers)
        {
            string tag = $"[T{tier}]";
            if (config.ColorItemTiers)
            {
                // Single-tier edge case (rankCount == 1): put it at the yellow end rather than divide by zero.
                double t = rankCount > 1 ? (double)tierRank[tier] / (rankCount - 1) : 0.0;
                double hue = 0.15 + t * (1.0 - 0.15);
                (int R, int G, int B) rgb = Leaves.Utils.Instance.HSVtoRGB(hue, 1.0, 1.0);
                string hex = $"#{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}";
                tag = Leaves.Utils.Instance.RTT_Color(tag, hex);
            }
            tierTags[tier] = tag;
        }

        Dictionary<string, string>? ItemTierAnnotationTransform(Dictionary<string, string>? locale)
        {
            if (locale == null)
            {
                return locale;
            }
            foreach ((MongoId id, int tier) in itemTiers)
            {
                if (config.ShowItemTiers)
                {
                    string nameKey = $"{id} Name";
                    if (locale.TryGetValue(nameKey, out string? name))
                    {
                        locale[nameKey] = $"{name} {tierTags[tier]}";
                    }
                }

                if (config.ShowItemTiersInDescription)
                {
                    string descKey = $"{id} Description";
                    string tierLine = $"Barter tier: {tierTags[tier]}";
                    if (locale.TryGetValue(descKey, out string? desc) && !string.IsNullOrEmpty(desc))
                    {
                        locale[descKey] = $"{desc}\n\n{tierLine}";
                    }
                    else
                    {
                        locale[descKey] = tierLine;
                    }
                }
            }
            return locale;
        }

        databaseService.GetLocales().Global["en"].AddTransformer(ItemTierAnnotationTransform);
        Leaves.Utils.Instance.printColor($"[Barter Economy] Annotated {itemTiers.Count} item(s) with barter tier (name={config.ShowItemTiers}, description={config.ShowItemTiersInDescription}).");
    }

    // Swaps a loose-ammo cash trade for its box, plus cartridge children (else the game sells empty boxes).
    private void ConvertAmmoTradeToBox(TraderAssort assort, Item trade, MongoId boxId, int roundsPerBox)
    {
        trade.Template = boxId;

        // Fill the box like the game does - AddCartridgesToAmmoBox splits rounds into valid stacks appended to [0].
        List<Item> boxWithCartridges = new() { trade };
        itemHelper.AddCartridgesToAmmoBox(boxWithCartridges, databaseService.GetItems()[boxId]);
        List<Item> cartridges = new();
        for (int i = 1; i < boxWithCartridges.Count; i++)
        {
            cartridges.Add(boxWithCartridges[i]);
        }
        assort.Items.AddRange(cartridges);

        if (!assortParentLookup.TryGetValue(trade.Id.ToString(), out List<Item>? children))
        {
            children = new List<Item>();
            assortParentLookup[trade.Id.ToString()] = children;
        }
        children.AddRange(cartridges);

        // Old scheme priced one round; scale to a full box (fallback for ammo too cheap to barterize).
        BarterScheme cashScheme = assort.BarterScheme[trade.Id][0][0];
        cashScheme.Count = Math.Ceiling((cashScheme.Count ?? 0) * roundsPerBox);

        // Scale the per-purchase buy cap to boxes. Stock (StackObjectsCount) is left to LimitedTraders.
        if (trade.Upd == null || roundsPerBox <= 0)
        {
            return;
        }
        if (trade.Upd.BuyRestrictionMax != null)
        {
            trade.Upd.BuyRestrictionMax = Math.Max(1, (int)Math.Ceiling((double)trade.Upd.BuyRestrictionMax.Value / roundsPerBox));
        }
    }

    // Display-only dump of the tier list to output/ when dumpTiersToFile is on. Never read back.
    private Dictionary<int, List<string>> ConvertTierListToNames(Dictionary<int, List<MongoId>> tierList)
    {
        Dictionary<MongoId, TemplateItem> items = databaseService.GetItems();
        Dictionary<int, List<string>> result = new();

        foreach ((int tier, List<MongoId> tierItems) in tierList)
        {
            List<string> named = new();
            foreach (MongoId itemId in tierItems)
            {
                string? localeName = Leaves.Utils.Instance.getLocale("en", itemId.ToString(), " Name");
                string parentId = items.TryGetValue(itemId, out TemplateItem? template) ? template.Parent.ToString() : "";
                string? parentLocaleName = Leaves.Utils.Instance.getLocale("en", parentId, " Name");
                named.Add($"[{itemId}]-[{localeName}]-[{parentLocaleName}]");
            }
            result[tier] = named;
        }

        return result;
    }

    private int GetNextValueTier(int current)
    {
        while (true)
        {
            current--;

            if (barterList.ContainsKey(current))
            {
                return current;
            }

            if (current <= 0)
            {
                return 1;
            }
        }
    }

    // Like FindAndReturnChildrenByAssort but reuses assortParentLookup (rebuilding it per call was ~10x slower).
    private List<Item> FindChildren(MongoId itemId)
    {
        List<Item> result = new();
        HashSet<MongoId> seen = new();
        Stack<string> stack = new();
        stack.Push(itemId.ToString());

        while (stack.Count > 0)
        {
            string key = stack.Pop();
            if (!assortParentLookup.TryGetValue(key, out List<Item>? children))
            {
                continue;
            }
            foreach (Item child in children)
            {
                if (seen.Add(child.Id))
                {
                    result.Add(child);
                    stack.Push(child.Id.ToString());
                }
            }
        }

        return result;
    }

    private List<Item> CopyItemWithChildren(TraderAssort assort, Item trade, List<Item> children)
    {
        HashSet<MongoId> idsToCopy = new() { trade.Id };
        foreach (Item child in children)
        {
            idsToCopy.Add(child.Id);
        }

        List<Item> newEntries = new();
        foreach (Item node in assort.Items)
        {
            if (idsToCopy.Contains(node.Id))
            {
                newEntries.Add(cloner.Clone(node)!);
            }
        }

        Leaves.Utils.Instance.generateNewItemIds(newEntries);
        return newEntries;
    }

    private void AdjustTrade(TraderAssort assort, Item trade, MongoId traderID)
    {
        List<Item> children = FindChildren(trade.Id);
        double value = GetTradeValue(assort, trade, children);
        int loyaltyLevel = GetLoyaltyLevel(assort, trade);

        if (!Leaves.Utils.Instance.isMoneyTrade(assort, trade))
        {
            // Barter copy alongside the original; never copy a quest-locked trade (the new id can't be gated).
            if (config.CopyOverrideExistingBarters && !IsQuestLocked(traderID, trade.Id))
            {
                List<Item> newEntries = CopyItemWithChildren(assort, trade, children);
                assort.Items.AddRange(newEntries);

                MongoId mainID = newEntries[0].Id;

                assort.BarterScheme[mainID] = new List<List<BarterScheme>> { GenerateBarter(value, trade.Template, loyaltyLevel) };
                assort.LoyalLevelItems[mainID] = assort.LoyalLevelItems[trade.Id];
            }
            // It's a barter and we're not overriding it - leave it alone.
            if (!config.OverrideExistingBarters)
            {
                return;
            }
        }

        if (value < config.ValueCutoff)
        {
            return;
        }
        if (config.TradeBlacklist.Contains(trade.Id))
        {
            return;
        }
        if (config.EnableItemBlacklist && TradeIncludesItems(assort, trade, config.TradeItemBlacklist))
        {
            return;
        }
        if (config.CashTradeEnabled && Random.Shared.Next(100) / 100.0 < config.CashTradeChance)
        {
            value *= config.CashTradeMultiplier;
            if (value < config.CashTradeMinValue)
            {
                value = config.CashTradeMinValue;
            }

            MongoId currencyType = "5449016a4bdc2d6f028b456f"; // Rubles
            if (config.CashTradeCurrency.TryGetValue(traderID.ToString(), out MongoId configuredCurrency))
            {
                currencyType = configuredCurrency;
                double currencyTypeValue = handbookHelper.GetTemplatePrice(currencyType);
                value = Math.Ceiling(value / currencyTypeValue);
            }

            BarterScheme scheme = new() { Count = value, Template = currencyType };
            assort.BarterScheme[trade.Id][0] = new List<BarterScheme> { scheme };
            return;
        }
        // Quest-locked trades aren't copied to cash (the fresh id can't be gated); they barterize on their own id.
        if (config.AllTradesAsCash && !IsQuestLocked(traderID, trade.Id))
        {
            List<Item> newEntries = CopyItemWithChildren(assort, trade, children);
            assort.Items.AddRange(newEntries);

            MongoId mainID = newEntries[0].Id;
            assort.LoyalLevelItems[mainID] = assort.LoyalLevelItems[trade.Id];

            // Compute the cash-copy price in its own local.
            // `value` must stay in ROUBLES: the GenerateBarter call below reads it, and a foreign-currency count here would collapse its barter to one item.
            double cashValue = value * config.CashTradeMultiplier;
            if (cashValue < config.CashTradeMinValue)
            {
                cashValue = config.CashTradeMinValue;
            }

            MongoId currencyType = "5449016a4bdc2d6f028b456f"; // Rubles
            if (config.CashTradeCurrency.TryGetValue(traderID.ToString(), out MongoId configuredCurrency))
            {
                currencyType = configuredCurrency;
                double currencyTypeValue = handbookHelper.GetTemplatePrice(currencyType);
                cashValue = Math.Ceiling(cashValue / currencyTypeValue);
            }

            BarterScheme scheme = new() { Count = cashValue, Template = currencyType };
            assort.BarterScheme[mainID] = new List<List<BarterScheme>> { new List<BarterScheme> { scheme } };
        }
        assort.BarterScheme[trade.Id][0] = GenerateBarter(value, trade.Template, loyaltyLevel);
    }

    private bool TradeIncludesItems(TraderAssort assort, Item trade, List<MongoId> itemIDs)
    {
        List<BarterScheme> scheme = assort.BarterScheme[trade.Id][0];
        foreach (BarterScheme entry in scheme)
        {
            if (itemIDs.Contains(entry.Template))
            {
                return true;
            }
        }
        return false;
    }

    private List<BarterScheme> GenerateBarter(double value, MongoId itemID, int loyaltyLevel)
    {
        List<BarterScheme> barters = new();

        int valueCredits;

        if (config.TradeValueOverrides.TryGetValue(itemID.ToString(), out int overrideValue) && overrideValue != 0)
        {
            valueCredits = overrideValue;
        }
        else
        {
            valueCredits = (int)Math.Ceiling((value / config.ValueStep) * config.ValueMultiplier) + config.ValueExtraCost;
            valueCredits = (int)Math.Round(valueCredits * config.LoyaltyMultiplier[loyaltyLevel - 1]);
        }

        if (valueCredits > config.MaxBarterValue)
        {
            valueCredits = config.MaxBarterValue;
        }

        List<string> logLines = new();
        if (config.WriteLog)
        {
            logLines.Add($"\n\tLoyalty:{loyaltyLevel} value:{valueCredits} name:{GetLocaleName(itemID)} id:{itemID}");
        }

        int count = 1;

        while (true)
        {
            int currentTier = GetNextValueTier(valueCredits);
            if (currentTier >= config.RandomTierdownStepStart)
            {
                List<int> tierdownSteps = config.RandomTierdownSteps;
                int downSteps = tierdownSteps[Random.Shared.Next(tierdownSteps.Count)];
                if (downSteps < 0)
                {
                    downSteps = 0;
                }
                for (int i = 0; i < downSteps; i++)
                {
                    currentTier = GetNextValueTier(currentTier);
                }
            }

            if (currentTier <= 0)
            {
                break;
            }

            MongoId selectedItemID;
            int attempts = 0;
            do
            {
                do
                {
                    selectedItemID = tierList.getRandomItemFromTier(barterList, currentTier);
                } while (selectedItemID == itemID && barterList[currentTier].Count > 1); // need more than one item on the tier or this loops forever.

                attempts++;
            }
            while (TradeHasItem(barters, selectedItemID) && attempts < 5);

            BarterScheme temp = new()
            {
                Template = selectedItemID,
                Count = Math.Max(Math.Floor((double)valueCredits / currentTier), 1)
            };

            // Cap this item's count at maxNumItems +/- 0-maxRandomNumItems.
            int maxItems = (Random.Shared.Next(config.MaxRandomNumItems * 2) - config.MaxRandomNumItems) + config.MaxNumItems;
            if (temp.Count > maxItems)
            {
                temp.Count = maxItems;
            }

            if (config.WriteLog)
            {
                string line = "";
                line += $"\t\tTier:{currentTier}".PadRight(11);
                line += $"\t\tcount:{temp.Count}".PadRight(11);
                line += $"\titem:{GetLocaleName(temp.Template)}".PadRight(50);
                line += $"\tid:{temp.Template}";
                logLines.Add(line);
            }
            barters.Add(temp);

            valueCredits -= (int)(currentTier * (temp.Count ?? 1));
            if (valueCredits <= 0 || count > config.MaxDifferentItems)
            {
                break;
            }
            count++;
        }

        if (config.WriteLog && logLines.Count > 0)
        {
            AddLogBlock(loyaltyLevel, logLines);
        }

        return barters;
    }

    private bool TradeHasItem(List<BarterScheme> trade, MongoId itemID)
    {
        foreach (BarterScheme entry in trade)
        {
            if (entry.Template == itemID)
            {
                return true;
            }
        }
        return false;
    }

    private double GetTradeValue(TraderAssort assort, Item trade, List<Item> children)
    {
        double total = 0;

        if (!assort.BarterScheme.ContainsKey(trade.Id))
        {
            Leaves.Utils.Instance.printColor($"Item {trade.Id} does not have a barter scheme. Defaulting to item value 1.", LogTextColor.Red);
            return 1;
        }

        foreach (BarterScheme barter in assort.BarterScheme[trade.Id][0])
        {
            total += GetItemValue(barter.Template) * (barter.Count ?? 0);
        }

        // Item + children; children multiply by stack count, the root doesn't (its count is stock).
        double itemValue = GetItemValue(trade.Template);
        foreach (Item child in children)
        {
            itemValue += GetItemValue(child.Template) * (child.Upd?.StackObjectsCount ?? 1);
        }
        if (itemValue > total)
        {
            total = itemValue;
        }

        Dictionary<MongoId, TemplateItem> itemDB = databaseService.GetItems();
        if (itemDB.TryGetValue(trade.Template, out TemplateItem? template) && config.ValueMultipliers.TryGetValue(template.Parent.ToString(), out MultiEntry? valueMultiplier))
        {
            total *= valueMultiplier.Multi;
        }

        double maxValue = itemValue * config.MaxValueOverHandbookMultiplier;
        if (total > maxValue)
        {
            total = maxValue;
        }

        return Math.Round(total);
    }

    private void ModifyTrader(MongoId traderID)
    {
        Dictionary<MongoId, TemplateItem> itemDB = databaseService.GetItems();
        Dictionary<MongoId, Trader> traders = databaseService.GetTraders();

        if (config.SaveLoadBarters && savedBarters.TryGetValue(traderID, out TraderAssort? saved))
        {
            long lockSeconds = (long)(config.SaveLoadLockHours * 3600.0);
            long lastReroll = 0;
            savedBarterTimes.TryGetValue(traderID.ToString(), out lastReroll);
            long elapsed = timeUtil.GetTimeStamp() - lastReroll;

            // lockSeconds <= 0 freezes forever.
            // Otherwise the saved barters stay put until the lock window elapses, then fall through to regeneration - which lands on this resupply, since OnUpdate only calls ModifyTrader on an actual resupply.
            if (lockSeconds <= 0 || elapsed < lockSeconds)
            {
                Leaves.Utils.Instance.printColor("[Barter Economy] Applied saved barters for: " + traderID);
                traders[traderID].Assort = cloner.Clone(saved)!;
                return;
            }

            Leaves.Utils.Instance.printColor($"[Barter Economy] Lock window elapsed for {traderID} ({elapsed / 3600}h >= {config.SaveLoadLockHours}h) - re-rolling barters.", LogTextColor.Yellow);
        }

        if (traders[traderID].Assort.Items == null)
        {
            return;
        }

        traders[traderID].Assort.Items = cloner.Clone(originalTraders[traderID].Assort.Items)!;

        // We never touch Trader.QuestAssort - the merged gating cache hydrates once, so runtime edits are ignored.

        // Built once per trader, reused by every AdjustTrade/FindChildren below.
        assortParentLookup = new Dictionary<string, List<Item>>();
        foreach (Item item in traders[traderID].Assort.Items)
        {
            if (item.ParentId == null)
            {
                continue;
            }
            if (!assortParentLookup.TryGetValue(item.ParentId, out List<Item>? siblings))
            {
                siblings = new List<Item>();
                assortParentLookup[item.ParentId] = siblings;
            }
            siblings.Add(item);
        }

        // Snapshot via ToList() - AdjustTrade appends copies to Items, and mutating mid-foreach throws.
        foreach (Item item in traders[traderID].Assort.Items.ToList())
        {
            if (item.ParentId != "hideout")
            {
                continue;
            }

            if (!itemDB.TryGetValue(item.Template, out TemplateItem? template))
            {
                Leaves.Utils.Instance.printColor("[Barter Economy] Found trade with item that doesn't exist in the global database. This is most likely caused by a mod doing something wrong.", LogTextColor.Red);
                Leaves.Utils.Instance.printColor($"[Barter Economy] Item ID of broken trade is: {item.Template} TradeID: {item.Id}", LogTextColor.Red);
                continue;
            }

            if (template.Parent == "")
            {
                Leaves.Utils.Instance.printColor("[Barter Economy] Found trade with item in the global database that has an invalid _parent entry. This is most likely caused by a mod doing something wrong.", LogTextColor.Red);
                Leaves.Utils.Instance.printColor($"[Barter Economy] Item ID of broken item is: {item.Template} TradeID: {item.Id}", LogTextColor.Red);
                continue;
            }

            // Swap loose ammo for its box before the blacklist below (Ammo stays blacklisted, the box doesn't).
            if (config.AmmoInBoxes && template.Parent == AmmoClass
                && Leaves.Utils.Instance.isMoneyTrade(traders[traderID].Assort, item)
                && ammoToBox.TryGetValue(item.Template, out AmmoBoxInfo? box))
            {
                ConvertAmmoTradeToBox(traders[traderID].Assort, item, box!.BoxId, box.RoundsPerBox);
                template = itemDB[box.BoxId]; // downstream checks now see the box
            }

            if (config.TraderCategoriesBlacklist.Contains(template.Parent))
            {
                continue;
            }

            if (config.SpecificItemBlacklist.Contains(item.Template))
            {
                continue;
            }
            AdjustTrade(traders[traderID].Assort, item, traderID);
        }

        if (config.SaveLoadBarters)
        {
            savedBarters[traderID] = cloner.Clone(traders[traderID].Assort)!;
            savedBarterTimes[traderID.ToString()] = timeUtil.GetTimeStamp();

            Leaves.Utils.Instance.saveJsonFile(savedBarters[traderID], modPath, System.IO.Path.Combine("_savedbarters", $"{traderID}.json"));
            Leaves.Utils.Instance.saveJsonFile(savedBarterTimes, modPath, System.IO.Path.Combine("_savedbarters", RerollTimesFile));
            Leaves.Utils.Instance.printColor("[Barter Economy] Saved barters for: " + traderID);
        }
    }

    private void WriteResult<T>(string prefix, T data)
    {
        Leaves.Utils.Instance.writeTimestampedResult(outputFolder, prefix, jsonUtil.Serialize(data, true) ?? "");
    }

    private int GetLoyaltyLevel(TraderAssort assort, Item trade)
    {
        if (!assort.LoyalLevelItems.TryGetValue(trade.Id, out int level) || level == 0)
        {
            Leaves.Utils.Instance.printColor($"Item {trade.Id} does not have a loyalty level. Defaulting to 1.", LogTextColor.Red);
            return 1;
        }
        return level;
    }

    // True if the trade is quest-gated. Copies can't inherit gating (merged cache hydrates once), so we don't copy them.
    private bool IsQuestLocked(MongoId traderID, MongoId tradeId)
    {
        var questAssort = databaseService.GetTraders()[traderID].QuestAssort;
        if (questAssort == null)
        {
            return false;
        }
        foreach (Dictionary<MongoId, MongoId> bucket in questAssort.Values)
        {
            if (bucket.ContainsKey(tradeId))
            {
                return true;
            }
        }
        return false;
    }

    // Header lines (no loyalty level) - always kept first; per-trade lines go through AddLogBlock.
    private void WriteLogFileLine(string line)
    {
        logPreamble.Add(line);
    }

    // One trade's log lines, tagged with loyalty level so FlushLogFile can group by loyalty.
    private void AddLogBlock(int loyaltyLevel, List<string> lines)
    {
        logBlocks.Add((loyaltyLevel, lines));
    }

    // Flushes all buffered lines in one append, trades sorted into loyalty order.
    private void FlushLogFile()
    {
        if (logPreamble.Count == 0 && logBlocks.Count == 0)
        {
            return;
        }

        // Bucket by loyalty (SortedDictionary keeps key order; insertion order stays stable within a key).
        SortedDictionary<int, List<List<string>>> byLoyalty = new();
        foreach ((int Loyalty, List<string> Lines) block in logBlocks)
        {
            if (!byLoyalty.TryGetValue(block.Loyalty, out List<List<string>>? bucket))
            {
                bucket = new List<List<string>>();
                byLoyalty[block.Loyalty] = bucket;
            }
            bucket.Add(block.Lines);
        }

        List<string> allLines = new(logPreamble);
        foreach (List<List<string>> bucket in byLoyalty.Values)
        {
            foreach (List<string> lines in bucket)
            {
                allLines.AddRange(lines);
            }
        }

        string content = string.Join("\n", allLines) + "\n";
        Leaves.Utils.Instance.appendFile(content, System.IO.Path.Combine(outputFolder, currentLogFile));

        logPreamble.Clear();
        logBlocks.Clear();
    }

    private string GetLocaleName(MongoId id)
    {
        return Leaves.Utils.Instance.getLocale(locale, id.ToString(), " Name") ?? "Unknown";
    }
}
