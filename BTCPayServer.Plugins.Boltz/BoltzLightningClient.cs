#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Lightning;
using Google.Protobuf;
using Grpc.Core;
using NBitcoin;
using Newtonsoft.Json;
using LightningChannel = BTCPayServer.Lightning.LightningChannel;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Boltz;

public class NodeStats
{
    public BTC BTC { get; set; }
}

public class BTC
{
    public Node CLN { get; set; }
    public Node LND { get; set; }
}

public class Node
{
    public string PublicKey { get; set; }
    public List<string> Uris { get; set; }
}

public class BoltzLightningClient(
    Uri grpcEndpoint,
    string macaroon,
    ulong walletId,
    Network network,
    BoltzDaemon daemon
)
    : ILightningClient
{
    private static NodeInfo? _clnInfo;

    private async Task<BoltzClient> GetClient()
    {
        await daemon.InitialStart.Task;
        return daemon.GetClient(new BoltzSettings { GrpcUrl = grpcEndpoint, Macaroon = macaroon })!;
    }

    // TODO

    public override string ToString()
    {
        return $"type=boltz;server={grpcEndpoint};macaroon={macaroon};walletId={walletId};allowinsecure=true";
    }

    private LightningPayment PaymentFromSwapInfo(SwapInfo info)
    {
        var invoice = BOLT11PaymentRequest.Parse(info.Invoice, network);
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
            AmountSent = LightMoney.Satoshis(info.ExpectedAmount),
        };
    }

    private LightningInvoice InvoiceFromSwapInfo(ReverseSwapInfo info)
    {
        var invoice = BOLT11PaymentRequest.Parse(info.Invoice, network);
        return new LightningInvoice
        {
            Amount = invoice.MinimumAmount,
            Id = info.Id,
            Preimage = info.Preimage,
            PaymentHash = invoice.PaymentHash?.ToString(),
            BOLT11 = info.Invoice,
            ExpiresAt = invoice.ExpiryDate,
            PaidAt = DateTimeOffset.FromUnixTimeSeconds(info.PaidAt),
            Status = info.State switch
            {
                SwapState.Successful => LightningInvoiceStatus.Paid,
                SwapState.Pending => LightningInvoiceStatus.Unpaid,
                _ => LightningInvoiceStatus.Expired,
            },
        };
    }

    public async Task<LightningInvoice> GetInvoice(string invoiceId,
        CancellationToken cancellation = new())
    {
        var client = await GetClient();
        var info = await client.GetSwapInfo(invoiceId);
        return InvoiceFromSwapInfo(info.ReverseSwap);
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash,
        CancellationToken cancellation = new())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new())
    {
        return await ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = new())
    {
        var client = await GetClient();
        var swaps = await client.ListSwaps();
        return swaps.ReverseSwaps.ToList().Select(InvoiceFromSwapInfo).ToArray();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        var payments = await ListPayments(cancellation);
        return payments.ToList().Find(payment => payment.PaymentHash == paymentHash)!;
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = new CancellationToken())
    {
        return await ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = new CancellationToken())
    {
        var listSwapsRequest = new ListSwapsRequest();
        /*
        if (request.OffsetIndex.HasValue)
        {
            listSwapsRequest.Offset = (ulong) request.OffsetIndex.Value;
        }
        */
        if (request.IncludePending.HasValue)
        {
            listSwapsRequest.State = SwapState.Pending;
        }

        var client = await GetClient();
        var swaps = await client.ListSwaps(listSwapsRequest);
        return swaps.Swaps.ToList().Select(PaymentFromSwapInfo).ToArray();
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = new())
    {
        return await CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = new())
    {
        var client = await GetClient();
        var request = new CreateReverseSwapRequest
        {
            AcceptZeroConf = true,
            Amount = (ulong)createInvoiceRequest.Amount.MilliSatoshi / 1000,
            WalletId = walletId,
            ExternalPay = true,
            Pair = new Pair { From = Currency.Btc, To = Currency.Lbtc },
            InvoiceExpiry = (ulong)createInvoiceRequest.Expiry.TotalSeconds,
        };
        if (createInvoiceRequest.DescriptionHashOnly)
        {
            request.DescriptionHash = ByteString.CopyFrom(createInvoiceRequest.DescriptionHash.ToBytes());
            request.DescriptionHash = ByteString.CopyFrom(createInvoiceRequest.DescriptionHash.ToBytes(false));
        }
        else
        {
            request.Description = createInvoiceRequest.Description;
        }

        var response = await client.CreateReverseSwap(request, cancellation);

        return await GetInvoice(response.Id, cancellation);
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new CancellationToken())
    {
        return new BoltzInvoiceListener(this, cancellation);
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new CancellationToken())
    {
        if (_clnInfo == null)
        {
            var httpClient = new HttpClient();
            string url = "https://api.boltz.exchange/v2/nodes";
            HttpResponseMessage response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();

            var nodeStats = JsonConvert.DeserializeObject<NodeStats>(responseBody);
            _clnInfo = NodeInfo.Parse(nodeStats!.BTC.CLN.Uris.First());
        }

        var client = await GetClient();
        var info = await client.GetInfo();
        return new LightningNodeInformation
        {
            Alias = "boltz-client",
            Version = info.Version,
            BlockHeight = (int)info.BlockHeights.Btc,
            Color = "#e6c826",
            NodeInfoList = { _clnInfo }
        };
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new CancellationToken())
    {
        var client = await GetClient();
        var wallet = await client.GetWallet(walletId);
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

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        var invoice = BOLT11PaymentRequest.Parse(bolt11, network);
        if (invoice.MinimumAmount == 0)
        {
            throw new ArgumentException("0 amount invoices are not supported");
        }

        var client = await GetClient();
        var wallet = await client.GetWallet(walletId);
        if (wallet.Readonly)
        {
            throw new InvalidOperationException("payouts cant be made from readonly wallets");
        }

        var response = await client.CreateSwap(new CreateSwapRequest
        {
            Invoice = bolt11,
            SendFromInternal = !wallet.Readonly,
            WalletId = walletId,
            Pair = new Pair { From = Currency.Lbtc, To = Currency.Btc },
        }, cancellation);
        var payDetails = new PayDetails
        {
            TotalAmount = invoice.MinimumAmount,
            Status = LightningPaymentStatus.Pending,
            FeeAmount = LightMoney.Satoshis(response.ExpectedAmount) - invoice.MinimumAmount,
            PaymentHash = invoice.PaymentHash
        };
        // swap was paid directly via onchain (magic routing hint)
        if (response.Id == "")
        {
            payDetails.Status = LightningPaymentStatus.Complete;
            return new PayResponse(PayResult.Ok, payDetails);
        }

        var source = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        cancellation.Register(source.Cancel);
        try
        {
            using var stream = client.GetSwapInfoStream(response.Id);
            while (await stream.ResponseStream.MoveNext(source.Token))
            {
                var swap = stream.ResponseStream.Current.Swap;
                if (swap.State == SwapState.Successful)
                {
                    payDetails.Status = LightningPaymentStatus.Complete;
                    if (!string.IsNullOrEmpty(swap.Preimage))
                    {
                        payDetails.Preimage = uint256.Parse(swap.Preimage);
                    }

                    return new PayResponse(PayResult.Ok, payDetails);
                }

                if (swap.State == SwapState.Error || swap.State == SwapState.ServerError)
                {
                    throw new Exception($"payment failed: {swap.Error}");
                }
            }
        }
        catch (RpcException) when (source.IsCancellationRequested)
        { }

        return new PayResponse(PayResult.Unknown, "payment is waiting for confirmation");
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = default)
    {
        return await Pay(bolt11, cancellation);
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

    public class BoltzInvoiceListener(BoltzLightningClient boltzLightningClient, CancellationToken cancellationToken)
        : ILightningInvoiceListener
    {
        private BoltzClient? _client;
        private AsyncServerStreamingCall<GetSwapInfoResponse>? _stream;

        public void Dispose()
        {
            if (_stream is not null)
            {
                _stream.Dispose();
            }
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            if (_stream is null)
            {
                _client = await boltzLightningClient.GetClient();
                _stream = _client.GetSwapInfoStream("");
            }

            try
            {
                while (await _stream.ResponseStream.MoveNext(cancellation))
                {
                    var id = _stream.ResponseStream.Current.ReverseSwap?.Id;
                    if (id != null)
                    {
                        return await boltzLightningClient.GetInvoice(id, cancellationToken);
                    }
                }

                throw new Exception("stream ended");
            }
            catch (Exception)
            {
                _stream = null;
                throw;
            }
        }
    }
}