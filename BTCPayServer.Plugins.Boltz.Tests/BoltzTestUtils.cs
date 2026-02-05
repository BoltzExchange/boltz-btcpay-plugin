using System;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    public static class BoltzTestUtils
    {
        public static async Task<BoltzService> GetBoltzService(this ServerTester serverTester)
        {
            return serverTester.PayTester.GetService<BoltzService>();
        }

        public static async Task<BoltzDaemon> GetBoltzDaemon(this ServerTester serverTester)
        {
            return serverTester.PayTester.GetService<BoltzDaemon>();
        }

        public static BoltzServerSettings CreateTestBoltzServerSettings()
        {
            return new BoltzServerSettings
            {
                ConnectNode = false,
                AllowTenants = true
            };
        }

        public static async Task<TestAccount> CreateTestStore(this ServerTester serverTester, string storeName = "Test Store")
        {
            await serverTester.StartAsync();
            var account = serverTester.NewAccount();
            account.GrantAccess();
            await account.CreateStoreAsync();
            return account;
        }

        public static async Task SetupBoltzForStore(this ServerTester serverTester, string storeId, BoltzMode mode = BoltzMode.Standalone)
        {
            var boltzService = await serverTester.GetBoltzService();
            var settings = await boltzService.InitializeStore(storeId, mode);
            var client = boltzService.Daemon.GetClient(settings);
            var wallet = await client.CreateWallet(new Boltzrpc.WalletParams { Name = "test", Currency = Boltzrpc.Currency.Lbtc });
            settings.SetStandaloneWallet(wallet.Wallet);
            await boltzService.Set(storeId, settings);
            Console.WriteLine(settings.ToString());
            Assert.True(boltzService.StoreConfigured(storeId));
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
