using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Boltzrpc;
using BTCPayServer.Plugins.Boltz.Data;

namespace BTCPayServer.Plugins.Boltz.Models;


public class AutoSwapData
{
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; }
    
    [Display(Name = "Max Balance")]
    public float MaxBalancePercent { get; set; }
}

public class BoltzStats
{
    public Wallet Wallet { get; set; }
    public string StoreId { get; set; }
    public string ProblemDescription { get; set; }
}

public class BoltzData
{
    public List<PluginData> Data { get; set; }
    public BoltzSettings Settings { get; set; }
    
    public GetInfoResponse Info { get; set; }
    
    public ListSwapsResponse Swaps { get; set; }
    
    public Wallets Wallets { get; set; }
    
    public AutoSwapData AutoSwapData { get; set; }
}