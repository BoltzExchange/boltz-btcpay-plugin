using System;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Hosting;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Plugins.Boltz.Payments;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NBitcoin;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzPlugin : BaseBTCPayServerPlugin
{
    public override Version Version => new(2, 1, 12);

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.0.1" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<BoltzDaemon>();
        services.AddSingleton<ILightningConnectionStringHandler>(provider =>
            provider.GetRequiredService<BoltzLightningConnectionStringHandler>());
        services.AddSingleton<BoltzLightningConnectionStringHandler>();
        services.AddSingleton<BoltzService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BoltzService>());
        services.AddSingleton(provider => new Lazy<BoltzService>(provider.GetRequiredService<BoltzService>));
        services.AddUIExtension("ln-payment-method-setup-tab", "Boltz/LNPaymentMethodSetupTab");
        services.AddUIExtension("ln-payment-method-setup-tabhead", "Boltz/LNPaymentMethodSetupTabhead");
        services.AddUIExtension("dashboard", "Boltz/BoltzInfo");
        services.AddUIExtension("store-integrations-nav", "Boltz/BoltzNav");
        services.AddUIExtension("store-wallets-nav", "Boltz/BoltzWallet");


        var pluginServices = (PluginServiceCollection)services;
        var networkProvider = pluginServices.BuildServiceProvider().GetRequiredService<BTCPayNetworkProvider>();
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");

        var blockExplorerLink = network.NBitcoinNetwork.ChainName == ChainName.Mainnet
            ? "https://liquid.network/tx/{0}"
            : "https://liquid.network/testnet/tx/{0}";
        services.AddTransactionLinkProvider(PaymentTypes.CHAIN.GetPaymentMethodId("LBTC"),
            new DefaultTransactionLinkProvider(blockExplorerLink));
        foreach (var service in pluginServices.ToList())
        {
            if (service.ServiceType == typeof(IPayoutHandler))
            {
                var t = service.ImplementationFactory(pluginServices.BuildServiceProvider());
                if (t is LightningLikePayoutHandler)
                    pluginServices.Remove(service);
            }
        }

        pluginServices.AddSingleton<IPayoutHandler>(provider => provider.GetRequiredService<BoltzPayoutHandler>());
        pluginServices.AddSingleton<BoltzPayoutHandler>();

        pluginServices.AddSingleton<WalletHelper>();
        pluginServices.AddTransient(provider => new Lazy<WalletHelper>(provider.GetRequiredService<WalletHelper>));
        pluginServices.AddSingleton<IPaymentMethodHandler>(provider =>
            provider.GetRequiredService<BoltzPaymentHandler>());
        pluginServices.AddSingleton<BoltzPaymentHandler>();

        var pmi = BoltzPaymentHandler.GetPaymentMethodId("BTC");

        services.AddSingleton(provider =>
            (IPaymentLinkExtension)ActivatorUtilities.CreateInstance(provider, typeof(BitcoinPaymentLinkExtension),
                network, pmi));

        services.AddSingleton(provider =>
            new BoltzCheckoutModelExtension(
                (BitcoinCheckoutModelExtension)ActivatorUtilities.CreateInstance(
                    provider, typeof(BitcoinCheckoutModelExtension), network, pmi
                )
            )
        );
        services.AddSingleton<IGlobalCheckoutModelExtension>(provider =>
            provider.GetRequiredService<BoltzCheckoutModelExtension>());

        services.AddSingleton<BoltzPaymentListener>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BoltzPaymentListener>());

        services.AddDefaultPrettyName(pmi, network.DisplayName);

        services.AddUIExtension("store-invoices-payments", "Boltz/ViewBoltzPaymentData");

        base.Execute(services);
    }
}