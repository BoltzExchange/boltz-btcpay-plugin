using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzSwaggerProvider : ISwaggerProvider
{
    private const string ResourceName = "BTCPayServer.Plugins.Boltz.Resources.swagger.boltz.json";

    public async Task<JObject> Fetch()
    {
        var assembly = typeof(BoltzSwaggerProvider).Assembly;
        
        await using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            return new JObject();
        }
        
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync();
        return JObject.Parse(json);
    }
}
