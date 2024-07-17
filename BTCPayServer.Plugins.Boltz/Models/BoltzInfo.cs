using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autoswaprpc;
using Boltzrpc;

namespace BTCPayServer.Plugins.Boltz.Models;

public class AutoSwapLnConfig
{
    [Display(Name = "Enabled")] public bool Enabled { get; set; }

    [Display(Name = "Swap Type")] public string SwapType { get; set; }

    [Display(Name = "Wallet")] public string Wallet { get; set; }

    [Display(Name = "Inbound Balance")] public float InboundBalancePercent { get; set; }

    [Display(Name = "Outbound Balance")] public float OutboundBalancePercent { get; set; }
}

public class AutoSwapChainConfig
{
    [Display(Name = "Enabled")] public bool Enabled { get; set; }
}

public class AutoSwapData
{
    public AutoSwapLnConfig Ln { get; set; }
    public AutoSwapChainConfig Chain { get; set; }
}

public class BoltzStats
{
    public Wallet Wallet { get; set; }
    public string StoreId { get; set; }
    public string ProblemDescription { get; set; }
}

public class BoltzConnection
{
    public GetInfoResponse Info { get; set; }
    public BoltzSettings Settings { get; set; }
}

public enum Unit
{
    Sat,
    Btc,
    Percent,
    None
}

public class Stat
{
    public string Name { get; set; }
    public object Value { get; set; }
    public Unit Unit { get; set; }
}

public class StatsModel
{
    public List<Stat> Stats { get; set; }
}

public class BoltzConfig
{
    public LightningConfig Ln { get; set; }
    public ChainConfig Chain { get; set; }
    public List<ExistingWallet> ExistingWallets { get; set; }
    public BoltzSettings Settings { get; set; }

    public List<ExistingWallet> WalletsForCurrency(Currency currency)
    {
        return ExistingWallets.FindAll(w => w.Currency == currency);
    }
}

public class AutoSwapStatus
{
    public Status Status { get; set; }
    public string Name { get; set; }
    public bool Compact { get; set; }
}

public class BoltzInfo
{
    public GetInfoResponse Info { get; set; }

    public ListSwapsResponse Swaps { get; set; }

    public Wallets Wallets { get; set; }

    public GetStatusResponse Status { get; set; }

    public LightningConfig Ln { get; set; }
    public ChainConfig Chain { get; set; }
}