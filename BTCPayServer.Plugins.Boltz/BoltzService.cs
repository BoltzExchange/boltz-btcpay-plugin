#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzService(
    BTCPayWalletProvider btcPayWalletProvider,
    StoreRepository storeRepository,
    IOptions<DataDirectories> dataDirectories,
    EventAggregator eventAggregator,
    ILogger<BoltzService> logger,
    BTCPayNetworkProvider btcPayNetworkProvider,
    IOptions<LightningNetworkOptions> lightningNetworkOptions,
    IOptions<ExternalServicesOptions> externalServiceOptions
)
    : EventHostedServiceBase(eventAggregator, logger)
{
    private readonly Uri _defaultUri = new("http://127.0.0.1:9002");
    private Dictionary<string, BoltzSettings>? _settings;
    private readonly Dictionary<string, BoltzClient> _clients = new();
    private readonly ILogger _logger = logger;

    public BoltzDaemon Daemon;
    public BoltzClient AdminClient => Daemon.AdminClient!;

    public async Task<StoreData?> GetRebalanceStore()
    {
        var store = _settings?.ToList()
            .Find(pair => pair.Value.Mode == BoltzMode.Rebalance);
        return store is null ? null : await storeRepository.FindStore(store.Value.Key);
    }

    public BTCPayNetwork BtcNetwork => btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
    public BTCPayWallet BtcWallet => btcPayWalletProvider.GetWallet(BtcNetwork);

    public ILightningClient? InternalLightning =>
        lightningNetworkOptions.Value.InternalLightningByCryptoCode.GetValueOrDefault("BTC", null);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        externalServiceOptions.Value.OtherExternalServices.Add("Boltz", new Uri("https://boltz.exchange"));
        _settings = (await storeRepository.GetSettingsAsync<BoltzSettings>("Boltz"))
            .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!);

        if (!Directory.Exists(StorageDir))
        {
            Directory.CreateDirectory(StorageDir);
        }

        Daemon = new BoltzDaemon(StorageDir, BtcNetwork, logger);

        await Daemon.Init();
        await Daemon.TryConfigure(InternalLightning);

        foreach (var keyValuePair in _settings)
        {
            try
            {
                var settings = keyValuePair.Value;
                await Handle(keyValuePair.Key, settings);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not initialize store");
            }
        }

        await base.StartAsync(cancellationToken);
    }

    public async Task<string> GenerateNewAddress(StoreData store)
    {
        var derivation = store.GetDerivationSchemeSettings(btcPayNetworkProvider, "BTC");
        if (derivation is null)
        {
            throw new InvalidOperationException("Store has no btc wallet configured");
        }

        var address = await BtcWallet.ReserveAddressAsync(store.Id, derivation.AccountDerivation, "Boltz");
        return address.Address.ToString();
    }


    private async Task<BoltzClient?> Handle(string? storeId, BoltzSettings? settings)
    {
        if (settings is null)
        {
            /*
            if (storeId is not null && _clients.Remove(storeId, out var client))
            {
                client.Dispose();
            }
            */
        }
        else
        {
            var client = new BoltzClient(settings.GrpcUrl!, settings.Macaroon);
            await client.GetInfo();
            return client;
        }

        return null;
    }

    public async Task<BoltzSettings> InitializeStore(string storeId, BoltzMode mode)
    {
        var settings = new BoltzSettings { GrpcUrl = _defaultUri, Mode = mode };
        if (mode == BoltzMode.Standalone)
        {
            var tenantName = "btcpay-" + storeId;
            Tenant tenant;
            try
            {
                tenant = await AdminClient.GetTenant(tenantName);
            }
            catch (RpcException)
            {
                tenant = await AdminClient.CreateTenant(tenantName);
            }

            var response = await AdminClient.BakeMacaroon(tenant.Id);
            settings.TenantId = tenant.Id;
            settings.Macaroon = response.Macaroon;
        }
        else
        {
            settings.Macaroon = Daemon.AdminMacaroon!;
        }

        return settings;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Boltz");
        await Daemon.Stop();
        await base.StopAsync(cancellationToken);
    }

    private string StorageDir => Path.Combine(dataDirectories.Value.StorageDir, "Boltz");

    public BoltzClient? GetClient(string? storeId)
    {
        return GetSettings(storeId)?.Client;
    }

    public BoltzSettings? GetSettings(string? storeId)
    {
        if (storeId is null)
        {
            return null;
        }

        _settings!.TryGetValue(storeId, out var settings);
        return settings;
    }

    public BoltzLightningClient? GetLightningClient(BoltzSettings? settings)
    {
        if (settings?.StandaloneWallet is not null)
        {
            return new BoltzLightningClient(settings.GrpcUrl, settings.Macaroon, settings.StandaloneWallet.Id,
                BtcNetwork.NBitcoinNetwork);
        }

        return null;
    }

    public async Task Set(string storeId, BoltzSettings? settings)
    {
        var result = await Handle(storeId, settings);
        await storeRepository.UpdateSetting(storeId, "Boltz", settings!);

        var cryptoCode = "BTC";
        var paymentMethodId = new PaymentMethodId(cryptoCode, PaymentTypes.LightningLike);

        var data = await storeRepository.FindStore(storeId);
        if (settings is null)
        {
            _settings!.Remove(storeId, out var oldSettings);
            if (oldSettings is not null && oldSettings.Mode == BoltzMode.Rebalance)
            {
                await AdminClient.ResetLnConfig();
            }

            var boltzUrl = GetLightningClient(oldSettings)?.ToString();
            var paymentMethod = data?.GetSupportedPaymentMethods(btcPayNetworkProvider)
                .OfType<LightningSupportedPaymentMethod>()
                .FirstOrDefault(method =>
                    method.PaymentId == paymentMethodId && method.GetExternalLightningUrl() == boltzUrl);
            if (paymentMethod is not null)
            {
                data!.SetSupportedPaymentMethod(paymentMethodId, null);
                await storeRepository.UpdateStore(data!);
            }
        }
        else if (result is not null)
        {
            if (settings.Mode == BoltzMode.Standalone)
            {
                var paymentMethod = new LightningSupportedPaymentMethod
                {
                    CryptoCode = paymentMethodId.CryptoCode
                };

                var lightningClient = GetLightningClient(settings)!;
                paymentMethod.SetLightningUrl(lightningClient);

                data!.SetSupportedPaymentMethod(paymentMethodId, paymentMethod);
            }

            await storeRepository.UpdateStore(data!);
            _settings.AddOrReplace(storeId, settings);
        }
    }
}