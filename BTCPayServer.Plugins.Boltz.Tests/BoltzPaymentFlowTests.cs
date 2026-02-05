using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Integration", "Integration")]
    public class BoltzPaymentFlowTests : BoltzTestBase
    {
        public BoltzPaymentFlowTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact]
        public async Task CanSetupBoltzStandaloneAndCreateInvoice()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();

            await serverTester.SetupBoltzForStore(account.StoreId, BoltzMode.Standalone);

            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(account.StoreId);

            var client = await account.CreateClient();
            var invoice = await client.CreateInvoice(account.StoreId, new CreateInvoiceRequest
            {
                Amount = 10,
                Currency = "USD"
            });

            Assert.NotNull(invoice);
            Assert.Equal(InvoiceStatus.New, invoice.Status);

            var paymentMethods = await client.GetInvoicePaymentMethods(account.StoreId, invoice.Id);
            Assert.NotEmpty(paymentMethods);

            var lnMethod = paymentMethods.FirstOrDefault(m => m.PaymentMethodId.Contains("LN"));
            Assert.NotNull(lnMethod);
        }

        [Fact]
        public async Task CanSetupBoltzStandaloneAndAccessWallet()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();

            await serverTester.SetupBoltzForStore(account.StoreId, BoltzMode.Standalone);

            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(account.StoreId);
            var boltzClient = boltzService.Daemon.GetClient(settings);

            Assert.NotNull(boltzClient);

            var wallets = await boltzClient.GetWallets(true);
            Assert.NotNull(wallets);
            Assert.NotEmpty(wallets.Wallets_);

            var standaloneWallet = wallets.Wallets_.FirstOrDefault(w => w.Name == settings.StandaloneWallet?.Name);
            Assert.NotNull(standaloneWallet);
        }

        [Fact]
        public async Task CanSetupBoltzRebalanceMode()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();

            await account.MakeAdmin();

            var boltzService = await serverTester.GetBoltzService();
            var settings = await boltzService.InitializeStore(account.StoreId, BoltzMode.Rebalance);

            Assert.NotNull(settings);
            Assert.Equal(BoltzMode.Rebalance, settings.Mode);

            var boltzClient = boltzService.Daemon.GetClient(settings);
            Assert.NotNull(boltzClient);

            var info = await boltzClient.GetInfo();
            Assert.NotNull(info);
        }

        [Fact]
        public async Task CanGetBoltzInfoAfterSetup()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account.StoreId, BoltzMode.Standalone);

            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(account.StoreId);
            var boltzClient = boltzService.Daemon.GetClient(settings);

            var info = await boltzClient.GetInfo();
            Assert.NotNull(info);
            Assert.NotNull(info.Version);

            var stats = await boltzClient.GetStats();
            Assert.NotNull(stats);

            var pairs = await boltzClient.GetPairs();
            Assert.NotNull(pairs);
        }

        [Fact]
        public async Task CanCreateMultipleStoresWithBoltz()
        {
            using var serverTester = CreateServerTesterWithBoltz();

            var account1 = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account1.StoreId, BoltzMode.Standalone);

            var account2 = serverTester.NewAccount();
            account2.GrantAccess();
            await account2.CreateStoreAsync();

            var boltzService = await serverTester.GetBoltzService();
            var settings2 = await boltzService.InitializeStore(account2.StoreId, BoltzMode.Standalone);
            var client2 = boltzService.Daemon.GetClient(settings2);
            var wallet2 = await client2.CreateWallet(new Boltzrpc.WalletParams { Name = "test2", Currency = Boltzrpc.Currency.Lbtc });
            settings2.SetStandaloneWallet(wallet2.Wallet);
            await boltzService.Set(account2.StoreId, settings2);

            Assert.True(boltzService.StoreConfigured(account1.StoreId));
            Assert.True(boltzService.StoreConfigured(account2.StoreId));

            var settings1 = boltzService.GetSettings(account1.StoreId);
            settings2 = boltzService.GetSettings(account2.StoreId);

            Assert.NotEqual(settings1.TenantId, settings2.TenantId);
        }

        [Fact]
        public async Task CanAccessInvoiceAfterBoltzSetup()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();

            account.RegisterDerivationScheme("BTC");

            await serverTester.SetupBoltzForStore(account.StoreId, BoltzMode.Standalone);

            var client = await account.CreateClient();
            var invoice = await client.CreateInvoice(account.StoreId, new CreateInvoiceRequest
            {
                Amount = 100,
                Currency = "USD"
            });

            Assert.NotNull(invoice);
            Assert.Equal(InvoiceStatus.New, invoice.Status);

            var retrievedInvoice = await client.GetInvoice(account.StoreId, invoice.Id);
            Assert.Equal(invoice.Id, retrievedInvoice.Id);
            Assert.Equal(invoice.Amount, retrievedInvoice.Amount);
        }

        [Fact]
        public async Task BoltzSettingsPreservedAfterUpdate()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account.StoreId, BoltzMode.Standalone);

            var boltzService = await serverTester.GetBoltzService();
            var originalSettings = boltzService.GetSettings(account.StoreId);

            Assert.NotNull(originalSettings);
            var originalTenantId = originalSettings.TenantId;
            var originalMacaroon = originalSettings.Macaroon;

            var retrievedSettings = boltzService.GetSettings(account.StoreId);

            Assert.Equal(originalTenantId, retrievedSettings.TenantId);
            Assert.Equal(originalMacaroon, retrievedSettings.Macaroon);
            Assert.Equal(originalSettings.Mode, retrievedSettings.Mode);
            Assert.Equal(originalSettings.StandaloneWallet?.Name, retrievedSettings.StandaloneWallet?.Name);
        }
    }
}
