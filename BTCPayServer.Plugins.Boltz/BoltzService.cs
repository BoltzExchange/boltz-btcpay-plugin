#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Client.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using LightningChannel = BTCPayServer.Lightning.LightningChannel;
using Network = NBitcoin.Network;
using UpdateChainConfigRequest = Autoswaprpc.UpdateChainConfigRequest;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzClient : IDisposable
{
    private readonly Metadata _metadata;
    private Boltzrpc.Boltz.BoltzClient client;
    private AutoSwap.AutoSwapClient autoClient;
    private readonly GrpcChannel _channel;
    private readonly Task _invoiceStreamTask;

    public BoltzClient(Uri grpcEndpoint, string macaroon)
    {
        var opt = new GrpcChannelOptions()
        {
            Credentials = ChannelCredentials.Insecure,
        };

        _channel = GrpcChannel.ForAddress(grpcEndpoint, opt);

        client = new(_channel);
        autoClient = new(_channel);


        _metadata = new Metadata()
        {
            { "macaroon", macaroon },
        };

        _invoiceStreamTask = InvoiceStream();
    }

    public async Task<GetInfoResponse> GetInfo()
    {
        return await client.GetInfoAsync(new GetInfoRequest(), _metadata);
    }

    public async Task<ListSwapsResponse> ListSwaps()
    {
        return await client.ListSwapsAsync(new ListSwapsRequest(), _metadata);
    }

    public async Task<Wallets> GetWallets()
    {
        return await client.GetWalletsAsync(new GetWalletsRequest{IncludeReadonly = true}, _metadata);
    }

    public async Task<Wallet> GetWallet(string name)
    {
        return await client.GetWalletAsync(new GetWalletRequest { Name = name }, _metadata);
    }


    public async Task<CreateWalletResponse> CreateWallet(WalletParams walletParams)
    {
        return await client.CreateWalletAsync(new CreateWalletRequest {Params = walletParams}, _metadata);
    }

    public async Task<Wallet> GetAutoSwapWallet()
    {
        // TODO: dont fetch config everytime
        var config = await autoClient.GetConfigAsync(new GetConfigRequest(), _metadata);
        return await GetWallet(config.Lightning[0].Wallet);
    }

    public AutoSwap.AutoSwapClient GetAutoSwapClient()
    {
        return autoClient;
    }

    public async Task<BoltzConfig> GetAutoSwapConfig()
    {
        var config = await autoClient.GetConfigAsync(new GetConfigRequest(), _metadata);

        var data = new BoltzConfig();
        if (config.Lightning.Count > 0)
        {
            data.Ln = config.Lightning[0];
        }

        if (config.Chain.Count > 0)
        {
            data.Chain = config.Chain[0];
        }

        return data;
    }

    public async Task<GetStatusResponse> GetAutoSwapStatus()
    {
        return await autoClient.GetStatusAsync(new GetStatusRequest(), _metadata);
    }

    public async Task UpdateAutoSwapConfig(BoltzConfig data)
    {
        if (data.Chain != null)
        {
            await autoClient.UpdateChainConfigAsync(new UpdateChainConfigRequest()
            {
                Config = data.Chain
            });
        }
        if (data.Ln != null)
        {
            await autoClient.UpdateLightningConfigAsync(new UpdateLightningConfigRequest
            {
                Config = data.Ln
            });
        }
    }

    public async Task UpdateAutoSwapChainConfig(AutoSwapChainConfig data)
    {
        await autoClient.UpdateChainConfigAsync(new UpdateChainConfigRequest
        {
            Config = new ChainConfig
            {
                Enabled = data.Enabled
            },
            FieldMask = new FieldMask
            {
                Paths = { "enabled" }
            }
        });
    }

    public Boltzrpc.Boltz.BoltzClient GetClient()
    {
        return client;
    }

    public async Task<CreateReverseSwapResponse> CreateReverseSwap(CreateReverseSwapRequest request)
    {
        return await client.CreateReverseSwapAsync(request, _metadata);
    }

    public async Task<CreateSwapResponse> CreateSwap(CreateSwapRequest request)
    {
        return await client.CreateSwapAsync(request, _metadata);
    }

    public event EventHandler<GetSwapInfoResponse> SwapUpdate;

    private async Task InvoiceStream()
    {
        using (var stream = client.GetSwapInfoStream(new GetSwapInfoRequest()))
        {
            while (await stream.ResponseStream.MoveNext())
            {
                SwapUpdate.Invoke(this, stream.ResponseStream.Current);
            }
        }
    }

    public void Dispose()
    {
        _channel.Dispose();
        _invoiceStreamTask.Dispose();
    }
}

public class BoltzService : EventHostedServiceBase
{
    private Dictionary<string, BoltzSettings> _settings;
    private Dictionary<string, BoltzClient> _clients = new();

    private readonly BTCPayWalletProvider _btcPayWalletProvider;
    private readonly StoreRepository _storeRepository;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly ILogger _logger;
    private BTCPayNetworkProvider _btcPayNetworkProvider;

    public BoltzService(
        BTCPayWalletProvider btcPayWalletProvider,
        StoreRepository storeRepository,
        IOptions<DataDirectories> dataDirectories,
        EventAggregator eventAggregator,
        ILogger<BoltzService> logger,
        BTCPayNetworkProvider btcPayNetworkProvider
    ) : base(eventAggregator, logger)
    {
        _btcPayWalletProvider = btcPayWalletProvider;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _storeRepository = storeRepository;
        _logger = logger;
        _dataDirectories = dataDirectories;
    }

    TaskCompletionSource tcs = new();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _settings = (await _storeRepository.GetSettingsAsync<BoltzSettings>("Boltz"))
            .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!);
        foreach (var keyValuePair in _settings)
        {
            try
            {
                await Handle(keyValuePair.Key, keyValuePair.Value);
            }
            catch (Exception e)
            {
            }
        }

        tcs.TrySetResult();
        await base.StartAsync(cancellationToken);
    }

    public async Task<BoltzClient?> Handle(string? storeId, BoltzSettings? settings)
    {
        if (settings is null)
        {
            if (storeId is not null && _clients.Remove(storeId, out var client))
            {
                client.Dispose();
            }
        }
        else
        {
            try
            {
                //var dir = GetWorkDir(storeId);
                //Directory.CreateDirectory(dir);
                var client = new BoltzClient(settings.GrpcUrl, settings.Macaroon);
                _clients.AddOrReplace(storeId, client);
                return client;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not create boltz client");
                throw;
            }
        }

        return null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _clients.Values.ToList().ForEach(c => c.Dispose());
    }

    public string GetWorkDir(string storeId)
    {
        var dir = _dataDirectories.Value.DataDir;
        return Path.Combine(dir, "Plugins", "Boltz", storeId);
    }

    public BoltzClient? GetClient(string? storeId)
    {
        if (storeId is null)
        {
            return null;
        }

        _clients.TryGetValue(storeId, out var client);
        return client;
    }

    public BoltzSettings? GetSettings(string? storeId)
    {
        if (storeId is null)
        {
            return null;
        }

        _settings.TryGetValue(storeId, out var settings);
        return settings;
    }


    public async Task Set(string storeId, BoltzSettings? settings)
    {
        var result = await Handle(storeId, settings);
        await _storeRepository.UpdateSetting(storeId, "Boltz", settings!);
        if (settings is null)
        {
            _settings.Remove(storeId, out var oldSettings);
            /*
            var data = await _storeRepository.FindStore(storeId);
            var existing = data?.GetSupportedPaymentMethods(_btcPayNetworkProvider)
                .OfType<LightningSupportedPaymentMethod>().FirstOrDefault(method =>
                    method.CryptoCode == "BTC" && method.PaymentId.PaymentType == LightningPaymentType.Instance);
            var isBreez = existing?.GetExternalLightningUrl() == $"type=breez;key={oldSettings.PaymentKey}";
            if (isBreez)
            {
                data.SetSupportedPaymentMethod(new PaymentMethodId("BTC", LightningPaymentType.Instance), null);
                await _storeRepository.UpdateStore(data);
            }

            Directory.Delete(GetWorkDir(storeId), true);
            */
        }
        else if (result is not null)
        {
            _settings.AddOrReplace(storeId, settings);
        }
    }
}