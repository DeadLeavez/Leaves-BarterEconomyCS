using System.Reflection;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers.Http;

namespace Leaves_BarterEconomy;

// The BarterEconomy web API - a small read-only surface. Endpoints:
//
//   GET /bartereconomy/ui                          -> the Timers web page (assets/ui/bartereconomy.html)
//   GET /bartereconomy/ui/tiers                    -> the Tier-list web page (assets/ui/tiers.html)
//   GET /bartereconomy/ui/trades                   -> the Trades web page (assets/ui/trades.html)
//   GET /bartereconomy/ui/search                   -> the cross-trader Search page (assets/ui/search.html)
//   GET /bartereconomy/ui/pinned                   -> the Pinned-barters page (assets/ui/pinned.html; pins live in localStorage)
//   GET /bartereconomy/ui/common.js                -> shared client helpers for Trades + Search + Pinned (assets/ui/common.txt)
//   GET /bartereconomy/api/status                  -> per-trader next-resupply + barter save-load lock window
//   GET /bartereconomy/api/traders                 -> every trader that has trades (id/nickname/avatar/count/managed)
//   GET /bartereconomy/api/trades?trader=<id>      -> one trader's trades, categorized, with payment schemes
//   GET /bartereconomy/api/trades/search?q=<text>  -> cross-trader trade search (search-only)
//   GET /bartereconomy/api/trades/sells?item=<tpl> -> who sells this item (barter-chain lookups)
//   GET /bartereconomy/api/tiers                   -> { "<itemId>": <tier>, ... }   the shared payment tier list
//
// /api/tiers is a SOFT dependency: LootFuckery's dump viewer fetches it to annotate items, and just drops the tier columns if this mod isn't present.
[Injectable]
public class BarterEconomyApi(BarterEconomy barterEconomy) : IHttpListener, IOnLoad
{
    private const string Prefix = "/bartereconomy/";

    // Publish our routes to the suite-wide Dev directory (served at /webui/dev/routes). Utils.Instance is set well before any OnLoad.
    public Task OnLoad()
    {
        var routes = new List<Leaves.RouteInfo>
        {
            // The /bartereconomy/ui* page + asset files are served by the Leaves-WebUI mod now, which
            // registers them under "Web UI (static files)". Left commented so the move is visible.
            //   new Leaves.RouteInfo("GET", "/bartereconomy/ui", "Timers page.", "ui"),
            //   new Leaves.RouteInfo("GET", "/bartereconomy/ui/tiers", "Tier-list page.", "ui"),
            //   new Leaves.RouteInfo("GET", "/bartereconomy/ui/trades", "Trades page.", "ui"),
            //   new Leaves.RouteInfo("GET", "/bartereconomy/ui/search", "Cross-trader Search page.", "ui"),
            //   new Leaves.RouteInfo("GET", "/bartereconomy/ui/pinned", "Pinned-barters page (pins live in localStorage).", "ui"),
            //   new Leaves.RouteInfo("GET", "/bartereconomy/ui/common.js", "Shared client helpers for Trades + Search + Pinned.", "internal"),
            //   new Leaves.RouteInfo("GET", "/bartereconomy/ui/nav.json", "Nav-rail manifest for the shared rail.", "internal"),
            new Leaves.RouteInfo("GET", "/bartereconomy/api/status", "Per-trader next-resupply + barter save-load lock window.", "api"),
            new Leaves.RouteInfo("GET", "/bartereconomy/api/traders", "Every trader that has trades (id/nickname/avatar/count/managed).", "api"),
            new Leaves.RouteInfo("GET", "/bartereconomy/api/trades?trader=", "One trader's trades, categorized, with payment schemes.", "api"),
            new Leaves.RouteInfo("GET", "/bartereconomy/api/trades/search?q=&limit=", "Cross-trader trade search.", "api"),
            new Leaves.RouteInfo("GET", "/bartereconomy/api/trades/sells?item=", "Who sells this item (barter-chain lookups).", "api"),
            new Leaves.RouteInfo("GET", "/bartereconomy/api/tiers", "The shared payment tier list { itemId: tier }.", "api"),
        };
        Leaves.Utils.Instance.RegisterRoutes("Barter Economy", routes);
        return Task.CompletedTask;
    }

    // The page files (bartereconomy/tiers/trades/search/pinned.html, common.txt, nav.json) moved to the
    // Leaves-WebUI mod, which serves every Leaves mod's static UI files. This listener is the read-only
    // JSON API only now. See WEBUI_EXTRACTION_PLAN.md.
    //   private readonly string uiDir = System.IO.Path.Combine(
    //       modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()), "assets", "ui");
    //   private const string HtmlType = "text/html; charset=utf-8";
    //   private sealed record FileRoute(string Path, string File, string ContentType);
    //   private static readonly List<FileRoute> FileRoutes = new()
    //   {
    //       new FileRoute("/bartereconomy/ui",           "bartereconomy.html", HtmlType),
    //       new FileRoute("/bartereconomy/ui/tiers",     "tiers.html",         HtmlType),
    //       new FileRoute("/bartereconomy/ui/trades",    "trades.html",        HtmlType),
    //       new FileRoute("/bartereconomy/ui/search",    "search.html",        HtmlType),
    //       new FileRoute("/bartereconomy/ui/pinned",    "pinned.html",        HtmlType),
    //       new FileRoute("/bartereconomy/ui/common.js", "common.txt", "application/javascript; charset=utf-8"),
    //       new FileRoute("/bartereconomy/ui/nav.json",  "nav.json",   "application/json; charset=utf-8"),
    //   };

    // Page paths now served by the Leaves-WebUI mod. Excluded here so exactly one listener claims each
    // (listener dispatch order is not guaranteed). Everything else under /bartereconomy/ is still ours.
    private static readonly HashSet<string> MovedToWebUiMod = new(StringComparer.OrdinalIgnoreCase)
    {
        "/bartereconomy/ui",
        "/bartereconomy/ui/tiers",
        "/bartereconomy/ui/trades",
        "/bartereconomy/ui/search",
        "/bartereconomy/ui/pinned",
        "/bartereconomy/ui/common.js",
        "/bartereconomy/ui/nav.json",
    };

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        if (!Leaves.HttpUtils.HasPrefix(context, Prefix))
        {
            return false;
        }
        string path = (context.Request.Path.Value ?? "").TrimEnd('/');
        return !MovedToWebUiMod.Contains(path);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        string method = context.Request.Method;

        try
        {
            // The static page/asset serves that were here (a loop over FileRoutes) moved to the
            // Leaves-WebUI mod; CanHandle already routes those paths elsewhere.

            if (Leaves.HttpUtils.IsGetSuffix(context, "/api/status"))
            {
                BarterEconomy.BarterStatusReport report = barterEconomy.GetTraderStatuses();
                await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status200OK, report);
                return;
            }
            if (Leaves.HttpUtils.IsGetSuffix(context, "/api/traders"))
            {
                List<BarterEconomy.TraderListEntry> traders = barterEconomy.GetAllTradersWithTrades();
                await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status200OK, traders);
                return;
            }
            if (Leaves.HttpUtils.IsGetSuffix(context, "/api/trades/search"))
            {
                string query = context.Request.Query["q"].ToString();
                int limit = 500;
                if (int.TryParse(context.Request.Query["limit"].ToString(), out int parsedLimit) && parsedLimit > 0)
                {
                    limit = parsedLimit;
                }
                BarterEconomy.TradeSearchReport report = barterEconomy.SearchTrades(query, limit);
                await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status200OK, report);
                return;
            }
            if (Leaves.HttpUtils.IsGetSuffix(context, "/api/trades/sells"))
            {
                // ?item=<item tpl>. "Who sells this item?" - powers the barter-chain feature.
                // A junk id just matches nothing (string compare), so no validation - empty results is a fine answer.
                string itemId = context.Request.Query["item"].ToString();
                BarterEconomy.TradeSearchReport report = barterEconomy.GetTradesSellingItem(itemId, 500);
                await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status200OK, report);
                return;
            }
            if (Leaves.HttpUtils.IsGetSuffix(context, "/api/trades"))
            {
                // ?trader=<trader id>. A junk id matches no trader (TryGet doesn't construct a MongoId), so 404.
                string traderId = context.Request.Query["trader"].ToString();
                if (!barterEconomy.TryGetTraderTrades(traderId, out BarterEconomy.TraderTradesReport report))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync($"No trader with id '{traderId}'.");
                    return;
                }
                await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status200OK, report);
                return;
            }
            if (Leaves.HttpUtils.IsGetSuffix(context, "/api/tiers"))
            {
                Dictionary<string, int> tiers = barterEconomy.GetItemTiers();
                await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status200OK, tiers);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"Unknown BarterEconomy resource: {path}");
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[BarterEconomy] Web API error on {method} {path}: {ex.Message}", LogTextColor.Red);
            await Leaves.HttpUtils.WriteJson(context, StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }
}
