#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Altcoins;
using NBitcoin.Altcoins.Elements;
using Newtonsoft.Json.Linq;
using MarkPayoutRequest = BTCPayServer.HostedServices.MarkPayoutRequest;
using PullPaymentHostedService = BTCPayServer.HostedServices.PullPaymentHostedService;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzService(
    BTCPayWalletProvider btcPayWalletProvider,
    StoreRepository storeRepository,
    EventAggregator eventAggregator,
    ILogger<BoltzService> logger,
    BTCPayNetworkProvider btcPayNetworkProvider,
    IOptions<LightningNetworkOptions> lightningNetworkOptions,
    IOptions<ExternalServicesOptions> externalServiceOptions,
    BoltzDaemon daemon,
    TransactionLinkProviders transactionLinkProviders,
    PullPaymentHostedService pullPaymentHostedService,
    BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
    LightningLikePayoutHandler lightningLikePayoutHandler,
    PluginHookService pluginHookService
)
    : EventHostedServiceBase(eventAggregator, logger)
{
    private readonly Uri _defaultUri = new("http://127.0.0.1:9002");
    private Dictionary<string, BoltzSettings>? _settings;
    private GetPairsResponse? _pairs;

    public BoltzDaemon Daemon => daemon;
    public BoltzClient? AdminClient => daemon.AdminClient;

    public BTCPayNetwork BtcNetwork => btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
    public BTCPayWallet BtcWallet => btcPayWalletProvider.GetWallet(BtcNetwork);

    public ILightningClient? InternalLightning =>
        lightningNetworkOptions.Value.InternalLightningByCryptoCode.GetValueOrDefault("BTC", null);

    public bool AllowTenants => _settings?.Values.Any(setting => setting.AllowTenants) ?? false;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        externalServiceOptions.Value.OtherExternalServices.Add("Boltz", new Uri("https://boltz.exchange"));
        _settings = (await storeRepository.GetSettingsAsync<BoltzSettings>("Boltz"))
            .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!);

        daemon.SwapUpdate += async (_, response) =>
        {
            try
            {
                await OnSwap(response);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Could not handle swap update");
            }
        };
        await daemon.Init();
        await daemon.TryConfigure(InternalLightning);

        if (daemon.Running)
        {
            foreach (var storeId in _settings.Keys)
            {
                try
                {
                    await CheckStore(storeId);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Could not initialize store");
                }
            }
        }

        pluginHookService.ActionInvoked += async (_, args) =>
        {
            if (args.hook == "before-automated-payout-processing")
            {
                await BeforePayoutAction((BeforePayoutActionData)args.args);
            }
        };

        await base.StartAsync(cancellationToken);
    }

    private async Task CheckStore(string storeId)
    {
        var settings = GetSettings(storeId)!;
        try
        {
            await CheckSettings(settings);
        }
        catch (RpcException e) when (settings.GrpcUrl == _defaultUri)
        {
            logger.LogInformation(e, "Trying to generate new macaroon for store {storeId}", storeId);
            await SetMacaroon(storeId, settings);
            await Set(storeId, settings);
        }
    }

    private async Task OnSwap(GetSwapInfoResponse swap)
    {
        if (swap is { ChainSwap: not null, ReverseSwap: not null })
        {
            var status = swap.ReverseSwap?.Status ?? swap.ChainSwap!.Status;
            var isAuto = swap.ReverseSwap?.IsAuto ?? swap.ChainSwap!.IsAuto;
            if (status != "swap.created" || !isAuto) return;
        }

        if (swap.Swap is not null && swap.Swap.State != SwapState.Successful)
        {
            return;
        }

        var tenantId = swap.ReverseSwap?.TenantId ?? swap.ChainSwap?.TenantId ?? swap.Swap!.TenantId;
        var found = _settings?.ToList()
            .Find(pair => pair.Value.ActualTenantId == tenantId);
        if (found?.Value is null) return;

        var store = await storeRepository.FindStore(found.Value.Key);
        if (store is null)
        {
            logger.LogError("Could not find store {storeId}", found.Value.Key);
            return;
        }

        if (swap.Swap is not null)
        {
            var payouts = await pullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery
            {
                States = [PayoutState.InProgress],
                Stores = [store.Id]
            });
            foreach (var payout in payouts)
            {
                if (BOLT11PaymentRequest.TryParse(swap.Swap.Invoice, out var invoice, BtcNetwork.NBitcoinNetwork))
                {
                    var proof = lightningLikePayoutHandler.ParseProof(payout) as PayoutLightningBlob;
                    if (proof?.PaymentHash != null && proof.PaymentHash == invoice?.PaymentHash?.ToString())
                    {
                        proof.Preimage = swap.Swap.Preimage;
                        payout.SetProofBlob(proof, null);
                        await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
                        {
                            PayoutId = payout.Id, State = PayoutState.Completed,
                            Proof = payout.GetProofBlobJson(),
                        });
                    }
                }
            }
        }

        var client = daemon.GetClient(found.Value.Value)!;
        var (ln, chain) = await client.GetAutoSwapConfig();

        var chainAddress = chain?.ToAddress;
        if (!string.IsNullOrEmpty(chainAddress) && chainAddress == swap.ChainSwap?.ToData.Address)
        {
            var address = await GenerateNewAddress(store);
            await client.UpdateAutoSwapChainConfig(
                new ChainConfig { ToAddress = address, },
                ["to_address"]
            );
        }

        var lnAddress = ln?.StaticAddress;
        if (!string.IsNullOrEmpty(lnAddress) && lnAddress == swap.ReverseSwap?.ClaimAddress)
        {
            var address = await GenerateNewAddress(store);
            await client.UpdateAutoSwapLightningConfig(
                new LightningConfig { StaticAddress = address, },
                ["static_address"]
            );
        }
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


    private async Task CheckSettings(BoltzSettings settings)
    {
        var client = daemon.GetClient(settings)!;
        await client.GetInfo();
    }

    public async Task SetMacaroon(string storeId, BoltzSettings settings)
    {
        if (settings.Mode == BoltzMode.Standalone)
        {
            var tenantName = "btcpay-" + storeId;
            Tenant tenant;
            try
            {
                tenant = await AdminClient!.GetTenant(tenantName);
            }
            catch (RpcException)
            {
                tenant = await AdminClient!.CreateTenant(tenantName);
            }

            var response = await AdminClient.BakeMacaroon(tenant.Id);
            settings.TenantId = tenant.Id;
            settings.Macaroon = response.Macaroon;
        }
        else
        {
            settings.Macaroon = daemon.AdminMacaroon!;
        }
    }

    public async Task<BoltzSettings> InitializeStore(string storeId, BoltzMode mode)
    {
        var settings = new BoltzSettings { GrpcUrl = _defaultUri, Mode = mode };
        await SetMacaroon(storeId, settings);
        return settings;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await daemon.Stop();
        await base.StopAsync(cancellationToken);
    }

    public BoltzClient? GetClient(string? storeId)
    {
        return daemon.GetClient(GetSettings(storeId));
    }

    public bool StoreConfigured(string? storeId)
    {
        return GetSettings(storeId)?.Mode is not null;
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
            return new BoltzLightningClient(settings.GrpcUrl!, settings.Macaroon!, settings.StandaloneWallet.Id,
                BtcNetwork.NBitcoinNetwork, daemon);
        }

        return null;
    }

    public async Task Set(string storeId, BoltzSettings? settings)
    {
        var cryptoCode = "BTC";
        var paymentMethodId = new PaymentMethodId(cryptoCode, PaymentTypes.LightningLike);

        var data = await storeRepository.FindStore(storeId);
        if (settings is null)
        {
            _settings!.Remove(storeId, out var oldSettings);
            if (oldSettings is not null && oldSettings.Mode == BoltzMode.Rebalance)
            {
                await AdminClient!.ResetLnConfig();
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
        else
        {
            await CheckSettings(settings);

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

        await storeRepository.UpdateSetting(storeId, "Boltz", settings!);
    }

    public async Task<StoreData?> GetRebalanceStore()
    {
        var store = _settings?.ToList()
            .Find(pair => pair.Value.Mode == BoltzMode.Rebalance);
        return store is null ? null : await storeRepository.FindStore(store.Value.Key);
    }


    public async Task<PairInfo?> GetPairInfo(Pair pair, SwapType swapType)
    {
        if (AdminClient is null) return null;
        _pairs = await AdminClient.GetPairs();
        var search = swapType switch
        {
            SwapType.Reverse => _pairs.Reverse,
            SwapType.Submarine => _pairs.Submarine,
            SwapType.Chain => _pairs.Chain,
            _ => throw new ArgumentOutOfRangeException(nameof(swapType), swapType, null)
        };
        return search.ToList().Find(p => p.Pair.From == pair.From && p.Pair.To == pair.To);
    }

    public PaymentUrlBuilder GenerateBIP21(Currency currency, string cryptoInfoAddress, decimal? cryptoInfoDue = null,
        string? label = null)
    {
        var isLbtc = currency == Currency.Lbtc;
        var prefix = isLbtc
            ? BtcNetwork.NBitcoinNetwork.ChainName == ChainName.Mainnet ? "liquidnetwork" : "liquidtestnet"
            : "bitcoin";
        var builder = new PaymentUrlBuilder(prefix);
        builder.Host = cryptoInfoAddress;
        if (cryptoInfoDue is not null && cryptoInfoDue.Value != 0.0m)
        {
            builder.QueryParams.Add("amount", cryptoInfoDue.Value.ToString(CultureInfo.InvariantCulture));
        }

        builder.QueryParams.Add("label", label ?? "Send to BTCPayserver");

        if (!isLbtc)
        {
            builder.QueryParams.Add("assetid", ElementsParams<Liquid.LiquidRegtest>.PeggedAssetId.ToString());
        }


        return builder;
    }

    public string? GetTransactionLink(Currency currency, string txId)
    {
        return transactionLinkProviders.GetTransactionLink(
            new PaymentMethodId(currency.ToString().ToUpper(), PaymentTypes.BTCLike), txId);
    }

    public static List<Stat> PairStats(PairInfo pairInfo) =>
    [
        new() { Name = "Boltz Service Fee", Value = pairInfo.Fees.Percentage, Unit = Unit.Percent },
        new() { Name = "Network Fee", Value = pairInfo.Fees.MinerFees, Unit = Unit.Sat },
        new() { Name = "Min Amount", Value = pairInfo.Limits.Minimal, Unit = Unit.Sat },
        new() { Name = "Max Amount", Value = pairInfo.Limits.Maximal, Unit = Unit.Sat }
    ];

    private async Task BeforePayoutAction(BeforePayoutActionData data)
    {
        foreach (var payout in data.Payouts)
        {
            // cancel 0 amount invoice
            var bolt11 = payout.GetBlob(jsonSerializerSettings).Destination;
            var invoice = BOLT11PaymentRequest.Parse(bolt11, BtcNetwork.NBitcoinNetwork);
            if (invoice.MinimumAmount == 0)
            {
                await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
                {
                    PayoutId = payout.Id, State = PayoutState.Cancelled,
                });
            }
        }
    }

    public string Hook => "before-automated-payout-processing";
}