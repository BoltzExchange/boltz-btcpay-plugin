using System;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    public static class BoltzTestUtils
    {
        public static async Task<BoltzService> GetBoltzService(this ServerTester serverTester)
        {
            await serverTester.StartAsync();
            return serverTester.PayTester.GetService<BoltzService>();
        }

        public static async Task<BoltzDaemon> GetBoltzDaemon(this ServerTester serverTester)
        {
            await serverTester.StartAsync();
            return serverTester.PayTester.GetService<BoltzDaemon>();
        }

        public static BoltzSettings CreateTestBoltzSettings(BoltzMode mode = BoltzMode.Standalone)
        {
            return new BoltzSettings
            {
                Mode = mode,
                GrpcUrl = new Uri("http://localhost:9001"),
                CertFilePath = "/tmp/test-cert.pem",
                Macaroon = "test-macaroon",
                TenantId = 1
            };
        }

        public static BoltzServerSettings CreateTestBoltzServerSettings()
        {
            return new BoltzServerSettings
            {
                ConnectNode = false,
                AllowTenants = true
            };
        }

        public static async Task<string> CreateTestStore(this ServerTester serverTester, string storeName = "Test Store")
        {
            await serverTester.StartAsync();
            var account = serverTester.NewAccount();
            account.GrantAccess();
            await account.CreateStoreAsync();
            return account.StoreId;
        }

        public static async Task SetupBoltzForStore(this ServerTester serverTester, string storeId, BoltzMode mode = BoltzMode.Standalone)
        {
            var boltzService = await serverTester.GetBoltzService();
            var settings = CreateTestBoltzSettings(mode);
            await boltzService.Set(storeId, settings);
        }

        public static void AssertBoltzSettingsEqual(BoltzSettings expected, BoltzSettings actual)
        {
            Assert.Equal(expected.Mode, actual.Mode);
            Assert.Equal(expected.GrpcUrl, actual.GrpcUrl);
            Assert.Equal(expected.CertFilePath, actual.CertFilePath);
            Assert.Equal(expected.Macaroon, actual.Macaroon);
            Assert.Equal(expected.TenantId, actual.TenantId);
        }

        public static void AssertBoltzServerSettingsEqual(BoltzServerSettings expected, BoltzServerSettings actual)
        {
            Assert.Equal(expected.ConnectNode, actual.ConnectNode);
            Assert.Equal(expected.AllowTenants, actual.AllowTenants);
        }
    }
}
