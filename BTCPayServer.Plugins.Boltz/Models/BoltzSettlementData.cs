#nullable enable

namespace BTCPayServer.Plugins.Boltz.Models;

public class BoltzSettlementData
{
    public string? SwapId { get; set; }
    public string? SettlementCurrency { get; set; }
    public string? SettlementAddress { get; set; }
    public string? SettlementTransactionId { get; set; }
}
