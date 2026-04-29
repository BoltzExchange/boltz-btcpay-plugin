using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Integration", "Integration")]
    [Trait("Lightning", "Lightning")]
    public class BoltzInvoiceFlowTests : BoltzTestBase
    {
        public BoltzInvoiceFlowTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        public async Task CanCreateAndPayInvoiceWithBoltz()
        {
            using var tester = CreateServerTesterWithBoltz();
            var account = await tester.CreateTestStore();
            await tester.SetupBoltzForStore(account.StoreId);

            var client = await account.CreateClient();
            var lightningPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString();
            var invoice = await client.CreateInvoice(account.StoreId, new CreateInvoiceRequest
            {
                Amount = 5m,
                Currency = "USD"
            });

            InvoicePaymentMethodDataModel lightningMethod = null;
            await TestUtils.EventuallyAsync(async () =>
            {
                var methods = await client.GetInvoicePaymentMethods(invoice.Id);
                lightningMethod = Assert.Single(methods, m => m.PaymentMethodId == lightningPaymentMethodId);
                Assert.True(lightningMethod.Activated);
                Assert.False(string.IsNullOrWhiteSpace(lightningMethod.Destination));
            }, TestUtils.TestTimeout);

            await tester.PayWithBoltzRegtestLnd(lightningMethod!.Destination);

            await TestUtils.EventuallyAsync(async () =>
            {
                var paidInvoice = await client.GetInvoice(invoice.Id);
                Assert.Equal(InvoiceStatus.Settled, paidInvoice.Status);
                Assert.Equal(InvoiceExceptionStatus.None, paidInvoice.AdditionalStatus);

                var methods = await client.GetInvoicePaymentMethods(invoice.Id);
                var paidLightningMethod = Assert.Single(methods, m => m.PaymentMethodId == lightningPaymentMethodId);
                Assert.Contains(
                    paidLightningMethod.Payments,
                    payment => payment.Status == InvoicePaymentMethodDataModel.Payment.PaymentStatus.Settled);
            }, TestUtils.TestTimeout);
        }
    }
}
