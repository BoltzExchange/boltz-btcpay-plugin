using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Payments;

public class BoltzPaymentConfig
{
    public Uri GrpcUrl { get; set; }
    public string Macaroon { get; set; }
}

public class BoltzPaymentHandler(
    BTCPayNetworkProvider networkProvider,
    IFeeProviderFactory feeRateProviderFactory,
    //BoltzService boltzService
    BoltzDaemon daemon,
    ILogger<BoltzClient> clientLogger
)
    : IPaymentMethodHandler, IHasNetwork
{
    public static readonly PaymentType PaymentType = new("CHAIN-BOLTZ");

    public static PaymentMethodId GetPaymentMethodId(string cryptoCode) =>
        PaymentType.GetPaymentMethodId(cryptoCode);

    public JsonSerializer Serializer => BlobSerializer.CreateSerializer(Network.NBXplorerNetwork).Serializer;
    public PaymentMethodId PaymentMethodId => GetPaymentMethodId(Network.CryptoCode);
    public BTCPayNetwork Network => networkProvider.GetNetwork<BTCPayNetwork>("BTC");

    class Prepare
    {
        public Task<FeeRate> GetRecommendedFeeRate;
        public Task<PairInfo> GetPairInfo;
        public Task<CreateSwapResponse> CreateSwap;
    }

    public BoltzClient GetClient(BoltzPaymentConfig config)
    {
        return new BoltzClient(clientLogger, config.GrpcUrl, config.Macaroon);
    }

    public class PromptDetails : BitcoinPaymentPromptDetails
    {
        public string SwapId { get; set; }
    }

    public class PaymentDetails
    {
        public string SwapId { get; set; }
        public string TransactionId { get; set; }
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details);
    }

    public PromptDetails ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<PromptDetails>(Serializer);
    }

    public BoltzPaymentConfig ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<BoltzPaymentConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(BoltzPaymentConfig)}");
    }

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config)
    {
        return ParsePaymentMethodConfig(config);
    }

    public async Task AfterSavingInvoice(PaymentMethodContext paymentMethodContext)
    {
    }

    public Task BeforeFetchingRates(PaymentMethodContext paymentMethodContext)
    {
        paymentMethodContext.Prompt.Currency = Network.CryptoCode;
        paymentMethodContext.Prompt.Divisibility = Network.Divisibility;
        if (paymentMethodContext.Prompt.Activated)
        {
            var storeBlob = paymentMethodContext.StoreBlob;
            var cfg = ParsePaymentMethodConfig(paymentMethodContext.PaymentMethodConfig);
            var client = GetClient(cfg);
            paymentMethodContext.State = new Prepare()
            {
                GetRecommendedFeeRate =
                    feeRateProviderFactory.CreateFeeProvider(Network)
                        .GetFeeRateAsync(storeBlob.RecommendedFeeBlockTarget),
                GetPairInfo = client.GetPairInfo(new Pair { From = Currency.Btc, To = Currency.Btc },
                    SwapType.Submarine),
                CreateSwap = client!.CreateSwap(new CreateSwapRequest
                {
                })
            };
        }

        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext paymentContext)
    {
        var prepare = (Prepare)paymentContext.State;
        var paymentMethod = paymentContext.Prompt;
        var onchainMethod = new PromptDetails();
        var blob = paymentContext.StoreBlob;

        onchainMethod.FeeMode = blob.NetworkFeeMode;
        onchainMethod.RecommendedFeeRate = await prepare.GetRecommendedFeeRate;
        var amount = paymentMethod.Calculate().Due;
        var pairInfo = await prepare.GetPairInfo;
        switch (onchainMethod.FeeMode)
        {
            case NetworkFeeMode.Always:
            case NetworkFeeMode.MultiplePaymentsOnly:
                if (onchainMethod.FeeMode == NetworkFeeMode.Always || paymentMethod.Calculate().TxCount > 0)
                {
                    var rate = (decimal)pairInfo.Fees.Percentage / 100;
                    var service = LightMoney.FromUnit(amount * rate, LightMoneyUnit.BTC);
                    var minerFee = LightMoney.Satoshis(pairInfo.Fees.MinerFees);
                    paymentMethod.PaymentMethodFee = (service + minerFee).ToDecimal(LightMoneyUnit.BTC);
                }

                break;
            case NetworkFeeMode.Never:
                onchainMethod.PaymentMethodFeeRate = FeeRate.Zero;
                break;
        }

        if (paymentContext.InvoiceEntity.Type != InvoiceType.TopUp &&
            amount < LightMoney.Satoshis(pairInfo.Limits.Minimal).ToUnit(LightMoneyUnit.BTC))
        {
            throw new PaymentMethodUnavailableException(
                "Amount below the boltz limit. For amounts of this size, it is recommended to enable an off-chain (Lightning) payment method"
            );
        }

        var swap = await prepare.CreateSwap;
        paymentMethod.Destination = swap.Address;
        onchainMethod.SwapId = swap.Id;
        paymentContext.TrackedDestinations.Add(swap.Address);
        paymentMethod.Details = JObject.FromObject(onchainMethod, Serializer);
    }

    public async Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        var res = validationContext.Config.ToObject<BoltzPaymentConfig>(Serializer);
        if (res is null)
        {
            validationContext.ModelState.AddModelError(nameof(validationContext.Config),
                "invalid boltz payment config");
            return;
        }

        try
        {
            var client = GetClient(res);
            var info = await client.GetInfo();
            if (info.Node == "standalone")
            {
                validationContext.ModelState.AddModelError(nameof(validationContext.Config),
                    "boltz client has to be connected to a lightning node");
            }
        }
        catch (RpcException e)
        {
            validationContext.ModelState.AddModelError(nameof(validationContext.Config),
                $"connection error: {e.Status.Detail}");
        }
    }

    public PaymentDetails ParsePaymentDetails(JToken details)
    {
        return details.ToObject<PaymentDetails>(Serializer) ??
               throw new FormatException($"Invalid {nameof(PaymentDetails)}");
    }

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }
}