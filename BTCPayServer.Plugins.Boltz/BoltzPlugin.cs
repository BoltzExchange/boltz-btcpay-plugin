using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Hosting;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzPlugin : BaseBTCPayServerPlugin
{
    public override Version Version => new (1, 2, 0);

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
        var networkProvider = pluginServices.BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>();
        var network = networkProvider.GetFromCryptoCode("BTC");

        var blockExplorerLink = network.NBitcoinNetwork.ChainName == ChainName.Mainnet
            ? "https://liquid.network/tx/{0}"
            : "https://liquid.network/testnet/tx/{0}";
        services.AddTransactionLinkProvider(new PaymentMethodId("LBTC", PaymentTypes.BTCLike),
            new DefaultTransactionLinkProvider(blockExplorerLink));

        base.Execute(services);
    }
}
