#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Client.Models;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using LightningChannel = BTCPayServer.Lightning.LightningChannel;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzLightningClient : ILightningClient
{
    private readonly string _apiKey;
    private readonly string _macaroon;
    private readonly Uri _grpcEndpoint;
    private Boltzrpc.Boltz.BoltzClient client;
    private BoltzClient _client;
    public string? WalletId { get; set; }

    private readonly Network _network;

    private string _wallet;
    private ulong _walletId;

    public BoltzLightningClient(Uri grpcEndpoint, string macaroon, string wallet)
    {
        _client = new BoltzClient(grpcEndpoint, macaroon);
        _macaroon = macaroon;
        _grpcEndpoint = grpcEndpoint;
        _wallet = wallet;
    }

    public override string ToString()
    {
        return $"type=boltz;server={_grpcEndpoint};macaroon={_macaroon};allowinsecure=true";
    }

    private LightningPayment PaymentFromSwapInfo(SwapInfo info)
    {
        var invoice = BOLT11PaymentRequest.Parse(info.Invoice, _network);
        return new LightningPayment()
        {
            Amount = invoice.MinimumAmount,
            Id = info.Id,
            Preimage = info.Preimage,
            PaymentHash = invoice.PaymentHash?.ToString(),
            BOLT11 = info.Invoice,
            Status = info.State switch
            {
                SwapState.Successful => LightningPaymentStatus.Complete,
                SwapState.Pending => LightningPaymentStatus.Pending,
                _ => LightningPaymentStatus.Failed,
            },
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(info.CreatedAt),
            Fee = LightMoney.Satoshis(info.OnchainFee) + LightMoney.Satoshis(info.ServiceFee),
            AmountSent = info.ExpectedAmount,
        };
    }

    private LightningInvoice InvoiceFromSwapInfo(ReverseSwapInfo info)
    {
        var invoice = BOLT11PaymentRequest.Parse(info.Invoice, _network);
        return new LightningInvoice
        {
            Amount = invoice.MinimumAmount,
            Id = info.Id,
            Preimage = info.Preimage,
            PaymentHash = invoice.PaymentHash?.ToString(),
            BOLT11 = info.Invoice,
            ExpiresAt = invoice.ExpiryDate,
            Status = info.State switch
            {
                SwapState.Successful => LightningInvoiceStatus.Paid,
                SwapState.Pending => LightningInvoiceStatus.Unpaid,
                _ => LightningInvoiceStatus.Expired,
            },
        };
    }

    public async Task<LightningInvoice> GetInvoice(string invoiceId,
        CancellationToken cancellation = new CancellationToken())
    {
        var info = await client.GetSwapInfoAsync(new GetSwapInfoRequest { Id = invoiceId });
        return InvoiceFromSwapInfo(info.ReverseSwap);
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new CancellationToken())
    {
        return await ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = new CancellationToken())
    {
        var swaps = await client.ListSwapsAsync(new ListSwapsRequest());
        return swaps.ReverseSwaps.ToList().Select(InvoiceFromSwapInfo).ToArray();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = new CancellationToken())
    {
        return await ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = new CancellationToken())
    {
        var listSwapsRequest = new ListSwapsRequest();
        if (request.OffsetIndex.HasValue)
        {
            listSwapsRequest.Offset = (ulong) request.OffsetIndex.Value;
        }
        if (request.IncludePending.HasValue)
        {
            listSwapsRequest.State = SwapState.Pending;
        }
        var swaps = await client.ListSwapsAsync(listSwapsRequest);
        return swaps.Swaps.ToList().Select(PaymentFromSwapInfo).ToArray();
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = new CancellationToken()) {
        var response = await client.CreateReverseSwapAsync(new CreateReverseSwapRequest
        {
            AcceptZeroConf = true,
            Amount = (ulong)amount.MilliSatoshi / 1000,
            WalletId = _walletId,
            ExternalPay = true,
            Pair = new Pair { From = Currency.Btc, To = Currency.Lbtc },
        }, cancellationToken: cancellation);

        return await GetInvoice(response.Id, cancellation);
    }

    public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = new CancellationToken())
    {
        return CreateInvoice(createInvoiceRequest.Amount, createInvoiceRequest.Description, createInvoiceRequest.Expiry,
            cancellation);
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new CancellationToken())
    {
        return new BoltzInvoiceListener(this, cancellation);
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new CancellationToken())
    {
        var wallet = await _client.GetWallet(_wallet);
        _walletId = wallet.Id;
        var info = await _client.GetInfo();
        return new LightningNodeInformation
        {
            Alias = "boltz-client",
            Version = info.Version,
            BlockHeight = (int)info.BlockHeights.Btc,
        };
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new CancellationToken())
    {
        var wallet = await _client.GetWallet(_wallet);
        return new LightningNodeBalance
        {
            OnchainBalance = new OnchainBalance
            {
                Confirmed = Money.Satoshis(wallet.Balance.Confirmed),
                Unconfirmed = Money.Satoshis(wallet.Balance.Unconfirmed),
            }
        };
    }

    public async Task<PayResponse> Pay(PayInvoiceParams payParams,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = new CancellationToken())
    {
        var response = await _client.CreateSwap(new CreateSwapRequest
        {
            Invoice = bolt11,
            SendFromInternal = true,
            WalletId = _walletId,
            Pair = new Pair { From = Currency.Lbtc, To = Currency.Btc },
        });
        return new PayResponse(PayResult.Ok, new PayDetails
        {
        });
    }

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public class BoltzInvoiceListener : ILightningInvoiceListener
    {
        private readonly BoltzLightningClient _boltzLightningClient;
        private readonly CancellationToken _cancellationToken;

        public BoltzInvoiceListener(BoltzLightningClient boltzLightningClient, CancellationToken cancellationToken)
        {
            _boltzLightningClient = boltzLightningClient;
            _cancellationToken = cancellationToken;

            boltzLightningClient._client.SwapUpdate += OnSwapUpdate;
        }

        private readonly ConcurrentQueue<Task<LightningInvoice>> _invoices = new();

        private void OnSwapUpdate(object? sender, GetSwapInfoResponse e)
        {
            var reverse = e.ReverseSwap;
            if (reverse != null && reverse.State == SwapState.Successful)
            {
                _invoices.Enqueue(_boltzLightningClient.GetInvoice(reverse.Id, _cancellationToken));
            }
        }

        public void Dispose()
        {
            _boltzLightningClient._client.SwapUpdate -= OnSwapUpdate;
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            while (cancellation.IsCancellationRequested is not true)
            {
                if (_invoices.TryDequeue(out var task))
                {
                    return await task.WithCancellation(cancellation);
                }

                await Task.Delay(100, cancellation);
            }

            cancellation.ThrowIfCancellationRequested();
            return null;
        }
    }
}