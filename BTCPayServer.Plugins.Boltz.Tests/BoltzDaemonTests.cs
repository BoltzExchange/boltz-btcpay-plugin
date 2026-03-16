#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Boltz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    public class BoltzDaemonTests : BoltzTestBase
    {
        public BoltzDaemonTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact]
        public void UsesSharedBinaryCacheDuringTestRuns()
        {
            var dataDir = Path.Combine(Path.GetTempPath(), $"boltz-daemon-tests-{Guid.NewGuid():N}");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TEST_RUNNER_ENABLED"] = "true"
                })
                .Build();

            var daemon = CreateDaemon(dataDir, configuration);
            var clientFilesDir = GetNonPublicStringProperty(daemon, "ClientFilesDir");

            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "btcpayserver-boltz-client-cache"), clientFilesDir);
            Assert.DoesNotContain(Path.Combine(dataDir, "Plugins", "Boltz"), clientFilesDir, StringComparison.Ordinal);
        }

        [Fact]
        public void UsesConfiguredBinaryCacheOverride()
        {
            var dataDir = Path.Combine(Path.GetTempPath(), $"boltz-daemon-tests-{Guid.NewGuid():N}");
            var configuredCacheDir = Path.Combine(Path.GetTempPath(), $"boltz-daemon-cache-{Guid.NewGuid():N}");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TEST_RUNNER_ENABLED"] = "true",
                    ["BOLTZ_CLIENT_CACHE_DIR"] = configuredCacheDir
                })
                .Build();

            var daemon = CreateDaemon(dataDir, configuration);
            var clientFilesDir = GetNonPublicStringProperty(daemon, "ClientFilesDir");

            Assert.StartsWith(Path.GetFullPath(configuredCacheDir), clientFilesDir);
            Assert.DoesNotContain(Path.Combine(dataDir, "Plugins", "Boltz"), clientFilesDir, StringComparison.Ordinal);
        }

        private BoltzDaemon CreateDaemon(string dataDir, IConfiguration configuration)
        {
            return new BoltzDaemon(
                Options.Create(new DataDirectories { DataDir = dataDir }),
                NullLogger<BoltzDaemon>.Instance,
                NullLogger<BoltzClient>.Instance,
                CreateNetworkProviderWithBoltz(),
                configuration);
        }

        private static string GetNonPublicStringProperty(object instance, string propertyName)
        {
            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(property);
            return Assert.IsType<string>(property!.GetValue(instance));
        }
    }
}
