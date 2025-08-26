#nullable enable
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;

namespace BTCPayServer.Plugins.Boltz.Payments;

public class BoltzCheckoutModelExtension(
    BitcoinCheckoutModelExtension bitcoinCheckoutModelExtension
) : ICheckoutModelExtension, IGlobalCheckoutModelExtension
{
    public string Image => bitcoinCheckoutModelExtension.Image;
    public string Badge => bitcoinCheckoutModelExtension.Badge;
    public PaymentMethodId PaymentMethodId => bitcoinCheckoutModelExtension.PaymentMethodId;

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        var methods = context.Model.AvailablePaymentMethods;
        if (methods.Find(method => method.PaymentMethodId == PaymentMethodId)?.Displayed ?? false)
        {
            var onchain = methods.Find(method =>
                method.PaymentMethodId == PaymentTypes.CHAIN.GetPaymentMethodId("BTC"));
            if (onchain is not null)
            {
                onchain.Displayed = false;
            }
        }
        bitcoinCheckoutModelExtension.ModifyCheckoutModel(context);
    }
}