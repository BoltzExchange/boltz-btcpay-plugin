using System.Net.Http;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Payouts;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Boltz.Payments;

public class BoltzPayoutHandler(
    IOptions<LightningNetworkOptions> options,
    BTCPayNetworkProvider networkProvider,
    PaymentMethodHandlerDictionary paymentHandlers,
    IHttpClientFactory httpClientFactory,
    UserService userService,
    IAuthorizationService authorizationService)
    : LightningLikePayoutHandler(PayoutTypes.LN.GetPayoutMethodId("BTC"), options,
        networkProvider.GetNetwork<BTCPayNetwork>("BTC"),
        paymentHandlers, httpClientFactory, userService,
        authorizationService), IPayoutHandler
{
    public new (bool valid, string error) ValidateClaimDestination(IClaimDestination claimDestination,
        PullPaymentBlob pullPaymentBlob)
    {
        if (claimDestination is BoltInvoiceClaimDestination bolt)
        {
            if (bolt.PaymentRequest.MinimumAmount == 0)
            {
                return (false, "0 amount invoices are not supported");
            }
        }

        return base.ValidateClaimDestination(claimDestination, pullPaymentBlob);
    }
};
/*
public class BoltzPayoutHandler : IPayoutHandler
{
    public BoltzPayoutHandler(
        PayoutMethodId payoutMethodId,
        BTCPayNetwork network
    )
    {
    }

    public bool IsSupported(StoreData storeData)
    {
        throw new NotImplementedException();
    }

    public async Task TrackClaim(ClaimRequest claimRequest, PayoutData payoutData)
    {
        throw new NotImplementedException();
    }

    public async Task<(IClaimDestination destination, string error)> ParseClaimDestination(string destination,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public (bool valid, string error) ValidateClaimDestination(IClaimDestination claimDestination,
        PullPaymentBlob pullPaymentBlob)
    {
        throw new NotImplementedException();
    }

    public IPayoutProof ParseProof(PayoutData payout)
    {
        throw new NotImplementedException();
    }

    public void StartBackgroundCheck(Action<Type[]> subscribe)
    {
        throw new NotImplementedException();
    }

    public async Task BackgroundCheck(object o)
    {
        throw new NotImplementedException();
    }

    public async Task<decimal> GetMinimumPayoutAmount(IClaimDestination claimDestination)
    {
        throw new NotImplementedException();
    }

    public Dictionary<PayoutState, List<(string Action, string Text)>> GetPayoutSpecificActions()
    {
        throw new NotImplementedException();
    }

    public async Task<StatusMessageModel> DoSpecificAction(string action, string[] payoutIds, string storeId)
    {
        throw new NotImplementedException();
    }

    public async Task<IActionResult> InitiatePayment(string[] payoutIds)
    {
        throw new NotImplementedException();
    }

    public string Currency { get; }
    public PayoutMethodId PayoutMethodId { get; }
}
*/