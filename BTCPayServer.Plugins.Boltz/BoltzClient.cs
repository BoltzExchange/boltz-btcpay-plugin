#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Plugins.Boltz.Models;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using NLog;
using GetInfoResponse = Boltzrpc.GetInfoResponse;
using ImportWalletRequest = Boltzrpc.ImportWalletRequest;
using UpdateChainConfigRequest = Autoswaprpc.UpdateChainConfigRequest;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzClient : IDisposable
{
    private readonly Metadata _metadata;
    private readonly Boltzrpc.Boltz.BoltzClient _client;
    private readonly AutoSwap.AutoSwapClient _autoClient;

    private static readonly Dictionary<Uri, GrpcChannel> Channels = new();
    private static GetPairsResponse? _pairs;
    private readonly ILogger<BoltzClient> _logger;

    public BoltzClient(ILogger<BoltzClient> logger, Uri grpcEndpoint, string? macaroon = null, string? tenant = null)
    {
        var opt = new GrpcChannelOptions
        {
            Credentials = ChannelCredentials.Insecure,
            HttpHandler = new SocketsHttpHandler()
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan
            }
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
        if (macaroon is not null)
        {
            _metadata.Add("macaroon", macaroon);
        }

        if (tenant is not null)
        {
            _metadata.Add("tenant", tenant);
        }
    }

    public async Task<GetInfoResponse> GetInfo(CancellationToken cancellationToken = default)
    {
        return await _client.GetInfoAsync(new GetInfoRequest(), _metadata, cancellationToken: cancellationToken);
    }

    public async Task<ListSwapsResponse> ListSwaps()
    {
        return await ListSwaps(new ListSwapsRequest());
    }

    public async Task<ListSwapsResponse> ListSwaps(ListSwapsRequest request)
    {
        return await _client.ListSwapsAsync(request, _metadata);
    }


    public async Task<GetSwapInfoResponse> GetSwapInfo(string id)
    {
        return await _client.GetSwapInfoAsync(new GetSwapInfoRequest { Id = id }, _metadata);
    }

    public AsyncServerStreamingCall<GetSwapInfoResponse> GetSwapInfoStream(string id, CancellationToken cancellationToken = default)
    {
        return _client.GetSwapInfoStream(new GetSwapInfoRequest { Id = id }, _metadata, cancellationToken: cancellationToken);
    }

    public async Task<Wallets> GetWallets(bool includeReadonly)
    {
        return await _client.GetWalletsAsync(new GetWalletsRequest { IncludeReadonly = includeReadonly }, _metadata);
    }

    public async Task<Wallet> GetWallet(string name)
    {
        return await _client.GetWalletAsync(new GetWalletRequest { Name = name }, _metadata);
    }

    public async Task<GetSubaccountsResponse> GetSubaccounts(ulong walletId)
    {
        return await _client.GetSubaccountsAsync(new GetSubaccountsRequest() { WalletId = walletId }, _metadata);
    }

    public async Task SetSubaccount(ulong walletId, ulong? subaccount)
    {
        var request = new SetSubaccountRequest { WalletId = walletId };
        if (subaccount.HasValue)
        {
            request.Subaccount = subaccount.Value;
        }

        await _client.SetSubaccountAsync(request, _metadata);
    }

    public async Task<Wallet> GetWallet(ulong id)
    {
        return await _client.GetWalletAsync(new GetWalletRequest { Id = id }, _metadata);
    }

    public async Task<WalletCredentials> GetWalletCredentials(ulong id)
    {
        return await _client.GetWalletCredentialsAsync(new GetWalletCredentialsRequest { Id = id }, _metadata);
    }

    public async Task<CreateWalletResponse> CreateWallet(WalletParams @params)
    {
        return await _client.CreateWalletAsync(new CreateWalletRequest { Params = @params }, _metadata);
    }

    public async Task<Wallet> ImportWallet(WalletParams @params, WalletCredentials credentials)
    {
        return await _client.ImportWalletAsync(new ImportWalletRequest { Params = @params, Credentials = credentials },
            _metadata);
    }

    public async Task<WalletSendResponse> WalletSend(WalletSendRequest request)
    {
        return await _client.WalletSendAsync(request, _metadata);
    }

    public async Task<WalletReceiveResponse> WalletReceive(ulong id)
    {
        return await _client.WalletReceiveAsync(new WalletReceiveRequest { Id = id }, _metadata);
    }

    public async Task<GetRecommendationsResponse> GetAutoSwapRecommendations()
    {
        return await _autoClient.GetRecommendationsAsync(new GetRecommendationsRequest(), _metadata);
    }

    public async Task<PairInfo> GetPairInfo(Pair pair, SwapType swapType)
    {
        _pairs ??= await GetPairs();
        var search = swapType switch
        {
            SwapType.Reverse => _pairs.Reverse,
            SwapType.Submarine => _pairs.Submarine,
            SwapType.Chain => _pairs.Chain,
            _ => throw new ArgumentOutOfRangeException(nameof(swapType), swapType, null)
        };
        return search.ToList().Find(p => p.Pair.From == pair.From && p.Pair.To == pair.To)!;
    }

    public async Task<GetPairsResponse> GetPairs()
    {
        _pairs = await _client.GetPairsAsync(new Empty(), _metadata);
        return _pairs;
    }

    public async Task<SwapStats> GetStats()
    {
        return (await _client.GetStatsAsync(new GetStatsRequest { Include = IncludeSwaps.Manual }, _metadata)).Stats;
    }

    public async Task ResetLnConfig()
    {
        await _autoClient.UpdateLightningConfigAsync(new UpdateLightningConfigRequest { Reset = true }, _metadata);
    }

    public async Task ResetChainConfig()
    {
        await _autoClient.UpdateChainConfigAsync(new UpdateChainConfigRequest { Reset = true }, _metadata);
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
        var config = await _autoClient.GetConfigAsync(new GetConfigRequest(), _metadata);
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
        return await _autoClient.GetStatusAsync(new GetStatusRequest(), _metadata);
    }

    public async Task UpdateAutoSwapConfig(BoltzConfig data)
    {
        if (data.Chain != null)
        {
            await _autoClient.UpdateChainConfigAsync(new UpdateChainConfigRequest()
            {
                Config = data.Chain
            }, _metadata);
        }

        if (data.Ln != null)
        {
            await _autoClient.UpdateLightningConfigAsync(new UpdateLightningConfigRequest
            {
                Config = data.Ln
            }, _metadata);
        }
    }

    public async Task<LightningConfig> UpdateAutoSwapLightningConfig(LightningConfig config, IEnumerable<string>? paths)
    {
        var request = new UpdateLightningConfigRequest { Config = config };
        if (paths is not null)
        {
            request.FieldMask = FieldMask.FromStringEnumerable<LightningConfig>(paths);
        }

        var result = await _autoClient.UpdateLightningConfigAsync(request, _metadata);
        return result.Lightning[0];
    }

    public async Task<ChainConfig> UpdateAutoSwapChainConfig(ChainConfig config, IEnumerable<string>? paths = null)
    {
        var request = new UpdateChainConfigRequest { Config = config };
        if (paths is not null)
        {
            request.FieldMask = FieldMask.FromStringEnumerable<ChainConfig>(paths);
        }

        var result = await _autoClient.UpdateChainConfigAsync(request, _metadata);
        return result.Chain[0];
    }

    public Boltzrpc.Boltz.BoltzClient GetClient()
    {
        return _client;
    }

    public async Task<CreateReverseSwapResponse> CreateReverseSwap(CreateReverseSwapRequest request,
        CancellationToken cancellation = new CancellationToken())
    {
        return await _client.CreateReverseSwapAsync(request, headers: _metadata, cancellationToken: cancellation);
    }

    public async Task<ChainSwapInfo> CreateChainSwap(CreateChainSwapRequest request,
        CancellationToken cancellation = default)
    {
        return await _client.CreateChainSwapAsync(request, headers: _metadata, cancellationToken: cancellation);
    }

    public async Task<CreateSwapResponse> CreateSwap(CreateSwapRequest request,
        CancellationToken cancellation = default)
    {
        return await _client.CreateSwapAsync(request, headers: _metadata, cancellationToken: cancellation);
    }

    public async Task<GetSwapInfoResponse> RefundSwap(RefundSwapRequest request,
        CancellationToken cancellation = default)
    {
        return await _client.RefundSwapAsync(request, headers: _metadata, cancellationToken: cancellation);
    }

    public async Task<Tenant> CreateTenant(string name)
    {
        return await _client.CreateTenantAsync(new CreateTenantRequest { Name = name }, _metadata);
    }

    public async Task<Tenant> GetTenant(string name)
    {
        return await _client.GetTenantAsync(new GetTenantRequest { Name = name }, _metadata);
    }

    public async Task Stop(CancellationToken cancellationToken = default)
    {
        await _client.StopAsync(new Empty(), _metadata, cancellationToken: cancellationToken);
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
        }, _metadata);
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
                using var stream = _client.GetSwapInfoStream(new GetSwapInfoRequest(), _metadata,
                    cancellationToken: _invoiceStreamCancel.Token);
                while (await stream.ResponseStream.MoveNext(_invoiceStreamCancel.Token))
                {
                    _swapUpdate?.Invoke(this, stream.ResponseStream.Current);
                }
            }
            catch (Exception e)
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

    public static List<Stat> ParseStats(SwapStats stats)
    {
        return new List<Stat>
        {
            new() { Name = "Fees", Value = stats.TotalFees, Unit = Unit.Sat },
            new() { Name = "Swap Volume", Value = stats.TotalAmount, Unit = Unit.Sat },
            new() { Name = "Successful Swap Count", Value = stats.SuccessCount, Unit = Unit.None }
        };
    }

    public static string CurrencyName(Currency currency)
    {
        return currency == Currency.Btc ? "BTC" : "L-BTC";
    }
}