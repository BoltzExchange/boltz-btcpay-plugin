using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Shopify.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;

public static class BoltzExtensions
{
    public const string StoreBlobKey = "boltz";

    public static BoltzSettings GetBoltzSettings(this StoreBlob storeBlob)
    {
        if (storeBlob.AdditionalData.TryGetValue(StoreBlobKey, out var rawS))
        {
            if (rawS is JObject rawObj)
            {
                return new Serializer(null).ToObject<BoltzSettings>(rawObj);
            }
            else if (rawS.Type == JTokenType.String)
            {
                return new Serializer(null).ToObject<BoltzSettings>(rawS.Value<string>());
            }
        }

        return null;
    }

    public static void SetBoltzSettings(this StoreBlob storeBlob, BoltzSettings settings)
    {
        if (settings is null)
        {
            storeBlob.AdditionalData.Remove(StoreBlobKey);
        }
        else
        {
            storeBlob.AdditionalData.AddOrReplace(StoreBlobKey, new Serializer(null).ToString(settings));
        }
    }
}