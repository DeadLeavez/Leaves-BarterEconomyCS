using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Services;

namespace Leaves_BarterEconomy;

// Snapshots the item table at Database+1 (pre-mod) so modded items stay out of the barter pool.
[Injectable(TypePriority = OnLoadOrder.Database + 1)]
public class VanillaItemSnapshot(BarterEconomy barterEconomy, DatabaseService databaseService) : IOnLoad
{
    public Task OnLoad()
    {
        barterEconomy.CaptureVanillaItemIds(databaseService.GetItems().Keys);
        return Task.CompletedTask;
    }
}
