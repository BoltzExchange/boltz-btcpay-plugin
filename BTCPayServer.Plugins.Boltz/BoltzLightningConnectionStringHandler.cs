#nullable  enable
using BTCPayServer.Lightning;
using System;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;
    
namespace BTCPayServer.Plugins.Boltz;

public class BoltzLightningConnectionStringHandler : ILightningConnectionStringHandler
{
      private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public BoltzLightningConnectionStringHandler(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }


    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "boltz")
        {
            error = null;
            return null;
        }
        
        if (!kv.TryGetValue("server", out var server))
        {
            error = $"The key 'server' is mandatory for boltz connection strings";
            return null;
        }

        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)
            || uri.Scheme != "http" && uri.Scheme != "https")
        {
            error = "The key 'server' should be an URI starting by http:// or https://";
            return null;
        }

        bool allowInsecure = false;
        if (kv.TryGetValue("allowinsecure", out var allowinsecureStr))
        {
            var allowedValues = new[] {"true", "false"};
            if (!allowedValues.Any(v => v.Equals(allowinsecureStr, StringComparison.OrdinalIgnoreCase)))
            {
                error = "The key 'allowinsecure' should be true or false";
                return null;
            }

            allowInsecure = allowinsecureStr.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        if (!LightningConnectionStringHelper.VerifySecureEndpoint(uri, allowInsecure))
        {
            error = "The key 'allowinsecure' is false, but server's Uri is not using https";
            return null;
        }

        if (!kv.TryGetValue("macaroon", out var macaroon))
        {
            error = "The key 'api-key' is not found";
            return null;
        }

        error = null;

        kv.TryGetValue("wallet-id", out var walletId);
        var bclient = new BoltzLightningClient(macaroon, uri, _loggerFactory.CreateLogger(nameof(BoltzLightningClient)));
        (Network Network, string DefaultWalletId, string DefaultWalletCurrency) res;
        try
        {
            
            //var info = bclient.GetInfo().GetAwaiter().GetResult();
            //if (res.Network != network)
            //{
            //    error = $"The wallet is not on the right network ({res.Network.Name} instead of {network.Name})";
            //    return null;
            //}

        }
        catch (Exception e)
        {
            error = $"Invalid server or api key";
            return null;
        }

        return bclient;
    }
}