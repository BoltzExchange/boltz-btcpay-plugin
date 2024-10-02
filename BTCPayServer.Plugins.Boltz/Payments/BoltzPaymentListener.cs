using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime.Internal.Util;
using Boltzrpc;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Boltz.Payments;

public class BoltzPaymentListener(
    PaymentMethodHandlerDictionary paymentHandlers,
    PaymentService paymentService,
    InvoiceRepository invoiceRepository,
    EventAggregator aggregator,
    BoltzDaemon daemon,
    ILogger<BoltzPaymentListener> logger
) : IHostedService
{
    private readonly PaymentMethodId _paymentMethodId = BoltzPaymentHandler.GetPaymentMethodId("BTC");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await daemon.InitialStart.Task;
        await PollInvoices();
        daemon.SwapUpdate += async (_, info) =>
        {
            if (info.Swap is not null)
            {
                var invoice = await invoiceRepository.GetInvoiceFromAddress(_paymentMethodId, info.Swap.LockupAddress);
                await UpdateInvoice(invoice, info.Swap);
            }
        };
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
    }

    public async Task PollInvoices()
    {
        var invoices = await invoiceRepository.GetMonitoredInvoices(_paymentMethodId);
        var handler = (BoltzPaymentHandler)paymentHandlers[_paymentMethodId];
        var client = daemon.AdminClient!;
        logger.LogInformation($"{invoices.Length} boltz related invoices being checked");
        foreach (var invoice in invoices)
        {
            var prompt = invoice.GetPaymentPrompt(_paymentMethodId);
            if (prompt is not null)
            {
                var details = handler.ParsePaymentDetails(prompt.Details);
                if (details.SwapId is null) continue;
                var response = await client.GetSwapInfo(details.SwapId);
                await UpdateInvoice(invoice, response.Swap);
            }
        }
    }

    private async Task UpdateInvoice(InvoiceEntity invoice, SwapInfo swap)
    {
        if (swap.Status == "transaction.mempool" || swap.State == SwapState.Successful)
        {
            var paymentData = new PaymentData
            {
                Id = swap.LockupTransactionId,
                Created = DateTimeOffset.UtcNow,
                Status = swap.State == SwapState.Successful
                    ? PaymentStatus.Settled
                    : PaymentStatus.Processing,
                Amount = LightMoney.FromUnit(swap.ExpectedAmount, LightMoneyUnit.Satoshi)
                    .ToDecimal(LightMoneyUnit.BTC),
                Currency = "BTC",
            }.Set(invoice, paymentHandlers[_paymentMethodId], new object());

            var alreadyExist = invoice
                .GetPayments(false).Find(c => c.Id == paymentData.Id);
            if (alreadyExist is null)
            {
                var payment = await paymentService.AddPayment(paymentData, [paymentData.Id]);
                if (payment != null)
                {
                    aggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment)
                        { Payment = payment });
                }
            }
            else
            {
                alreadyExist.Status = paymentData.Status!.Value;
                await paymentService.UpdatePayments([alreadyExist]);
                aggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
            }
        }
    }
}