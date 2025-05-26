using System.Collections.Generic;
using Boltzrpc;

namespace BTCPayServer.Plugins.Boltz.Models;

public class WalletsModel
{
    public List<Wallet> Wallets { get; set; } = new();
}
