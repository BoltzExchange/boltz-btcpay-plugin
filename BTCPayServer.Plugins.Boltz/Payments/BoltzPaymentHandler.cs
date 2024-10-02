using System;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Payments;

public class BoltzPaymentConfig
{
    public ulong? WalletId { get; set; }
}

public class BoltzPaymentHandler(
    BTCPayNetworkProvider networkProvider,
    IFeeProviderFactory feeRateProviderFactory,
    ILogger<BoltzClient> clientLogger,
    BoltzDaemon daemon,
    Lazy<BoltzService> boltzService
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

    public BoltzClient GetClient(string storeId)
    {
        return boltzService.Value.GetClient(storeId);
    }

    public class PromptDetails : BitcoinPaymentPromptDetails
    {
        public string SwapId { get; set; }
    }

    public class PaymentDetails
    {
        public string SwapId { get; set; }
        public string TransactionId { get; set; }
        public string RefundTransactionId { get; set; }
        public string RefundAddress { get; set; }
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
            var client = GetClient(paymentMethodContext.Store.Id);
            paymentMethodContext.State = new Prepare()
            {
                GetRecommendedFeeRate =
                    feeRateProviderFactory.CreateFeeProvider(Network)
                        .GetFeeRateAsync(storeBlob.RecommendedFeeBlockTarget),
                GetPairInfo = client.GetPairInfo(new Pair { From = Currency.Btc, To = Currency.Btc },
                    SwapType.Submarine),
                CreateSwap = Task.Run(async () =>
                {
                    var prompt = paymentMethodContext.InvoiceEntity.GetPaymentPrompt(PaymentMethodId);
                    if (prompt?.Details != null)
                    {
                        var existing = ParsePaymentDetails(prompt.Details);
                        if (existing is { SwapId: not null })
                        {
                            var swap = await client.GetSwapInfo(existing.SwapId);
                            if (swap is { Swap.Status: "swap.created" })
                                // TODO: return proper info
                                return null;
                        }
                    }


                    var request = new CreateSwapRequest
                    {
                        Pair = new Pair { From = Currency.Btc, To = Currency.Btc },
                        SendFromInternal = false,
                    };

                    if (cfg.WalletId.HasValue)
                    {
                        request.WalletId = cfg.WalletId.Value;
                    }
                    else
                    {
                        var store = paymentMethodContext.Store;
                        request.RefundAddress = await boltzService.Value.GenerateNewAddress(store);
                    }

                    return await client!.CreateSwap(request);
                })
            };
        }

        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext paymentContext)
    {
        var prepare = (Prepare)paymentContext.State;
        var paymentPrompt = paymentContext.Prompt;
        var promptDetails = new PromptDetails();
        var blob = paymentContext.StoreBlob;

        promptDetails.FeeMode = blob.NetworkFeeMode;
        promptDetails.RecommendedFeeRate = await prepare.GetRecommendedFeeRate;
        var amount = paymentPrompt.Calculate().Due;
        var pairInfo = await prepare.GetPairInfo;
        switch (promptDetails.FeeMode)
        {
            case NetworkFeeMode.Always:
            case NetworkFeeMode.MultiplePaymentsOnly:
                if (promptDetails.FeeMode == NetworkFeeMode.Always || paymentPrompt.Calculate().TxCount > 0)
                {
                    var rate = (decimal)pairInfo.Fees.Percentage / 100;
                    var service = LightMoney.FromUnit(amount * rate, LightMoneyUnit.BTC);
                    var minerFee = LightMoney.Satoshis(pairInfo.Fees.MinerFees);
                    paymentPrompt.PaymentMethodFee = (service + minerFee).ToDecimal(LightMoneyUnit.BTC);
                }

                break;
            case NetworkFeeMode.Never:
                promptDetails.PaymentMethodFeeRate = FeeRate.Zero;
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
        if (swap is not null)
        {
            paymentPrompt.Destination = swap.Address;
            promptDetails.SwapId = swap.Id;
            paymentContext.TrackedDestinations.Add(swap.Address);
        }

        paymentPrompt.Details = JObject.FromObject(promptDetails, Serializer);
    }

    public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        var res = validationContext.Config.ToObject<BoltzPaymentConfig>(Serializer);
        if (res is null)
        {
            validationContext.ModelState.AddModelError(nameof(validationContext.Config),
                "invalid boltz payment config");
        }
        return Task.CompletedTask;
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