using System;
using System.Collections.Generic;
using Boltzrpc;
using BTCPayServer.Models;

namespace BTCPayServer.Plugins.Boltz.Models;

public class WalletViewModel : BasePagingViewModel
{
    public Wallet Wallet { get; set; }
    public List<WalletTransaction> Transactions { get; set; } = new();
    public override int CurrentPageCount => Transactions.Count;
}
