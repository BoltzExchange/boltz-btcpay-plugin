#nullable enable
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
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
            var (client, storeId, invoiceId, lightningPaymentMethodId) = await CreateAndPayInvoiceWithBoltz(tester);
            await AssertInvoiceSettled(client, storeId, invoiceId, lightningPaymentMethodId);
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        public async Task GetsBoltzSettlementDataForSettledLightningPayment()
        {
            using var tester = CreateServerTesterWithBoltz();
            var (_, storeId, invoiceId, _) = await CreateAndPayInvoiceWithBoltz(tester);
            var boltzService = await tester.GetBoltzService();
            var handlers = tester.PayTester.GetService<PaymentMethodHandlerDictionary>();

            await TestUtils.EventuallyAsync(async () =>
            {
                var invoice = await tester.PayTester.InvoiceRepository.GetInvoice(invoiceId);
                var payment = Assert.Single(
                    invoice.GetPayments(false),
                    p => p.PaymentMethodId == PaymentTypes.LN.GetPaymentMethodId("BTC") &&
                         p.Status == PaymentStatus.Settled);

                Assert.True(handlers.TryGetValue(payment.PaymentMethodId, out var handler));
                var prompt = invoice.GetPaymentPrompt(payment.PaymentMethodId);
                Assert.NotNull(prompt);
                var promptDetails = Assert.IsType<LigthningPaymentPromptDetails>(
                    handler.ParsePaymentPromptDetails(prompt!.Details));

                var settlementData = await boltzService.GetBoltzSettlementData(storeId, payment, invoice);
                Assert.NotNull(settlementData);
                Assert.Equal(promptDetails.InvoiceId, settlementData!.SwapId);
                Assert.Equal("LBTC", settlementData.SettlementCurrency);
                Assert.False(string.IsNullOrWhiteSpace(settlementData.SettlementTransactionId));
            }, TestUtils.TestTimeout);
        }

        private static async Task<(BTCPayServerClient Client, string StoreId, string InvoiceId, string LightningPaymentMethodId)>
            CreateAndPayInvoiceWithBoltz(ServerTester tester)
        {
            var account = await tester.CreateTestStore();
            await tester.SetupBoltzForStore(account.StoreId);

            var client = await account.CreateClient();
            var lightningPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC").ToString();
            var invoiceId = (await client.CreateInvoice(account.StoreId, new CreateInvoiceRequest
            {
                Amount = 5m,
                Currency = "USD"
            })).Id;

            var lightningMethod = await WaitForLightningMethod(client, account.StoreId, invoiceId, lightningPaymentMethodId);
            await tester.PayWithBoltzRegtestLnd(lightningMethod.Destination);

            return (client, account.StoreId, invoiceId, lightningPaymentMethodId);
        }

        private static async Task<InvoicePaymentMethodDataModel> WaitForLightningMethod(
            BTCPayServerClient client,
            string storeId,
            string invoiceId,
            string lightningPaymentMethodId)
        {
            InvoicePaymentMethodDataModel? lightningMethod = null;
            await TestUtils.EventuallyAsync(async () =>
            {
                var methods = await client.GetInvoicePaymentMethods(storeId, invoiceId);
                lightningMethod = Assert.Single(methods, m => m.PaymentMethodId == lightningPaymentMethodId);
                Assert.True(lightningMethod.Activated);
                Assert.False(string.IsNullOrWhiteSpace(lightningMethod.Destination));
            }, TestUtils.TestTimeout);
            return lightningMethod!;
        }

        private static async Task AssertInvoiceSettled(
            BTCPayServerClient client,
            string storeId,
            string invoiceId,
            string lightningPaymentMethodId)
        {
            await TestUtils.EventuallyAsync(async () =>
            {
                var paidInvoice = await client.GetInvoice(storeId, invoiceId);
                Assert.Equal(InvoiceStatus.Settled, paidInvoice.Status);
                Assert.Equal(InvoiceExceptionStatus.None, paidInvoice.AdditionalStatus);

                var methods = await client.GetInvoicePaymentMethods(storeId, invoiceId);
                var paidLightningMethod = Assert.Single(methods, m => m.PaymentMethodId == lightningPaymentMethodId);
                Assert.Contains(
                    paidLightningMethod.Payments,
                    payment => payment.Status == InvoicePaymentMethodDataModel.Payment.PaymentStatus.Settled);
            }, TestUtils.TestTimeout);
        }
    }
}
