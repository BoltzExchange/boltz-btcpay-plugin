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
using BTCPayServer.Plugins.Boltz.Payments;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NBitcoin;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzPlugin : BaseBTCPayServerPlugin
{
    public override Version Version => new(2, 0, 0, 0);

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=1.12.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<BoltzDaemon>();
        services.AddSingleton<ILightningConnectionStringHandler>(provider =>
            provider.GetRequiredService<BoltzLightningConnectionStringHandler>());
        services.AddSingleton<BoltzLightningConnectionStringHandler>();
        services.AddSingleton<BoltzService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BoltzService>());
        services.AddSingleton<IUIExtension>(new UIExtension("Boltz/LNPaymentMethodSetupTab",
            "ln-payment-method-setup-tab"));
        services.AddSingleton<IUIExtension>(new UIExtension("Boltz/LNPaymentMethodSetupTabhead",
            "ln-payment-method-setup-tabhead"));
        services.AddSingleton<IUIExtension>(new UIExtension("Boltz/BoltzInfo", "dashboard"));
        services.AddSingleton<IUIExtension>(new UIExtension("Boltz/BoltzNav", "store-integrations-nav"));

        var pluginServices = (PluginServiceCollection)services;
        var networkProvider = pluginServices.BuildServiceProvider().GetRequiredService<BTCPayNetworkProvider>();
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");

        var blockExplorerLink = network.NBitcoinNetwork.ChainName == ChainName.Mainnet
            ? "https://liquid.network/tx/{0}"
            : "https://liquid.network/testnet/tx/{0}";
        services.AddTransactionLinkProvider("LBTC", new DefaultTransactionLinkProvider(blockExplorerLink));
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

        pluginServices.AddSingleton<IPaymentMethodHandler>(provider =>
            provider.GetRequiredService<BoltzPaymentHandler>());
        pluginServices.AddSingleton<BoltzPaymentHandler>();

        var pmi = BoltzPaymentHandler.GetPaymentMethodId("BTC");

        services.AddSingleton(provider =>
            (IPaymentLinkExtension)ActivatorUtilities.CreateInstance(provider, typeof(BitcoinPaymentLinkExtension),
                network, pmi));

        services.AddSingleton(provider =>
            (IPaymentMethodViewExtension)ActivatorUtilities.CreateInstance(provider,
                typeof(BitcoinPaymentMethodViewExtension), pmi));

        services.AddSingleton(provider =>
            (IPaymentModelExtension)ActivatorUtilities.CreateInstance(provider,
                typeof(BoltzPaymentModelExtension), network, pmi));

        services.AddSingleton<BoltzPaymentListener>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BoltzPaymentListener>());

        base.Execute(services);
    }
}