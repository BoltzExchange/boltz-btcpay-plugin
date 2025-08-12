#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Plugins.Boltz.Models;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.X509;
using GetInfoResponse = Boltzrpc.GetInfoResponse;
using ImportWalletRequest = Boltzrpc.ImportWalletRequest;
using UpdateChainConfigRequest = Autoswaprpc.UpdateChainConfigRequest;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzClient : IDisposable
{
    private readonly Metadata _metadata;
    private readonly Boltzrpc.Boltz.BoltzClient _client;
    private readonly AutoSwap.AutoSwapClient _autoClient;

    private static readonly Dictionary<Uri, GrpcChannel> Channels = new();
    private static GetPairsResponse? _pairs;
    private readonly ILogger<BoltzClient> _logger;

    // Add default timeout and call options helpers
    private static readonly TimeSpan DefaultGrpcTimeout = TimeSpan.FromSeconds(5);
    private CallOptions _callOptions => new CallOptions(headers: _metadata, cancellationToken: new CancellationTokenSource(DefaultGrpcTimeout).Token);
    private CallOptions CreateCallOptions(CancellationToken cancellationToken)
    {
        return cancellationToken == default
            ? _callOptions
            : new CallOptions(headers: _metadata, cancellationToken: cancellationToken);
    }

    public BoltzClient(ILogger<BoltzClient> logger, Uri grpcEndpoint, string macaroon, string certPath,
        string? tenant = null)
    {
        var http = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                {
                    if (certificate == null) throw new ArgumentNullException(nameof(certificate));
                    var expectedCollection = new X509Certificate2Collection();
                    expectedCollection.ImportFromPemFile(certPath);
                    return expectedCollection.Contains(certificate);
                },
            },
        };

        var opt = new GrpcChannelOptions
        {
            Credentials = ChannelCredentials.SecureSsl,
            HttpHandler = http
        };

        if (!Channels.TryGetValue(grpcEndpoint, out var channel))
        {
            channel = GrpcChannel.ForAddress(grpcEndpoint, opt);
            Channels.Add(grpcEndpoint, channel);
        }

        _logger = logger;
        _client = new(channel);
        _autoClient = new(channel);

        _metadata = new Metadata();
        _metadata.Add("macaroon", macaroon);

        if (tenant is not null)
        {
            _metadata.Add("tenant", tenant);
        }
    }

    public async Task<GetInfoResponse> GetInfo(CancellationToken cancellationToken = default)
    {
        return await _client.GetInfoAsync(new GetInfoRequest(), CreateCallOptions(cancellationToken));
    }

    public async Task<ListSwapsResponse> ListSwaps()
    {
        return await ListSwaps(new ListSwapsRequest());
    }

    public async Task<ListSwapsResponse> ListSwaps(ListSwapsRequest request)
    {
        return await _client.ListSwapsAsync(request, _callOptions);
    }


    public async Task<GetSwapInfoResponse> GetSwapInfo(string id)
    {
        return await _client.GetSwapInfoAsync(new GetSwapInfoRequest { SwapId = id }, _callOptions);
    }

    public async Task<GetSwapInfoResponse> GetSwapInfo(byte[] paymentHash)
    {
        return await _client.GetSwapInfoAsync(new GetSwapInfoRequest { PaymentHash = ByteString.CopyFrom(paymentHash) }, _callOptions);
    }

    public AsyncServerStreamingCall<GetSwapInfoResponse> GetSwapInfoStream(string id,
        CancellationToken cancellationToken = default)
    {
        return _client.GetSwapInfoStream(new GetSwapInfoRequest { SwapId = id }, CreateCallOptions(cancellationToken));
    }

    public async Task<Wallets> GetWallets(bool includeReadonly)
    {
        return await _client.GetWalletsAsync(new GetWalletsRequest { IncludeReadonly = includeReadonly }, _callOptions);
    }

    public async Task<Wallet> GetWallet(string name)
    {
        return await _client.GetWalletAsync(new GetWalletRequest { Name = name }, _callOptions);
    }

    public async Task<ListWalletTransactionsResponse> ListWalletTransactions(ListWalletTransactionsRequest request)
    {
        return await _client.ListWalletTransactionsAsync(request, _callOptions);
    }

    public async Task<GetSubaccountsResponse> GetSubaccounts(ulong walletId)
    {
        return await _client.GetSubaccountsAsync(new GetSubaccountsRequest() { WalletId = walletId }, _callOptions);
    }

    public async Task SetSubaccount(ulong walletId, ulong? subaccount)
    {
        var request = new SetSubaccountRequest { WalletId = walletId };
        if (subaccount.HasValue)
        {
            request.Subaccount = subaccount.Value;
        }

        await _client.SetSubaccountAsync(request, _callOptions);
    }

    public async Task<Wallet> GetWallet(ulong id)
    {
        return await _client.GetWalletAsync(new GetWalletRequest { Id = id }, _callOptions);
    }

    public async Task<RemoveWalletResponse> RemoveWallet(ulong id)
    {
        return await _client.RemoveWalletAsync(new RemoveWalletRequest { Id = id }, _callOptions);
    }

    public async Task<WalletSendFee> GetWalletSendFee(WalletSendRequest request)
    {
        return await _client.GetWalletSendFeeAsync(request, _callOptions);
    }

    public async Task<WalletCredentials> GetWalletCredentials(ulong id)
    {
        return await _client.GetWalletCredentialsAsync(new GetWalletCredentialsRequest { Id = id }, _callOptions);
    }

    public async Task<CreateWalletResponse> CreateWallet(WalletParams @params)
    {
        return await _client.CreateWalletAsync(new CreateWalletRequest { Params = @params }, _callOptions);
    }

    public async Task<Wallet> ImportWallet(WalletParams @params, WalletCredentials credentials)
    {
        return await _client.ImportWalletAsync(new ImportWalletRequest { Params = @params, Credentials = credentials },
            _callOptions);
    }

    public async Task<WalletSendResponse> WalletSend(WalletSendRequest request)
    {
        return await _client.WalletSendAsync(request, _callOptions);
    }

    public async Task<WalletReceiveResponse> WalletReceive(ulong id)
    {
        return await _client.WalletReceiveAsync(new WalletReceiveRequest { Id = id }, _callOptions);
    }

    public async Task<GetRecommendationsResponse> GetAutoSwapRecommendations()
    {
        return await _autoClient.GetRecommendationsAsync(new GetRecommendationsRequest(), _callOptions);
    }

    public async Task<ExecuteRecommendationsResponse> ExecuteAutoSwapRecommendations(
        ExecuteRecommendationsRequest request)
    {
        return await _autoClient.ExecuteRecommendationsAsync(request, _callOptions);
    }

    public async Task<GetPairsResponse> GetPairs()
    {
        _pairs = await _client.GetPairsAsync(new Empty(), _callOptions);
        return _pairs;
    }

    public async Task<SwapStats> GetStats()
    {
        return (await _client.GetStatsAsync(new GetStatsRequest { Include = IncludeSwaps.Manual }, _callOptions)).Stats;
    }

    public async Task ResetLnConfig()
    {
        await _autoClient.UpdateLightningConfigAsync(new UpdateLightningConfigRequest { Reset = true }, _callOptions);
    }

    public async Task ResetChainConfig()
    {
        await _autoClient.UpdateChainConfigAsync(new UpdateChainConfigRequest { Reset = true }, _callOptions);
    }

    private ChainConfig? ChainConfig(Config config)
    {
        return config.Chain.Count > 0 ? config.Chain[0] : null;
    }

    private LightningConfig? LightningConfig(Config config)
    {
        return config.Lightning.Count > 0 ? config.Lightning[0] : null;
    }

    public async Task<LightningConfig?> GetLightningConfig()
    {
        return (await GetAutoSwapConfig()).Item1;
    }

    public async Task<ChainConfig?> GetChainConfig()
    {
        return (await GetAutoSwapConfig()).Item2;
    }

    public async Task<bool> IsAutoSwapConfigured()
    {
        var (ln, chain) = await GetAutoSwapConfig();
        return ln is not null || chain is not null;
    }

    public async Task<(LightningConfig?, ChainConfig?)> GetAutoSwapConfig()
    {
        var config = await _autoClient.GetConfigAsync(new GetConfigRequest(), _callOptions);
        return (LightningConfig(config), ChainConfig(config));
    }

    public async Task EnableAutoSwap()
    {
        var (ln, chain) = await GetAutoSwapConfig();
        if (ln is not null)
        {
            await UpdateAutoSwapLightningConfig(new LightningConfig { Enabled = true }, new[] { "enabled" });
        }

        if (chain is not null)
        {
            await UpdateAutoSwapChainConfig(new ChainConfig { Enabled = true }, new[] { "enabled" });
        }
    }

    public async Task<GetStatusResponse> GetAutoSwapStatus()
    {
        return await _autoClient.GetStatusAsync(new GetStatusRequest(), _callOptions);
    }

    public async Task UpdateAutoSwapConfig(BoltzConfig data)
    {
        if (data.Chain != null)
        {
            await _autoClient.UpdateChainConfigAsync(new UpdateChainConfigRequest()
            {
                Config = data.Chain
            }, _callOptions);
        }

        if (data.Ln != null)
        {
            await _autoClient.UpdateLightningConfigAsync(new UpdateLightningConfigRequest
            {
                Config = data.Ln
            }, _callOptions);
        }
    }

    public async Task<LightningConfig> UpdateAutoSwapLightningConfig(LightningConfig config, IEnumerable<string>? paths)
    {
        var request = new UpdateLightningConfigRequest { Config = config };
        if (paths is not null)
        {
            request.FieldMask = FieldMask.FromStringEnumerable<LightningConfig>(paths);
        }

        var result = await _autoClient.UpdateLightningConfigAsync(request, _callOptions);
        return result.Lightning[0];
    }

    public async Task<ChainConfig> UpdateAutoSwapChainConfig(ChainConfig config, IEnumerable<string>? paths = null)
    {
        var request = new UpdateChainConfigRequest { Config = config };
        if (paths is not null)
        {
            request.FieldMask = FieldMask.FromStringEnumerable<ChainConfig>(paths);
        }

        var result = await _autoClient.UpdateChainConfigAsync(request, _callOptions);
        return result.Chain[0];
    }

    public Boltzrpc.Boltz.BoltzClient GetClient()
    {
        return _client;
    }

    public async Task<CreateReverseSwapResponse> CreateReverseSwap(CreateReverseSwapRequest request,
        CancellationToken cancellation = new CancellationToken())
    {
        return await _client.CreateReverseSwapAsync(request, CreateCallOptions(cancellation));
    }

    public async Task<ChainSwapInfo> CreateChainSwap(CreateChainSwapRequest request,
        CancellationToken cancellation = default)
    {
        return await _client.CreateChainSwapAsync(request, CreateCallOptions(cancellation));
    }

    public async Task<CreateSwapResponse> CreateSwap(CreateSwapRequest request,
        CancellationToken cancellation = default)
    {
        return await _client.CreateSwapAsync(request, CreateCallOptions(cancellation));
    }

    public async Task<GetSwapInfoResponse> RefundSwap(RefundSwapRequest request,
        CancellationToken cancellation = default)
    {
        return await _client.RefundSwapAsync(request, CreateCallOptions(cancellation));
    }

    public async Task<Tenant> CreateTenant(string name)
    {
        return await _client.CreateTenantAsync(new CreateTenantRequest { Name = name }, _callOptions);
    }

    public async Task<Tenant> GetTenant(string name)
    {
        return await _client.GetTenantAsync(new GetTenantRequest { Name = name }, _callOptions);
    }

    public async Task Stop(CancellationToken cancellationToken = default)
    {
        if (_invoiceStreamCancel is not null)
        {
            await _invoiceStreamCancel.CancelAsync();
        }
        await _client.StopAsync(new Empty(), CreateCallOptions(cancellationToken));
    }

    public async Task<BakeMacaroonResponse> BakeMacaroon(ulong tenantId)
    {
        return await _client.BakeMacaroonAsync(new BakeMacaroonRequest
        {
            TenantId = tenantId,
            Permissions =
            {
                new MacaroonPermissions
                {
                    Action = MacaroonAction.Read,
                },
                new MacaroonPermissions
                {
                    Action = MacaroonAction.Write,
                },
            },
        }, _callOptions);
    }

    private EventHandler<GetSwapInfoResponse>? _swapUpdate;
    private CancellationTokenSource? _invoiceStreamCancel;

    public event EventHandler<GetSwapInfoResponse> SwapUpdate
    {
        add
        {
            if (_invoiceStreamCancel is null)
            {
                Task.Run(InvoiceStream);
            }

            _swapUpdate += value;
        }
        remove
        {
            _swapUpdate -= value;
            if (_swapUpdate is null)
            {
                _invoiceStreamCancel?.Cancel();
                _invoiceStreamCancel = null;
            }
        }
    }

    private async Task InvoiceStream()
    {
        _invoiceStreamCancel = new CancellationTokenSource();
        while (!_invoiceStreamCancel.IsCancellationRequested)
        {
            try
            {
                using var stream = _client.GetSwapInfoStream(new GetSwapInfoRequest(), CreateCallOptions(_invoiceStreamCancel.Token));
                while (await stream.ResponseStream.MoveNext(_invoiceStreamCancel.Token))
                {
                    _swapUpdate?.Invoke(this, stream.ResponseStream.Current);
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e) when (!_invoiceStreamCancel.IsCancellationRequested)
            {
                _logger.LogError(e, "Error in swap stream");
                await Task.Delay(3000);
            }
        }
    }

    public void Dispose()
    {
        foreach (var channel in Channels.Values)
        {
            channel.Dispose();
        }

        Channels.Clear();
    }

    public static void Clear()
    {
        foreach (var channel in Channels.Values)
        {
            channel.Dispose();
        }

        Channels.Clear();
    }

    public static List<Stat> ParseStats(SwapStats stats)
    {
        return
        [
            new() { Name = "Fees", Value = stats.TotalFees, Unit = Unit.Sat },
            new() { Name = "Swap Volume", Value = stats.TotalAmount, Unit = Unit.Sat },
            new() { Name = "Successful Swap Count", Value = stats.SuccessCount, Unit = Unit.None }
        ];
    }

    public static string CurrencyName(Currency currency)
    {
        return currency == Currency.Btc ? "BTC" : "L-BTC";
    }
}