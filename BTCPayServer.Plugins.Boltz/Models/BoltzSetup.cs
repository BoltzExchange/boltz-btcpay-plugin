using System.Collections.Generic;
using Autoswaprpc;
using Boltzrpc;

namespace BTCPayServer.Plugins.Boltz.Models;

public class BoltzSetup
{
    public LightningConfig Ln { get; set; }
    public ChainConfig Chain { get; set; }

    public string LiquidWallet { get; set; }

    public List<Wallet> Wallets { get; set; }

    public WalletParams CreateWallet { get; set; }
}