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
    StoreRepository storeRepository
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
        if (swap.Status == "transaction.mempool" || swap.Status == "invoice.set" || swap.State == SwapState.Successful)
        {
            var details = new BoltzPaymentHandler.PaymentDetails
            {
                TransactionId = swap.LockupTransactionId,
                SwapId = swap.Id,
            };
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
            }.Set(invoice, paymentHandlers[_paymentMethodId], details);

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

    private async Task CreateNewSwapForInvoice(InvoiceEntity invoice)
    {
        var paymentMethods = invoice.GetPaymentPrompts().ToList()
            .FindAll(prompt => prompt.Activated && prompt.PaymentMethodId == _paymentMethodId);
        var store = await storeRepository.FindStore(invoice.StoreId);
        if (store is null)
            return;
        //invoice.GetPayments().Find(entity => entity.GetDetails<>())
        if (paymentMethods.Any())
        {
            var logs = new InvoiceLogs();
            logs.Write(
                "Partial payment detected, attempting to update all boltz methods with used swaps.",
                InvoiceEventData.EventSeverity.Info);
            foreach (var o in paymentMethods)
            {
                var handler = Handler;
                var promptDetails = Handler.ParsePaymentPromptDetails(o.Details);

                try
                {
                    var config = store.GetPaymentMethodConfig(_paymentMethodId);
                    if (config is null) return;

                    var client = handler.GetClient(handler.ParsePaymentMethodConfig(config));
                    var swapInfo = await client.GetSwapInfo(promptDetails.SwapId);
                    if (swapInfo.Swap.Status == "swap.created") continue;

                    var paymentContext = new PaymentMethodContext(store, store.GetStoreBlob(), config,
                        handler, invoice, logs);
                    var paymentPrompt = paymentContext.Prompt;
                    await handler.BeforeFetchingRates(paymentContext);
                    await paymentContext.CreatePaymentPrompt();
                    if (paymentContext.Status != PaymentMethodContext.ContextStatus.Created)
                    {
                        if (invoice.GetPaymentPrompts().Count() == 1)
                        {
                            invoice.Status = InvoiceStatus.Invalid;
                            await invoiceRepository.UpdateInvoiceStatus(invoice.Id,
                                new InvoiceState(InvoiceStatus.Invalid, InvoiceExceptionStatus.PaidPartial));
                            logs.Write(
                                $"{_paymentMethodId}: Invoice marked invalid since no more payment method is available.",
                                InvoiceEventData.EventSeverity.Warning);
                        }
                        else
                        {
                            o.Inactive = true;
                            await invoiceRepository.UpdatePrompt(invoice.Id, o);
                            logs.Write($"{_paymentMethodId}: Deactivating payment method",
                                InvoiceEventData.EventSeverity.Info);
                        }

                        aggregator.Publish(new InvoiceDataChangedEvent(invoice));
                        continue;
                    }

                    await invoiceRepository.NewPaymentPrompt(invoice.Id, paymentContext);
                    await paymentContext.ActivatingPaymentPrompt();
                    promptDetails = handler.ParsePaymentPromptDetails(paymentPrompt.Details);
                    aggregator.Publish(new InvoiceNewPaymentDetailsEvent(invoice.Id,
                        promptDetails, _paymentMethodId));
                }
                catch (Exception e)
                {
                    logs.Write($"Could not update {_paymentMethodId}: {e.Message}",
                        InvoiceEventData.EventSeverity.Error);
                }
            }


            await invoiceRepository.AddInvoiceLogs(invoice.Id, logs);
        }
    }
}