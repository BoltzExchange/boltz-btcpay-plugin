using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Reporting;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzPaymentsReportProvider : ReportProvider
{
    private readonly DisplayFormatter _displayFormatter;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly BoltzService _boltzService;

    public BoltzPaymentsReportProvider(
        DisplayFormatter displayFormatter,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        BoltzService boltzService)
    {
        _displayFormatter = displayFormatter;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _boltzService = boltzService;
    }

    public override string Name => "Payments";

    private ViewDefinition CreateViewDefinition()
    {
        return new()
        {
            Fields =
            {
                new ("Date", "datetime"),
                new ("InvoiceId", "invoice_id"),
                new ("OrderId", "string"),
                new ("Category", "string"),
                new ("PaymentMethodId", "string"),
                new ("Confirmed", "boolean"),
                new ("Address", "string"),
                new ("PaymentCurrency", "string"),
                new ("PaymentAmount", "amount"),
                new ("PaymentMethodFee", "decimal"),
                new ("LightningAddress", "string"),
                new ("BoltzSwapId", "string"),
                new ("SettlementCurrency", "string"),
                new ("SettlementAddress", "string"),
                new ("SettlementTransactionId", "string"),
                new ("InvoiceCurrency", "string"),
                new ("InvoiceCurrencyAmount", "amount"),
                new ("Rate", "amount")
            },
            Charts =
            {
                new ()
                {
                    Name = "Aggregated by payment's currency",
                    Groups = { "PaymentCurrency", "PaymentMethodId" },
                    Totals = { "PaymentCurrency" },
                    HasGrandTotal = false,
                    Aggregates = { "PaymentAmount" }
                },
                new ()
                {
                    Name = "Aggregated by invoice's currency",
                    Groups = { "InvoiceCurrency" },
                    Totals = { "InvoiceCurrency" },
                    HasGrandTotal = false,
                    Aggregates = { "InvoiceCurrencyAmount" }
                },
                new ()
                {
                    Name = "Group by Lightning Address",
                    Filters = { "typeof this.LightningAddress === 'string' && this.PaymentCurrency == \"BTC\"" },
                    Groups = { "LightningAddress", "InvoiceCurrency" },
                    Aggregates = { "InvoiceCurrencyAmount" },
                    HasGrandTotal = true
                },
                new ()
                {
                    Name = "Group by Lightning Address (Crypto)",
                    Filters = { "typeof this.LightningAddress === 'string' && this.PaymentCurrency == \"BTC\"" },
                    Groups = { "LightningAddress", "PaymentCurrency" },
                    Aggregates = { "PaymentAmount" },
                    HasGrandTotal = true
                }
            }
        };
    }

    public override async Task Query(QueryContext queryContext, CancellationToken cancellation)
    {
        queryContext.ViewDefinition = CreateViewDefinition();
        var invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery
        {
            StoreId = [queryContext.StoreId],
            StartDate = queryContext.From,
            EndDate = queryContext.To,
            OrderByDesc = false,
        }, cancellation);

        var boltzPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");

        foreach (var invoice in invoices)
        {
            foreach (var payment in invoice.GetPayments(true))
            {
                var values = queryContext.CreateData();
                values.Add(invoice.InvoiceTime);
                values.Add(invoice.Id);
                values.Add(invoice.Metadata.OrderId);

                var paymentMethodId = payment.PaymentMethodId;
                _handlers.TryGetValue(paymentMethodId, out var handler);

                BoltzSettlementData? boltzPaymentData = null;
                if (paymentMethodId == boltzPaymentMethodId)
                {
                    boltzPaymentData = await _boltzService.GetBoltzSettlementData(queryContext.StoreId, payment, invoice);
                }

                var isBoltzLightningPayment =
                    paymentMethodId == boltzPaymentMethodId &&
                    boltzPaymentData is not null;

                if (handler is ILightningPaymentHandler)
                {
                    values.Add(isBoltzLightningPayment ? "Lightning via Boltz" : "Lightning");
                }
                else if (handler is BitcoinLikePaymentHandler)
                {
                    values.Add("On-Chain");
                }
                else
                {
                    values.Add(paymentMethodId.ToString());
                }

                values.Add(paymentMethodId.ToString());
                values.Add(payment.Status is PaymentStatus.Settled);
                values.Add(payment.Destination);
                values.Add(payment.Currency);
                values.Add(new FormattedAmount(payment.Value, payment.Divisibility).ToJObject());
                values.Add(payment.PaymentMethodFee);

                var prompt = invoice.GetPaymentPrompt(PaymentTypes.LNURL.GetPaymentMethodId("BTC"));
                var consumedLightningAddress = prompt is null || handler is not LNURLPayPaymentHandler lnurlHandler
                    ? null
                    : lnurlHandler.ParsePaymentPromptDetails(prompt.Details).ConsumedLightningAddress;
                values.Add(consumedLightningAddress);

                values.Add(boltzPaymentData?.SwapId);
                values.Add(boltzPaymentData?.SettlementCurrency);
                values.Add(boltzPaymentData?.SettlementAddress);
                values.Add(boltzPaymentData?.SettlementTransactionId);

                values.Add(invoice.Currency);
                if (invoice.TryGetRate(payment.Currency, out var rate))
                {
                    values.Add(_displayFormatter.ToFormattedAmount(rate * payment.Value, invoice.Currency));
                    values.Add(_displayFormatter.ToFormattedAmount(rate, invoice.Currency));
                }
                else
                {
                    values.Add(null);
                    values.Add(null);
                }

                queryContext.Data.Add(values);
            }
        }
    }
}
