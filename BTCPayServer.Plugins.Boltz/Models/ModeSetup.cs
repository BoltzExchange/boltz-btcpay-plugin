#nullable enable
using System;
using System.Collections.Generic;
using Autoswaprpc;
using Boltzrpc;
namespace BTCPayServer.Plugins.Boltz.Models;

public class ModeSetup
{
    public bool Enabled { get; set; }
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
    Chain,
    Manual,
}

public class ExistingWallet
{
    public String Name { get; set; } = "";
    public bool IsBtcpay { get; set; }
    public bool IsReadonly { get; set; }
    public ulong? Balance { get; set; }
    public Currency Currency { get; set; }

    public string CurrencyName => BoltzClient.CurrencyName(Currency);
    public String Value => IsBtcpay ? "" : Name;
}

public class WalletSetup
{
    public string? StoreId { get; set; }

    public WalletSetupFlow Flow { get; set; }
    public Currency? Currency { get; set; }
    public string? WalletName { get; set; }
    public WalletCredentials WalletCredentials { get; set; } = new();
    public WalletImportMethod? ImportMethod { get; set; }
    public List<ExistingWallet> ExistingWallets { get; set; } = new();

    public bool AllowImportHot { get; set; }
    public bool AllowCreateHot { get; set; }

    public bool IsImport => ImportMethod.HasValue;

    public List<Subaccount>? Subaccounts { get; set; }
    public bool InitialRender { get; set; }
    public ulong? Subaccount { get; set; }

    public Dictionary<string, string?> RouteData => new()
    {
        { "storeId", StoreId },
        { "flow", Flow.ToString() },
        { "currency", Currency.ToString() },
        { "importMethod", ImportMethod.ToString() },
        { "walletName", WalletName },
    };

    public Dictionary<string, string?> GetRouteData(string key, object value)
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
    public LightningConfig? Ln { get; set; }
    public BalanceType? BalanceType { get; set; }
}

public enum SwapperType
{
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
    public ulong ReserveBalance { get; set; }
    public PairInfo? PairInfo { get; set; }
}