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

// Web-UI / HTTP-API surface: read-only reports served by BarterEconomyApi. Wire-shape DTOs are at the bottom.
public partial class BarterEconomy
{
    // Flat item-id -> tier map other mods fetch over HTTP. Empty until the first trader update builds it.
    public Dictionary<string, int> GetItemTiers()
    {
        Dictionary<string, int> tiers = new();
        if (barterList == null)
        {
            return tiers;
        }
        foreach ((int tier, List<MongoId> tierItems) in barterList)
        {
            foreach (MongoId id in tierItems)
            {
                tiers[id.ToString()] = tier;
            }
        }
        return tiers;
    }

    // Read-only status snapshot for the web UI: each managed trader's next resupply and save-load lock window.
    // Empty before the first trader update. All timestamps are unix seconds in Now's clock.
    public BarterStatusReport GetTraderStatuses()
    {
        BarterStatusReport report = new()
        {
            Now = timeUtil.GetTimeStamp(),
            SaveLoadEnabled = config.SaveLoadBarters,
            LockHours = config.SaveLoadLockHours,
            Traders = new List<TraderStatus>(),
        };

        // Hit before the first OnUpdate: nothing to report yet.
        if (tradersToUpdate == null)
        {
            return report;
        }

        Dictionary<MongoId, Trader> traders = databaseService.GetTraders();
        long lockSeconds = (long)(config.SaveLoadLockHours * 3600.0);

        // Per-trader resupply period (seconds). The cycle length is randomized in [min, max], so the next resupply is only bounded, not known.
        // Reading UpdateTime here yields the effective period, since TreasureBox mutates it at load.
        Dictionary<string, (long Min, long Max)> resupplyPeriods = new();
#pragma warning disable CS0618
        TraderConfig traderConfig = configServer.GetConfig<TraderConfig>();
#pragma warning restore CS0618
        foreach (UpdateTime updateTime in traderConfig.UpdateTime)
        {
            resupplyPeriods[updateTime.TraderId.ToString()] = (updateTime.Seconds.Min, updateTime.Seconds.Max);
        }

        foreach (MongoId traderID in tradersToUpdate)
        {
            if (!traders.TryGetValue(traderID, out Trader? trader))
            {
                continue;
            }

            long lastReroll = 0;
            savedBarterTimes.TryGetValue(traderID.ToString(), out lastReroll);

            // When the saved barters become eligible to re-roll (the next resupply after this time re-rolls them).
            // Null when save-load is off, lockHours <= 0 (freeze forever), or nothing's rolled yet.
            long? lockEnds = null;
            if (config.SaveLoadBarters && lockSeconds > 0 && lastReroll > 0)
            {
                lockEnds = lastReroll + lockSeconds;
            }

            long resupplyMin = 0;
            long resupplyMax = 0;
            if (resupplyPeriods.TryGetValue(traderID.ToString(), out (long Min, long Max) period))
            {
                resupplyMin = period.Min;
                resupplyMax = period.Max;
            }

            TraderStatus status = new()
            {
                Id = traderID.ToString(),
                Nickname = Leaves.Utils.Instance.getTraderNickname(traderID),
                Avatar = Leaves.Utils.Instance.getTraderAvatar(traderID),
                NextResupply = trader.Base?.NextResupply ?? 0,
                LastReroll = lastReroll,
                LockEnds = lockEnds,
                ResupplyMin = resupplyMin,
                ResupplyMax = resupplyMax,
            };
            report.Traders.Add(status);
        }

        // Reorder into in-game trader order; modded/unknown traders keep their spot after the vanilla set.
        var orderedTraders = new List<TraderStatus>(report.Traders.Count);
        foreach (string id in TraderDisplayOrder)
        {
            foreach (var status in report.Traders)
            {
                if (status.Id == id)
                {
                    orderedTraders.Add(status);
                }
            }
        }
        foreach (var status in report.Traders)
        {
            if (!IsInDisplayOrder(status.Id))
            {
                orderedTraders.Add(status);
            }
        }
        report.Traders = orderedTraders;

        return report;
    }

    // Wire shapes for GetTraderStatuses - serialized by BarterEconomyApi over /api/status.
    public class BarterStatusReport
    {
        [JsonPropertyName("now")] public long Now { get; set; }
        [JsonPropertyName("saveLoadEnabled")] public bool SaveLoadEnabled { get; set; }
        [JsonPropertyName("lockHours")] public double LockHours { get; set; }
        [JsonPropertyName("traders")] public List<TraderStatus> Traders { get; set; } = new();
    }

    public class TraderStatus
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
        [JsonPropertyName("avatar")] public string? Avatar { get; set; }
        [JsonPropertyName("nextResupply")] public long NextResupply { get; set; }
        [JsonPropertyName("lastReroll")] public long LastReroll { get; set; }
        [JsonPropertyName("lockEnds")] public long? LockEnds { get; set; }
        // Randomized resupply cycle bounds (seconds). Min==Max would mean a fixed cycle; here they differ.
        [JsonPropertyName("resupplyMin")] public long ResupplyMin { get; set; }
        [JsonPropertyName("resupplyMax")] public long ResupplyMax { get; set; }
    }

    // Handbook lookups for the Trades page, built once (the handbook is static after server load).
    private Dictionary<string, string>? handbookItemCategory;       // item tpl -> its (leaf) category id
    private Dictionary<string, HandbookCategory>? handbookCategoriesById;
    private HashSet<string>? handbookIconFiles;                     // filenames present in images/handbook
    private readonly object handbookCacheLock = new();

    private void EnsureHandbookMaps()
    {
        lock (handbookCacheLock)
        {
            if (handbookItemCategory != null)
            {
                return;
            }
            var handbook = databaseService.GetHandbook();
            var itemCategory = new Dictionary<string, string>();
            foreach (var hbItem in handbook.Items)
            {
                itemCategory[hbItem.Id.ToString()] = hbItem.ParentId.ToString();
            }
            var categoriesById = new Dictionary<string, HandbookCategory>();
            foreach (var category in handbook.Categories)
            {
                categoriesById[category.Id.ToString()] = category;
            }

            // List the real icon files on disk so ValidIcon can drop any icon that would 404.
            // A blank category Icon resolves to a bare "/files/handbook/", and that 404 gets logged as a red [UNHANDLED].
            var iconFiles = new HashSet<string>();
            // modPath is …\SPT\user\mods\Leaves-BarterEconomy; three parents up is the SPT root.
            string? sptRoot = System.IO.Directory.GetParent(modPath)?.Parent?.Parent?.FullName;
            string iconDir = System.IO.Path.Combine(sptRoot ?? "", "SPT_Data", "images", "handbook");
            if (System.IO.Directory.Exists(iconDir))
            {
                foreach (string file in System.IO.Directory.GetFiles(iconDir))
                {
                    iconFiles.Add(System.IO.Path.GetFileName(file));
                }
            }

            handbookItemCategory = itemCategory;
            handbookCategoriesById = categoriesById;
            handbookIconFiles = iconFiles;
        }
    }

    // A handbook Icon URL, or null when it wouldn't resolve (blank filename or file not on disk) so the client never 404s.
    // If we couldn't list the icon dir (empty set), only the blank-filename case is dropped.
    private string? ValidIcon(string? iconUrl)
    {
        if (string.IsNullOrEmpty(iconUrl))
        {
            return null;
        }
        string file = iconUrl.Substring(iconUrl.LastIndexOf('/') + 1);
        if (file.Length == 0)
        {
            return null;
        }
        if (handbookIconFiles != null && handbookIconFiles.Count > 0 && !handbookIconFiles.Contains(file))
        {
            return null;
        }
        return iconUrl;
    }

    // The handbook category chain for an item template, ordered leaf -> top (e.g. [Assault rifles, Weapons]).
    // Empty when the item isn't in the handbook. Guards against a cyclic ParentId with the seen set.
    private List<HandbookCategory> CategoryChain(string tpl)
    {
        var chain = new List<HandbookCategory>();
        if (handbookItemCategory == null || handbookCategoriesById == null)
        {
            return chain;
        }
        if (!handbookItemCategory.TryGetValue(tpl, out var catId))
        {
            return chain;
        }
        var seen = new HashSet<string>();
        while (!string.IsNullOrEmpty(catId) && seen.Add(catId)
               && handbookCategoriesById.TryGetValue(catId, out var category))
        {
            chain.Add(category);
            catId = category.ParentId.ToString();
        }
        return chain;
    }

    // Resolves a trader id STRING to its trades, false when no trader matches. A malformed id just matches nothing.
    // MongoId.Equals(string) doesn't throw the way its constructor does, so no shape check is needed.
    public bool TryGetTraderTrades(string traderId, out TraderTradesReport report)
    {
        foreach (MongoId id in databaseService.GetTraders().Keys)
        {
            if (id.Equals(traderId))
            {
                report = GetTraderTrades(id);
                return true;
            }
        }
        report = new TraderTradesReport();
        return false;
    }

    // Per-trader trade listing for the Trades page, read from the LIVE assort so it reflects current barters.
    // Every trade is included (item barters and cash), tagged Cash or not, under its handbook category with its payment scheme resolved to names + counts.
    public TraderTradesReport GetTraderTrades(MongoId traderID)
    {
        EnsureHandbookMaps();

        var report = new TraderTradesReport
        {
            Id = traderID.ToString(),
            Nickname = DisplayNickname(traderID),
            Avatar = Leaves.Utils.Instance.getTraderAvatar(traderID),
            Trades = new List<TradeInfo>(),
        };

        var traders = databaseService.GetTraders();
        if (!traders.TryGetValue(traderID, out var trader) || trader.Assort?.Items == null)
        {
            return report;
        }
        var assort = trader.Assort;
        var moneyTpls = Money.GetMoneyTpls();

        foreach (var item in assort.Items)
        {
            // Root trade items only; children are the attachments/cartridges hanging off them.
            if (item.ParentId != "hideout")
            {
                continue;
            }

            string tpl = item.Template.ToString();
            var trade = new TradeInfo
            {
                Id = item.Id.ToString(),
                ItemId = tpl,
                Name = Leaves.Utils.Instance.getLocale("en", tpl, " Name") ?? tpl,
                Short = Leaves.Utils.Instance.getLocale("en", tpl, " ShortName") ?? "",
                Count = item.Upd?.StackObjectsCount ?? 1,
                Stock = item.Upd?.StackObjectsCount ?? 0,
                Unlimited = item.Upd?.UnlimitedCount == true,
                Loyalty = assort.LoyalLevelItems.TryGetValue(item.Id, out var loyaltyLevel) ? loyaltyLevel : 0,
                Cash = Leaves.Utils.Instance.isMoneyTrade(assort, item),
                Payment = new List<PaymentPart>(),
            };

            // Categorize by the sold item's handbook chain: top-level group + its leaf sub-category.
            var chain = CategoryChain(tpl);
            if (chain.Count > 0)
            {
                var top = chain[chain.Count - 1];
                trade.TopId = top.Id.ToString();
                trade.TopName = Leaves.Utils.Instance.getLocale("en", top.Id.ToString()) ?? top.Id.ToString();
                trade.TopIcon = ValidIcon(top.Icon);
                if (chain.Count > 1)
                {
                    var leaf = chain[0];
                    trade.SubId = leaf.Id.ToString();
                    trade.SubName = Leaves.Utils.Instance.getLocale("en", leaf.Id.ToString()) ?? leaf.Id.ToString();
                    trade.SubIcon = ValidIcon(leaf.Icon);
                }
            }

            // The payment scheme (first/only option): each part is an item-or-currency + a count.
            if (assort.BarterScheme.TryGetValue(item.Id, out var schemes) && schemes.Count > 0)
            {
                foreach (var part in schemes[0])
                {
                    string partTpl = part.Template.ToString();
                    trade.Payment.Add(new PaymentPart
                    {
                        Id = partTpl,
                        Name = Leaves.Utils.Instance.getLocale("en", partTpl, " Name") ?? partTpl,
                        Short = Leaves.Utils.Instance.getLocale("en", partTpl, " ShortName") ?? "",
                        Count = part.Count ?? 0,
                        Currency = moneyTpls.Contains(part.Template),
                    });
                }
            }

            report.Trades.Add(trade);
        }

        return report;
    }

    private static readonly string FenceId = "579dc571d53a0658a154fbec";
    private static readonly string RefId = "6617beeaa9cfa777ca915b7c";

    private string DisplayNickname(MongoId traderID)
    {
        //Ref shows as "Arena" in some places, but is "Ref" in-game. No idea why.
        if (traderID.ToString() == RefId)
        {
            return "Ref";
        }
        return Leaves.Utils.Instance.getTraderNickname(traderID);
    }

    // In-game trader order, used to sort the web UI's trader lists to match the game.
    // Any trader not listed here (a modded one) sorts after this vanilla set, keeping its original relative order.
    private static readonly string[] TraderDisplayOrder =
    {
        "54cb50c76803fa8b248b4571", // Prapor
        "54cb57776803fa99248b456e", // Therapist
        "58330581ace78e27b8b10cee", // Skier
        "5935c25fb3acc3127c3d8cd9", // Peacekeeper
        "5a7c2eca46aef81a7ca2145d", // Mechanic
        "5ac3b934156ae10c4430e83c", // Ragman
        "5c0647fdd443bc2504c2d371", // Jaeger
        "6617beeaa9cfa777ca915b7c", // Ref
    };

    private static bool IsInDisplayOrder(string traderId)
    {
        foreach (string id in TraderDisplayOrder)
        {
            if (id == traderId)
            {
                return true;
            }
        }
        return false;
    }

    public List<TraderListEntry> GetAllTradersWithTrades()
    {
        var list = new List<TraderListEntry>();
        foreach (var pair in databaseService.GetTraders())
        {
            // Fence is excluded from the web UI. huge randomized cash-only assort we don't touch.
            if (pair.Key.ToString() == FenceId)
            {
                continue;
            }
            var trader = pair.Value;
            if (trader.Assort?.Items == null)
            {
                continue;
            }
            int tradeCount = 0;
            foreach (var item in trader.Assort.Items)
            {
                if (item.ParentId == "hideout")
                {
                    tradeCount++;
                }
            }
            if (tradeCount == 0)
            {
                continue;
            }
            list.Add(new TraderListEntry
            {
                Id = pair.Key.ToString(),
                Nickname = DisplayNickname(pair.Key),
                Avatar = Leaves.Utils.Instance.getTraderAvatar(pair.Key),
                TradeCount = tradeCount,
                Managed = tradersToUpdate != null && tradersToUpdate.Contains(pair.Key),
            });
        }

        // Reorder into in-game trader order; modded/unknown traders keep their spot after the vanilla set.
        var ordered = new List<TraderListEntry>(list.Count);
        foreach (string id in TraderDisplayOrder)
        {
            foreach (var entry in list)
            {
                if (entry.Id == id)
                {
                    ordered.Add(entry);
                }
            }
        }
        foreach (var entry in list)
        {
            if (!IsInDisplayOrder(entry.Id))
            {
                ordered.Add(entry);
            }
        }
        return ordered;
    }

    // Cross-trader trade search. Search-only: an empty query returns nothing.
    // Case-insensitive substring over each trade's item name/short, payment item names, category names, and trader nickname.
    // Results carry their trader so the page can group them. Capped at `limit`; `Truncated` says the cap was hit.
    public TradeSearchReport SearchTrades(string query, int limit)
    {
        var report = new TradeSearchReport { Query = query, Results = new List<TradeSearchResult>() };
        if (string.IsNullOrWhiteSpace(query))
        {
            return report;
        }
        string needle = query.ToLowerInvariant();

        foreach (var entry in GetAllTradersWithTrades())
        {
            var traderTrades = GetTraderTrades(entry.Id);
            foreach (var trade in traderTrades.Trades)
            {
                if (!TradeMatches(trade, traderTrades.Nickname, needle))
                {
                    continue;
                }
                report.Results.Add(new TradeSearchResult
                {
                    TraderId = traderTrades.Id,
                    TraderNickname = traderTrades.Nickname,
                    TraderAvatar = traderTrades.Avatar,
                    Trade = trade,
                });
                if (report.Results.Count >= limit)
                {
                    report.Truncated = true;
                    return report;
                }
            }
        }
        return report;
    }

    // All trades across traders whose SOLD item is exactly `itemTpl` - i.e. "where do I get this item?".
    // Powers the Search page's barter-chain feature: clicking a payment item looks up who sells it.
    // Same result shape as SearchTrades, grouped by trader on the client. Capped at `limit`.
    public TradeSearchReport GetTradesSellingItem(string itemTpl, int limit)
    {
        var report = new TradeSearchReport { Query = itemTpl, Results = new List<TradeSearchResult>() };
        if (string.IsNullOrWhiteSpace(itemTpl))
        {
            return report;
        }
        foreach (var entry in GetAllTradersWithTrades())
        {
            var traderTrades = GetTraderTrades(entry.Id);
            foreach (var trade in traderTrades.Trades)
            {
                if (trade.ItemId != itemTpl)
                {
                    continue;
                }
                report.Results.Add(new TradeSearchResult
                {
                    TraderId = traderTrades.Id,
                    TraderNickname = traderTrades.Nickname,
                    TraderAvatar = traderTrades.Avatar,
                    Trade = trade,
                });
                if (report.Results.Count >= limit)
                {
                    report.Truncated = true;
                    return report;
                }
            }
        }
        return report;
    }

    // Case-insensitive substring test over everything worth matching on a trade. `needle` is already lowercased.
    private static bool TradeMatches(TradeInfo trade, string nickname, string needle)
    {
        if (trade.Name.ToLowerInvariant().Contains(needle))
        {
            return true;
        }
        if (trade.Short.ToLowerInvariant().Contains(needle))
        {
            return true;
        }
        if ((trade.TopName ?? "").ToLowerInvariant().Contains(needle))
        {
            return true;
        }
        if ((trade.SubName ?? "").ToLowerInvariant().Contains(needle))
        {
            return true;
        }
        if (nickname.ToLowerInvariant().Contains(needle))
        {
            return true;
        }
        foreach (var part in trade.Payment)
        {
            if (part.Name.ToLowerInvariant().Contains(needle))
            {
                return true;
            }
            if (part.Short.ToLowerInvariant().Contains(needle))
            {
                return true;
            }
        }
        return false;
    }

    // Wire shapes for GetAllTradersWithTrades / SearchTrades.
    public class TraderListEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
        [JsonPropertyName("avatar")] public string? Avatar { get; set; }
        [JsonPropertyName("tradeCount")] public int TradeCount { get; set; }
        [JsonPropertyName("managed")] public bool Managed { get; set; }
    }

    public class TradeSearchReport
    {
        [JsonPropertyName("query")] public string Query { get; set; } = "";
        [JsonPropertyName("truncated")] public bool Truncated { get; set; }
        [JsonPropertyName("results")] public List<TradeSearchResult> Results { get; set; } = new();
    }

    public class TradeSearchResult
    {
        [JsonPropertyName("traderId")] public string TraderId { get; set; } = "";
        [JsonPropertyName("traderNickname")] public string TraderNickname { get; set; } = "";
        [JsonPropertyName("traderAvatar")] public string? TraderAvatar { get; set; }
        [JsonPropertyName("trade")] public TradeInfo Trade { get; set; } = new();
    }

    // Wire shapes for GetTraderTrades.
    public class TraderTradesReport
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
        [JsonPropertyName("avatar")] public string? Avatar { get; set; }
        [JsonPropertyName("trades")] public List<TradeInfo> Trades { get; set; } = new();
    }

    public class TradeInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("short")] public string Short { get; set; } = "";
        [JsonPropertyName("count")] public double Count { get; set; }
        [JsonPropertyName("stock")] public double Stock { get; set; }
        [JsonPropertyName("unlimited")] public bool Unlimited { get; set; }
        [JsonPropertyName("loyalty")] public int Loyalty { get; set; }
        [JsonPropertyName("cash")] public bool Cash { get; set; }
        [JsonPropertyName("topId")] public string? TopId { get; set; }
        [JsonPropertyName("topName")] public string? TopName { get; set; }
        [JsonPropertyName("topIcon")] public string? TopIcon { get; set; }
        [JsonPropertyName("subId")] public string? SubId { get; set; }
        [JsonPropertyName("subName")] public string? SubName { get; set; }
        [JsonPropertyName("subIcon")] public string? SubIcon { get; set; }
        [JsonPropertyName("payment")] public List<PaymentPart> Payment { get; set; } = new();
    }

    public class PaymentPart
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("short")] public string Short { get; set; } = "";
        [JsonPropertyName("count")] public double Count { get; set; }
        [JsonPropertyName("currency")] public bool Currency { get; set; }
    }
}
