#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Plugins.Boltz.Models;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using GetInfoResponse = Boltzrpc.GetInfoResponse;
using ImportWalletRequest = Boltzrpc.ImportWalletRequest;
using UpdateChainConfigRequest = Autoswaprpc.UpdateChainConfigRequest;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzClient : IDisposable
{
    private readonly Metadata _metadata;
    private readonly Boltzrpc.Boltz.BoltzClient _client;
    private readonly AutoSwap.AutoSwapClient _autoClient;
    private readonly GrpcChannel _channel;

    private static readonly Dictionary<Uri, GrpcChannel> Channels = new();

    public BoltzClient(Uri grpcEndpoint, string? macaroon = null, ulong? tenantId = null)
    {
        var opt = new GrpcChannelOptions
        {
            Credentials = ChannelCredentials.Insecure
        };

        if (!Channels.TryGetValue(grpcEndpoint, out _channel!))
        {
            _channel = GrpcChannel.ForAddress(grpcEndpoint, opt);
            Channels.Add(grpcEndpoint, _channel);
        }

        _client = new(_channel);
        _autoClient = new(_channel);

        _metadata = new Metadata();
        if (macaroon is not null)
        {
            _metadata.Add("macaroon", macaroon);
        }

        if (tenantId is not null)
        {
            _metadata.Add("tenant", tenantId.Value.ToString());
        }
    }

    public async Task<GetInfoResponse> GetInfo()
    {
        return await _client.GetInfoAsync(new GetInfoRequest(), _metadata);
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

    public async Task<Wallets> GetWallets(bool includeReadonly)
    {
        return await _client.GetWalletsAsync(new GetWalletsRequest { IncludeReadonly = includeReadonly }, _metadata);
    }

    public async Task<Wallet> GetWallet(string name)
    {
        return await _client.GetWalletAsync(new GetWalletRequest { Name = name }, _metadata);
    }

    public async Task<Wallet> GetWallet(ulong id)
    {
        return await _client.GetWalletAsync(new GetWalletRequest { Id = id }, _metadata);
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

    public async Task<GetRecommendationsResponse> GetAutoSwapRecommendations()
    {
        return await _autoClient.GetRecommendationsAsync(new GetRecommendationsRequest(), _metadata);
    }

    public async Task<PairInfo> GetPairInfo(Pair pair, SwapType swapType)
    {
        return await _client.GetPairInfoAsync(new GetPairInfoRequest
        {
            Pair = pair,
            Type = swapType
        }, _metadata);
    }

    public async Task<GetStatsResponse> GetStats()
    {
        return await _client.GetStatsAsync(new GetStatsRequest(), _metadata);
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

    public async Task<CreateSwapResponse> CreateSwap(CreateSwapRequest request)
    {
        return await _client.CreateSwapAsync(request, _metadata);
    }

    public async Task<Tenant> CreateTenant(string name)
    {
        return await _client.CreateTenantAsync(new CreateTenantRequest { Name = name }, _metadata);
    }

    public async Task<Tenant> GetTenant(string name)
    {
        return await _client.GetTenantAsync(new GetTenantRequest { Name = name }, _metadata);
    }

    public async Task Stop()
    {
        await _client.StopAsync(new Empty(), _metadata);
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
        using var stream = _client.GetSwapInfoStream(new GetSwapInfoRequest(), _metadata,
            cancellationToken: _invoiceStreamCancel.Token);
        while (await stream.ResponseStream.MoveNext())
        {
            _swapUpdate?.Invoke(this, stream.ResponseStream.Current);
        }
    }

    public void Dispose()
    {
        foreach (var channel in Channels.Values)
        {
            channel.Dispose();
        }
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

    public static string  CurrencyName(Currency currency)
    {
        return currency == Currency.Btc ? "BTC" : "L-BTC";
    }
}
