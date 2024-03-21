#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Boltzrpc;
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
    public string? WalletId { get; set; }

    private Metadata headers;


    private readonly Network _network;
    public ILogger Logger;

    public class BlinkConnectionInit
    {
        [JsonProperty("X-API-KEY")] public string ApiKey { get; set; }
    }

    public BoltzLightningClient(string macaroon, Uri grpcEndpoint, ILogger logger)
    {
        _macaroon = macaroon;
        _grpcEndpoint = grpcEndpoint;
        Logger = logger;
        using var channel = GrpcChannel.ForAddress(grpcEndpoint.AbsoluteUri);
        client = new Boltzrpc.Boltz.BoltzClient(channel);
        headers = new Metadata()
        {
            { "macaroon", macaroon },
        };
    }

    public override string ToString()
    {
        return
            $"type=blink;server={_grpcEndpoint};api-key={_apiKey}{(WalletId is null ? "" : $";wallet-id={WalletId}")}";
    }

    public async Task<LightningInvoice> GetInvoice(string invoiceId,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = new CancellationToken())
    {
        var response = await client.CreateReverseSwapAsync(new CreateReverseSwapRequest
        {
            AcceptZeroConf = true,
            Amount = amount.MilliSatoshi / 1000,
        }, headers, cancellationToken: cancellation);
        return new LightningInvoice
        {
            ExpiresAt = DateTimeOffset.Now.AddHours(1)
        };
    }

    public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = new CancellationToken())
    {
        return CreateInvoice(createInvoiceRequest.Amount, createInvoiceRequest.Description, createInvoiceRequest.Expiry,
            cancellation);
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<PayResponse> Pay(PayInvoiceParams payParams,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
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
}