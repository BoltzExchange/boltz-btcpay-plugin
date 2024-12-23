#nullable enable
using System.Collections.Generic;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Models;
using BTCPayServer.Models.ServerViewModels;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.Boltz.Models;

public class AdminModel
{
    public GetInfoResponse? Info { get; set; }
    public BoltzSettings? Settings { get; set; }
    public BoltzServerSettings? ServerSettings { get; set; }
    public LogsViewModel Log { get; set; } = new();
}

public enum Unit
{
    None,
    Sat,
    Btc,
    Percent,
}

public class Stat
{
    public string? Name { get; set; }
    public object? Value { get; set; }
    public Unit Unit { get; set; }
}

public class BoltzConfig
{
    public LightningConfig? Ln { get; set; }
    public ChainConfig? Chain { get; set; }
    public List<ExistingWallet> ExistingWallets { get; set; } = new();
    public BoltzSettings? Settings { get; set; }

    public SelectList WalletSelectList(Currency? currency)
    {
        return new SelectList(
            currency.HasValue ? ExistingWallets.FindAll(w => w.Currency == currency) : ExistingWallets,
            nameof(ExistingWallet.Value), nameof(ExistingWallet.Name));
    }
}

public class AutoSwapStatus
{
    public Status Status { get; init; } = new();
    public SwapperType SwapperType { get; init; }
    public bool Compact { get; set; }
    public string Wallet = "";
    public string ToWallet { get; set; } = "";
}

public class BoltzInfo
{
    public GetInfoResponse? Info { get; set; }
    public GetRecommendationsResponse? Recommendations { get; set; }
    public List<AnySwapInfo>? PendingAutoSwaps { get; set; }
    public SwapStats? Stats { get; set; }

    public ListSwapsResponse? Swaps { get; set; }

    public Wallet? StandaloneWallet { get; set; }
    public Wallet? RebalanceWallet { get; set; }

    public GetStatusResponse? Status { get; set; }

    public LightningConfig? Ln { get; set; }
    public ChainConfig? Chain { get; set; }

    public GetSwapInfoResponse? SwapInfo { get; set; }
}

public class SwapsModel : BasePagingViewModel
{
    public ListSwapsResponse? Swaps { get; set; }
    public GetSwapInfoResponse? SwapInfo { get; set; }
    public override int CurrentPageCount => Swaps?.AllSwaps.Count ?? 0;
}

public class FeesModel
{
    public Pair? Pair { get; set; }
    public SwapType? SwapType { get; set; }

    public static FeesModel Standalone = new()
    {
        Pair = new Pair { From = Currency.Btc, To = Currency.Lbtc },
        SwapType = Boltzrpc.SwapType.Reverse
    };
}