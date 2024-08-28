using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Boltz.Views.Boltz.Components.SwapInfo;

public class SwapInfo(
    BoltzService boltzService
) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string swapId, string storeId)
    {
        var client = boltzService.GetClient(storeId);
        var swap = await client!.GetSwapInfo(swapId);
        return View(swap);
    }
}