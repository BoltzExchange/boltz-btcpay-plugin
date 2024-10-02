using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Payments;

public class BoltzPaymentConfig
{
    public ulong WalletId { get; set; }
}

public class BoltzPaymentHandler(
    BTCPayNetworkProvider networkProvider,
    IFeeProviderFactory feeRateProviderFactory,
    //BoltzService boltzService
    BoltzDaemon daemon

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
        public Task<FeeRate> GetNetworkFeeRate;
        public Task<CreateSwapResponse> CreateSwap;
    }

    public class PromptDetails : BitcoinPaymentPromptDetails
    {
        public string SwapId { get; set; }
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
        //paymentMethodContext.Prompt.Calculate()
        if (paymentMethodContext.Prompt.Activated)
        {
            var storeBlob = paymentMethodContext.StoreBlob;
            var store = paymentMethodContext.Store;
            //var client = boltzService.GetClient(store.Id);
            var client = daemon.AdminClient!;
            paymentMethodContext.State = new Prepare()
            {
                GetRecommendedFeeRate =
                    feeRateProviderFactory.CreateFeeProvider(Network)
                        .GetFeeRateAsync(storeBlob.RecommendedFeeBlockTarget),
                GetNetworkFeeRate = storeBlob.NetworkFeeMode == NetworkFeeMode.Never
                    ? null
                    : feeRateProviderFactory.CreateFeeProvider(Network).GetFeeRateAsync(),
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
        switch (onchainMethod.FeeMode)
        {
            case NetworkFeeMode.Always:
            case NetworkFeeMode.MultiplePaymentsOnly:
                onchainMethod.PaymentMethodFeeRate = (await prepare.GetNetworkFeeRate);
                if (onchainMethod.FeeMode == NetworkFeeMode.Always || paymentMethod.Calculate().TxCount > 0)
                {
                    paymentMethod.PaymentMethodFee =
                        onchainMethod.PaymentMethodFeeRate.GetFee(100).GetValue(Network); // assume price for 100 bytes
                }

                break;
            case NetworkFeeMode.Never:
                onchainMethod.PaymentMethodFeeRate = FeeRate.Zero;
                break;
        }

        if (paymentContext.InvoiceEntity.Type != InvoiceType.TopUp)
        {
            var txOut = Network.NBitcoinNetwork.Consensus.ConsensusFactory.CreateTxOut();
            txOut.ScriptPubKey =
                new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
            var dust = txOut.GetDustThreshold();
            var amount = paymentMethod.Calculate().Due;
            if (amount < dust.ToDecimal(MoneyUnit.BTC))
                throw new PaymentMethodUnavailableException(
                    "Amount below the dust threshold. For amounts of this size, it is recommended to enable an off-chain (Lightning) payment method");
        }

        var swap = await prepare.CreateSwap;
        paymentMethod.Destination = swap.Address;
        onchainMethod.SwapId = swap.Id;
        paymentContext.TrackedDestinations.Add(swap.Address);
        paymentMethod.Details = JObject.FromObject(onchainMethod, Serializer);
    }

    public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        var res = validationContext.Config.ToObject<BoltzPaymentConfig>(Serializer);
        if (res is null)
        {
            validationContext.ModelState.AddModelError(nameof(validationContext.Config),
                "Invalid derivation scheme settings");
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    public PromptDetails ParsePaymentDetails(JToken details)
    {
        return details.ToObject<PromptDetails>(Serializer) ??
               throw new FormatException($"Invalid {nameof(BitcoinLikePaymentData)}");
    }

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }
}