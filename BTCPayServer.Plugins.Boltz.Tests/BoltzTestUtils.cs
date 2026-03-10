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
        private static async Task<bool> WaitForAdminClient(BoltzService boltzService, int timeoutSeconds)
        {
            for (var second = 0; second < timeoutSeconds; second++)
            {
                if (boltzService.AdminClient is not null && boltzService.Daemon.Running)
                {
                    return true;
                }

                await Task.Delay(1000);
            }

            return boltzService.AdminClient is not null && boltzService.Daemon.Running;
        }

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
                AllowTenants = true
            };
        }

        public static async Task<TestAccount> CreateTestStore(this ServerTester serverTester, string storeName = "Test Store")
        {
            await serverTester.StartAsync();
            await Task.Delay(3000);
            var account = serverTester.NewAccount();
            account.GrantAccess();
            await account.CreateStoreAsync();
            return account;
        }

        public static async Task SetupBoltzForStore(this ServerTester serverTester, string storeId)
        {
            var boltzService = await serverTester.GetBoltzService();
            await boltzService.SetServerSettings(CreateTestBoltzServerSettings());
            if (!await WaitForAdminClient(boltzService, timeoutSeconds: 45))
            {
                await boltzService.SetServerSettings(CreateTestBoltzServerSettings());
            }

            BoltzSettings settings = null;
            BoltzClient client = null;
            const int maxAttempts = 30;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (boltzService.AdminClient is null)
                {
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(2000);
                        continue;
                    }
                }

                try
                {
                    settings = await boltzService.InitializeStore(storeId);
                    client = boltzService.Daemon.GetClient(settings);
                    if (client is not null)
                    {
                        break;
                    }
                }
                catch (NullReferenceException) when (attempt < maxAttempts)
                {
                    // Boltz daemon/admin client can still be initializing right after test server startup.
                }
                catch (InvalidOperationException) when (attempt < maxAttempts)
                {
                    // Boltz daemon/admin client can still be initializing right after test server startup.
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(2000);
                }
            }

            Assert.NotNull(settings);
            Assert.NotNull(client);
            var wallet = await client.CreateWallet(new Boltzrpc.WalletParams { Name = "test", Currency = Boltzrpc.Currency.Lbtc });
            settings.SetStandaloneWallet(wallet.Wallet);
            await boltzService.Set(storeId, settings);
            Console.WriteLine(settings.ToString());
            Assert.True(boltzService.StoreConfigured(storeId));
        }

        public static void AssertBoltzSettingsEqual(BoltzSettings expected, BoltzSettings actual)
        {
            Assert.Equal(expected.GrpcUrl, actual.GrpcUrl);
            Assert.Equal(expected.CertFilePath, actual.CertFilePath);
            Assert.Equal(expected.Macaroon, actual.Macaroon);
            Assert.Equal(expected.TenantId, actual.TenantId);
        }

        public static void AssertBoltzServerSettingsEqual(BoltzServerSettings expected, BoltzServerSettings actual)
        {
            Assert.Equal(expected.AllowTenants, actual.AllowTenants);
        }
    }
}
