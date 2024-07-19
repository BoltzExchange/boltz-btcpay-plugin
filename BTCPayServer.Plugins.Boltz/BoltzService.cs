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
    IOptions<LightningNetworkOptions> lightningNetworkOptions)
    : EventHostedServiceBase(eventAggregator, logger)
{
    private readonly Uri _defaultUri = new("http://127.0.0.1:9002");
    private Dictionary<string, BoltzSettings>? _settings;
    private readonly Dictionary<string, BoltzClient> _clients = new();
    private readonly ILogger _logger = logger;
    private BoltzClient AdminClient => Daemon.AdminClient;

    public BoltzDaemon Daemon;

    public BoltzSettings? RebalanceStore => _settings?.Values.ToList()
        .Find(settings => settings.Mode == BoltzMode.Rebalance);

    public BTCPayNetwork BtcNetwork => btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
    public BTCPayWallet BtcWallet => btcPayWalletProvider.GetWallet(BtcNetwork);

    public ILightningClient? InternalLightning =>
        lightningNetworkOptions.Value.InternalLightningByCryptoCode.GetValueOrDefault("BTC", null);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
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
            if (storeId is not null && _clients.Remove(storeId, out var client))
            {
                client.Dispose();
            }
        }
        else
        {
            var client = new BoltzClient(settings.GrpcUrl, settings.Macaroon);
            await client.GetInfo();
            _clients!.AddOrReplace(storeId, client);
            return client;
        }

        return null;
    }

    public async Task InitializeStore(string storeId, BoltzMode mode)
    {
        if (_clients.TryGetValue(storeId, out var existing))
        {
            try
            {
                await existing.ResetLnConfig();
                await existing.ResetChainConfig();
            }
            catch (RpcException e)
            {
                if (!e.Message.Contains("autoswap not configured"))
                {
                    throw;
                }
            }
        }

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

        await Set(storeId, settings);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await AdminClient.Stop();
        _clients.Values.ToList().ForEach(c => c.Dispose());
        await base.StopAsync(cancellationToken);
    }

    private string StorageDir => Path.Combine(dataDirectories.Value.StorageDir, "Boltz");

    public BoltzClient? GetClient(string? storeId)
    {
        if (storeId is null)
        {
            return null;
        }

        _clients.TryGetValue(storeId, out var client);
        return client;
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
        if (settings is null)
        {
            _settings!.Remove(storeId, out var oldSettings);
            var data = await storeRepository.FindStore(storeId);
            var paymentMethodId = new PaymentMethodId("BTC", LightningPaymentType.Instance);
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
            _settings.AddOrReplace(storeId, settings);
        }
    }
}