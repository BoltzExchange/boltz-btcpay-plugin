#nullable enable
using Boltzrpc;

namespace BTCPayServer.Plugins.Boltz.Models;

public enum SendType
{
    Native = 0,
    Lightning = 1,
    Chain = 2,
}

public class WalletSendModel
{
    public Wallet Wallet { get; set; }

    public SendType SendType { get; set; }

    public string? Destination { get; set; }
    public ulong? Amount { get; set; }
    public double? FeeRate { get; set; }

    public string? TransactionId { get; set; }
    public GetSwapInfoResponse? SwapInfo { get; set; }
}
public class WalletReceiveModel
{
    public Wallet Wallet { get; set; }

    public SendType SendType { get; set; }

    public ulong? AmountLn { get; set; }
    public ulong? AmountChain { get; set; }

    public string? Address { get; set; }

    public GetSwapInfoResponse? SwapInfo { get; set; }
}
