using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;

namespace BTCPayServer.Plugins.Boltz;

public class WalletHelper(
    BTCPayWalletProvider btcPayWalletProvider,
    BTCPayNetworkProvider btcPayNetworkProvider,
    PaymentMethodHandlerDictionary paymentHandlers
) {
    public BTCPayNetwork BtcNetwork => btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
    public BTCPayWallet BtcWallet => btcPayWalletProvider.GetWallet(BtcNetwork);

    public async Task<string> GenerateNewAddress(StoreData store, string generatedBy = "Boltz")
    {
        var derivation = store.GetDerivationSchemeSettings(paymentHandlers, "BTC");
        if (derivation is null)
        {
            throw new InvalidOperationException("Store has no btc wallet configured");
        }

        var address = await BtcWallet.ReserveAddressAsync(store.Id, derivation.AccountDerivation, generatedBy);
        return address.Address.ToString();
        return "";
    }
}