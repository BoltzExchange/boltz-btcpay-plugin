using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NBitcoin;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=1.12.0" }
    ];

    public override void Execute(IServiceCollection services)
    {

        services.AddSingleton<BoltzDaemon>();
        services.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<BoltzLightningConnectionStringHandler>());
        services.AddSingleton<BoltzLightningConnectionStringHandler>();
        services.AddSingleton<BoltzService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BoltzService>());
        services.AddSingleton<IUIExtension>(new UIExtension("Boltz/LNPaymentMethodSetupTab", "ln-payment-method-setup-tab"));
        services.AddSingleton<IUIExtension>(new UIExtension("Boltz/LNPaymentMethodSetupTabhead", "ln-payment-method-setup-tabhead"));
        services.AddSingleton<IUIExtension>(new UIExtension("Boltz/BoltzInfo", "dashboard"));
        services.AddSingleton<IUIExtension>(new UIExtension("Boltz/BoltzNav", "store-integrations-nav"));

        base.Execute(services);
    }
}
