using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_BarterEconomy;

public record MultiEntry
{
    [JsonPropertyName("multi")]
    public required double Multi { get; set; }
}

public record BarterEconomyConfig
{
    [JsonPropertyName("dumpTiersToFile")]
    public required bool DumpTiersToFile { get; set; }

    [JsonPropertyName("tradersToBarter")]
    public required List<MongoId> TradersToBarter { get; set; }

    [JsonPropertyName("barterCategories")]
    public required Dictionary<string, MultiEntry> BarterCategories { get; set; }

    [JsonPropertyName("valueMultipliers")]
    public required Dictionary<string, MultiEntry> ValueMultipliers { get; set; }

    [JsonPropertyName("tiers")]
    public required List<int> Tiers { get; set; }

    [JsonPropertyName("loyaltyMultiplier")]
    public required List<double> LoyaltyMultiplier { get; set; }

    [JsonPropertyName("traderCategoriesBlacklist")]
    public required List<MongoId> TraderCategoriesBlacklist { get; set; }

    [JsonPropertyName("tradeBlacklist")]
    public required List<MongoId> TradeBlacklist { get; set; }

    [JsonPropertyName("enableItemBlacklist")]
    public required bool EnableItemBlacklist { get; set; }

    [JsonPropertyName("onlyVanillaBarterItems")]
    public bool OnlyVanillaBarterItems { get; set; } = true;

    [JsonPropertyName("tradeItemBlacklist")]
    public required List<MongoId> TradeItemBlacklist { get; set; }

    [JsonPropertyName("tradeValueOverrides")]
    public required Dictionary<string, int> TradeValueOverrides { get; set; }

    [JsonPropertyName("maxNumItems")]
    public required int MaxNumItems { get; set; }

    [JsonPropertyName("maxRandomNumItems")]
    public required int MaxRandomNumItems { get; set; }

    [JsonPropertyName("maxDifferentItems")]
    public required int MaxDifferentItems { get; set; }

    [JsonPropertyName("valueStep")]
    public required double ValueStep { get; set; }

    [JsonPropertyName("maxBarterValue")]
    public required int MaxBarterValue { get; set; }

    [JsonPropertyName("valueExtraCost")]
    public required int ValueExtraCost { get; set; }

    [JsonPropertyName("valueMultiplier")]
    public required double ValueMultiplier { get; set; }

    [JsonPropertyName("randomTierdownStepStart")]
    public required int RandomTierdownStepStart { get; set; }

    [JsonPropertyName("randomTierdownSteps")]
    public required List<int> RandomTierdownSteps { get; set; }

    [JsonPropertyName("valueCutoff")]
    public required double ValueCutoff { get; set; }

    [JsonPropertyName("overrideExistingBarters")]
    public required bool OverrideExistingBarters { get; set; }

    [JsonPropertyName("copyOverrideExistingBarters")]
    public required bool CopyOverrideExistingBarters { get; set; }

    [JsonPropertyName("maxValueOverHandbookMultiplier")]
    public required double MaxValueOverHandbookMultiplier { get; set; }

    [JsonPropertyName("writeLog")]
    public required bool WriteLog { get; set; }

    [JsonPropertyName("writeLogLocale")]
    public required string WriteLogLocale { get; set; }

    [JsonPropertyName("cashTradeEnabled")]
    public required bool CashTradeEnabled { get; set; }

    [JsonPropertyName("cashTradeChance")]
    public required double CashTradeChance { get; set; }

    [JsonPropertyName("cashTradeMinValue")]
    public required double CashTradeMinValue { get; set; }

    [JsonPropertyName("cashTradeMultiplier")]
    public required double CashTradeMultiplier { get; set; }

    [JsonPropertyName("cashTradeCurrency")]
    public required Dictionary<string, MongoId> CashTradeCurrency { get; set; }

    [JsonPropertyName("allTradesAsCash")]
    public required bool AllTradesAsCash { get; set; }

    [JsonPropertyName("specificItemBlacklist")]
    public required List<MongoId> SpecificItemBlacklist { get; set; }

    [JsonPropertyName("saveloadBarters")]
    public required bool SaveLoadBarters { get; set; }

    // Hours saved barters stay frozen before the next resupply re-rolls and re-locks them. <= 0 freezes forever.
    // Not required so old configs default to 72h.
    [JsonPropertyName("saveloadLockHours")]
    public double SaveLoadLockHours { get; set; } = 72;

    [JsonPropertyName("ammoInBoxes")]
    public bool AmmoInBoxes { get; set; } = true;

    [JsonPropertyName("ammoBoxShowStats")]
    public bool AmmoBoxShowStats { get; set; } = true;

    // Append the payment tier to each barter-tier item's name (e.g. "Sledgehammer [T15]") so players can eyeball its barter value.
    [JsonPropertyName("showItemTiers")]
    public bool ShowItemTiers { get; set; } = true;

    // With showItemTiers on: color the "[T#]" tag by tier rank (yellow through green/teal/blue/purple to red),
    // spaced by rank not raw value so crowded low tiers stay distinct. Uses rich text, which may not render on name labels.
    [JsonPropertyName("colorItemTiers")]
    public bool ColorItemTiers { get; set; } = true;

    // Also add a "Barter tier: [T#]" line to each item's description. Descriptions render rich text reliably
    // (name labels may not), so this is the fallback. Independent of showItemTiers.
    [JsonPropertyName("showItemTiersInDescription")]
    public bool ShowItemTiersInDescription { get; set; } = false;
}
