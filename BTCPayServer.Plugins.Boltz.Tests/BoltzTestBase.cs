using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Bitcoin;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins;
using BTCPayServer.Tests;
using BTCPayServer.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;
using NBXplorer;
using Xunit.Abstractions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    public class BoltzTestBase : UnitTestBase
    {
        public BoltzTestBase(ITestOutputHelper helper) : base(helper)
        {
        }

        public BTCPayNetworkProvider CreateNetworkProviderWithBoltz(ChainName chainName)
        {
            var conf = new ConfigurationRoot(new List<IConfigurationProvider>()
            {
                new MemoryConfigurationProvider(new MemoryConfigurationSource()
                {
                    InitialData = new[] {
                        new KeyValuePair<string, string>("chains", "*"),
                        new KeyValuePair<string, string>("network", chainName.ToString())
                    }
                })
            });
            return CreateNetworkProviderWithBoltz(conf);
        }

        public BTCPayNetworkProvider CreateNetworkProviderWithBoltz(IConfiguration conf = null)
        {
            conf ??= new ConfigurationRoot(new List<IConfigurationProvider>()
            {
                new MemoryConfigurationProvider(new MemoryConfigurationSource()
                {
                    InitialData = new[] {
                        new KeyValuePair<string, string>("chains", "*"),
                        new KeyValuePair<string, string>("network", "regtest")
                    }
                })
            });
            var bootstrap = Startup.CreateBootstrap(conf);
            var services = new PluginServiceCollection(new ServiceCollection(), bootstrap);
            var plugins = new List<BaseBTCPayServerPlugin>() { new BitcoinPlugin() };

            foreach (var p in plugins)
            {
                p.Execute(services);
            }
            services.AddSingleton(services.BootstrapServices.GetRequiredService<SelectedChains>());
            services.AddSingleton(services.BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>());
            services.AddSingleton(services.BootstrapServices.GetRequiredService<Logs>());
            services.AddSingleton(services.BootstrapServices.GetRequiredService<IConfiguration>());
            services.AddSingleton<BTCPayNetworkProvider>();

            var boltzPlugin = new BoltzPlugin();
            boltzPlugin.Execute(services);

            var serviceProvider = services.BuildServiceProvider();
            return serviceProvider.GetService<BTCPayNetworkProvider>();

        }

        public ServerTester CreateServerTesterWithBoltz([CallerMemberNameAttribute] string scope = null, bool newDb = false)
        {
            var provider = CreateNetworkProviderWithBoltz();
            Assert.NotNull(provider);
            Assert.NotNull(provider.GetNetwork<BTCPayNetwork>("BTC"));
            return new ServerTester(scope, newDb, TestLogs, TestLogProvider, provider);
        }

        public SeleniumTester CreateSeleniumTesterWithBoltz([CallerMemberNameAttribute] string scope = null, bool newDb = false)
        {
            return new SeleniumTester() { Server = new ServerTester(scope, newDb, TestLogs, TestLogProvider, CreateNetworkProviderWithBoltz()) };
        }

        public PlaywrightTester CreatePlaywrightTesterWithBoltz([CallerMemberNameAttribute] string scope = null, bool newDb = false)
        {
            return new PlaywrightTester() { Server = new ServerTester(scope, newDb, TestLogs, TestLogProvider, CreateNetworkProviderWithBoltz()) };
        }
    }
}
