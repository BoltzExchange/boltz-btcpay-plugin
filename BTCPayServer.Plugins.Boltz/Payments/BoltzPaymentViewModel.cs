using BTCPayServer.Models.InvoicingModels;

namespace BTCPayServer.Plugins.Boltz.Payments;

public class BoltzPaymentViewModel : OnchainPaymentViewModel
{
    public string SwapId { get; set; }
}