using System.Collections.Generic;
using BTCPayServer.Plugins.Boltz.Payments;

namespace BTCPayServer.Plugins.Boltz.Models;

public class OnchainSettingsViewModel
{
    public BoltzPaymentConfig Config { get; set; }
    public List<ExistingWallet> ExistingWallets { get; set; }
    public bool Enabled { get; set; }
}