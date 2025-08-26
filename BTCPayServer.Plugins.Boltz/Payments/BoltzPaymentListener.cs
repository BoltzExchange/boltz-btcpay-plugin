using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime.Internal.Util;
using Boltzrpc;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBXplorer;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Payments;

public class BoltzPaymentListener(
    PaymentMethodHandlerDictionary paymentHandlers,
    PaymentService paymentService,
    InvoiceRepository invoiceRepository,
    EventAggregator aggregator,
    BoltzDaemon daemon,
    ILogger<BoltzPaymentListener> logger,
    InvoiceActivator invoiceActivator
) : IHostedService
{
    private readonly PaymentMethodId _paymentMethodId = BoltzPaymentHandler.GetPaymentMethodId("BTC");
    private BoltzPaymentHandler Handler => (BoltzPaymentHandler)paymentHandlers[_paymentMethodId];

    readonly CompositeDisposable leases = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await daemon.InitialStart.Task;
        await PollInvoices();
        daemon.SwapUpdate += async (_, info) =>
        {
            if (info.Swap is not null)
            {
                var invoice = await invoiceRepository.GetInvoiceFromAddress(_paymentMethodId, info.Swap.LockupAddress);
                if (invoice is not null)
                    await UpdateInvoice(invoice, info.Swap);
            }
        };
        leases.Add(aggregator.SubscribeAsync<Events.InvoiceEvent>(async inv =>
        {
            if (inv.Name == InvoiceEvent.ReceivedPayment && inv.Invoice.Status == InvoiceStatus.New &&
                inv.Invoice.ExceptionStatus == InvoiceExceptionStatus.PaidPartial)
            {
                var pm = inv.Invoice.GetPaymentPrompts().First();
                if (pm.Calculate().Due > 0m)
                {
                    await CreateNewSwapForInvoice(inv.Invoice);
                }
            }
        }));
        leases.Add(aggregator.SubscribeAsync<Events.InvoiceDataChangedEvent>(async inv =>
        {
            if (inv.State.Status == InvoiceStatus.New &&
                inv.State.ExceptionStatus == InvoiceExceptionStatus.PaidPartial)
            {
                var invoice = await invoiceRepository.GetInvoice(inv.InvoiceId);
                await CreateNewSwapForInvoice(invoice);
            }
        }));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        leases.Dispose();
    }

    public async Task PollInvoices()
    {
        var invoices = await invoiceRepository.GetMonitoredInvoices(_paymentMethodId);
        var client = daemon.AdminClient!;
        logger.LogInformation($"{invoices.Length} boltz related invoices being checked");
        foreach (var invoice in invoices)
        {
            var prompt = invoice.GetPaymentPrompt(_paymentMethodId);
            if (prompt is not null)
            {
                var details = Handler.ParsePaymentDetails(prompt.Details);
                if (details.SwapId is null) continue;
                var response = await client.GetSwapInfo(details.SwapId);
                await UpdateInvoice(invoice, response.Swap);
            }
        }
    }

    private async Task UpdateInvoice(InvoiceEntity invoice, SwapInfo swap)
    {
        var settled = swap.State is SwapState.Successful or SwapState.Refunded;
        var lockupFailed = swap.State == SwapState.Error && swap.LockupTransactionId != "";
        if (swap.Status == "transaction.mempool" || settled || lockupFailed)
        {
            var details = new BoltzPaymentHandler.PaymentDetails
            {
                TransactionId = swap.LockupTransactionId,
                SwapId = swap.Id,
            };
            if (swap.State == SwapState.Refunded)
            {
                details.RefundTransactionId = swap.RefundTransactionId;
                details.RefundAddress = swap.RefundAddress;
            }

            var paymentData = new PaymentData
            {
                Id = swap.LockupTransactionId,
                Created = DateTimeOffset.UtcNow,
                Status = settled
                    ? PaymentStatus.Settled
                    : PaymentStatus.Processing,
                Amount = LightMoney.FromUnit(swap.ExpectedAmount, LightMoneyUnit.Satoshi)
                    .ToDecimal(LightMoneyUnit.BTC),
                Currency = "BTC",
            }.Set(invoice, Handler, details);
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
                alreadyExist.Details = paymentData.GetBlob().Details;
                await paymentService.UpdatePayments([alreadyExist]);
                aggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
            }
        }
    }

    private async Task CreateNewSwapForInvoice(InvoiceEntity invoice)
    {
        var prompt = invoice.GetPaymentPrompt(_paymentMethodId);
        if (prompt is { Activated: true })
        {
            var logs = new InvoiceLogs();
            logs.Write(
                "Partial payment detected, attempting to update all boltz methods with used swaps.",
                InvoiceEventData.EventSeverity.Info);
            if (!await invoiceActivator.ActivateInvoicePaymentMethod(invoice.Id, _paymentMethodId, true))
            {
                prompt.Inactive = true;
                await invoiceRepository.UpdatePrompt(invoice.Id, prompt);

                if (invoice.GetPaymentPrompts().Count() == 1)
                {
                    invoice.Status = InvoiceStatus.Invalid;
                    await invoiceRepository.UpdateInvoiceStatus(invoice.Id,
                        new InvoiceState(InvoiceStatus.Invalid, InvoiceExceptionStatus.PaidPartial));
                    logs.Write(
                        $"{_paymentMethodId}: Invoice marked invalid since no more payment method is available.",
                        InvoiceEventData.EventSeverity.Warning);
                }
            }

            await invoiceRepository.AddInvoiceLogs(invoice.Id, logs);
        }
    }
}