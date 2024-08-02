#nullable enable
using System;
using System.Collections.Generic;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Data;
using NBitcoin;

namespace BTCPayServer.Plugins.Boltz.Models;

public class ModeSetup
{
    public StoreData? RebalanceStore { get; set; }
    public bool IsAdmin { get; set; }
    public bool HasInternal { get; set; }
    public bool ConnectedInternal { get; set; }

    public bool AllowStandalone { get; set; }
    public bool AllowRebalance => IsAdmin && HasInternal && ConnectedInternal && RebalanceStore is null;
}

public enum WalletImportMethod
{
    Mnemonic,
    Xpub,
    Descriptor,
}

public enum WalletSetupFlow
{
    Standalone,
    Lightning,
    Chain,
    Manual,
}

public class ExistingWallet
{
    public String Name { get; set; }
    public bool IsBtcpay { get; set; }
    public bool IsReadonly { get; set; }
    public ulong Balance { get; set; }
    public Currency Currency { get; set; }

    public string CurrencyName => BoltzClient.CurrencyName(Currency);
    public String Value => IsBtcpay ? "" : Name;
}

public class WalletSetup
{
    public string StoreId { get; set; }

    public WalletSetupFlow Flow { get; set; }
    public Currency? Currency { get; set; }
    public WalletParams WalletParams { get; set; } = new() { Currency = Boltzrpc.Currency.Lbtc };
    public WalletCredentials WalletCredentials { get; set; } = new();
    public WalletImportMethod? ImportMethod { get; set; }
    public string? SwapType { get; set; }

    public List<ExistingWallet> ExistingWallets { get; set; }

    public bool AllowReadonly =>
        Flow == WalletSetupFlow.Chain || (Flow == WalletSetupFlow.Lightning && SwapType == "reverse");

    public bool IsImport => ImportMethod.HasValue;

    public Dictionary<string, string> RouteData => new()
    {
        { "storeId", StoreId },
        { "swapType", SwapType },
        { "flow", Flow.ToString() },
        { "currency", Currency.ToString() },
        { "importMethod", ImportMethod.ToString() },
    };

    public Dictionary<string, string> GetRouteData(string key, object value)
    {
        var data = RouteData;
        data[key] = value.ToString();
        return data;
    }
}

public enum BalanceType
{
    Absolute,
    Percentage
}

public class BalanceSetup
{
    public LightningConfig Ln { get; set; }
    public BalanceType? BalanceType { get; set; }
}

public enum SwapperType
{
    Ln,
    Chain
}

public class BudgetSetup
{
    public SwapperType SwapperType { get; set; }
    public ulong Budget { get; set; }
    public ulong BudgetIntervalDays { get; set; }
    public float MaxFeePercent { get; set; }
}

public class ChainSetup
{
    public ulong MaxBalance { get; set; }
    public PairInfo PairInfo { get; set; }
}