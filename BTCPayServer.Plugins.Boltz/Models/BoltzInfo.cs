using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Plugins.Boltz.Data;

namespace BTCPayServer.Plugins.Boltz.Models;


public class AutoSwapLnConfig
{
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; }
    
    [Display(Name = "Swap Type")]
    public string SwapType { get; set; }
    
    [Display(Name = "Wallet")]
    public string Wallet { get; set; }
    
    [Display(Name = "Inbound Balance")]
    public float InboundBalancePercent { get; set; }
    
    [Display(Name = "Outbound Balance")]
    public float OutboundBalancePercent { get; set; }
    
    
}

public class AutoSwapChainConfig
{
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; }
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

public class BoltzConfig
{
    public LightningConfig Ln { get; set; }
    public ChainConfig Chain { get; set; }
    
    public string LiquidWallet { get; set; }

    public List<Wallet> Wallets { get; set; }

    public WalletParams CreateWallet { get; set; }
}

public class BoltzInfo
{
    public GetInfoResponse Info { get; set; }
    
    public ListSwapsResponse Swaps { get; set; }
    
    public Wallets Wallets { get; set; }
    
    public GetStatusResponse Status { get; set; }
}