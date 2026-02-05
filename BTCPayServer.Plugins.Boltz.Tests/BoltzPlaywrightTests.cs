using System.Threading.Tasks;
using BTCPayServer.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Playwright", "Playwright")]
    [Collection(nameof(NonParallelizableCollectionDefinition))]
    public class BoltzPlaywrightTests : BoltzTestBase
    {
        public BoltzPlaywrightTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact]
        public async Task CanNavigateToBoltzPluginPage()
        {
            await using var s = CreatePlaywrightTesterWithBoltz();
            await s.StartAsync();
            await s.RegisterNewUser(true);
            var (_, storeId) = await s.CreateNewStore();

            await s.GoToUrl($"/plugins/{storeId}/Boltz");
            await s.Page.AssertNoError();

            var content = await s.Page.ContentAsync();
            Assert.Contains("Get Started with Boltz", content);
            Assert.Contains("Rebalance existing Lightning node", content);
            Assert.Contains("Accept Lightning payments without running a node", content);
        }

        [Fact]
        public async Task CanStartStandaloneSetupFlow()
        {
            await using var s = CreatePlaywrightTesterWithBoltz();
            await s.StartAsync();
            await s.RegisterNewUser(true);
            var (_, storeId) = await s.CreateNewStore();

            await s.GoToUrl($"/plugins/{storeId}/Boltz/setup");
            await s.Page.AssertNoError();

            await s.Page.ClickAsync("a[href*='mode=Standalone']");
            await s.Page.AssertNoError();

            var setupContent = await s.Page.ContentAsync();
            Assert.Contains("How does it work?", setupContent);

            await s.Page.ClickAsync("a.btn-success:has-text('Continue')");
            await s.Page.AssertNoError();

            var walletContent = await s.Page.ContentAsync();
            Assert.Contains("Create a new wallet", walletContent);
            Assert.Contains("Import a wallet", walletContent);
        }

        [Fact]
        public async Task CanAccessBoltzStatusPageAfterSetup()
        {
            await using var s = CreatePlaywrightTesterWithBoltz();
            await s.StartAsync();
            await s.RegisterNewUser(true);
            var (_, storeId) = await s.CreateNewStore();

            await s.Server.SetupBoltzForStore(storeId);

            await s.GoToUrl($"/plugins/{storeId}/Boltz/status");
            await s.Page.AssertNoError();

            var content = await s.Page.ContentAsync();
            Assert.Contains("Lightning Payments", content);
            Assert.Contains("test", content);
        }

        [Fact]
        public async Task CanViewWalletDetails()
        {
            await using var s = CreatePlaywrightTesterWithBoltz();
            await s.StartAsync();
            await s.RegisterNewUser(true);
            var (_, storeId) = await s.CreateNewStore();

            await s.Server.SetupBoltzForStore(storeId);

            await s.GoToUrl($"/plugins/{storeId}/Boltz/wallets");
            await s.Page.AssertNoError();
            var listContent = await s.Page.ContentAsync();
            Assert.Contains("View Details", listContent);
            Assert.Contains("test", listContent);

            var viewDetails = s.Page.Locator("a:has-text('View Details')").First;
            await viewDetails.ClickAsync();
            await s.Page.AssertNoError();

            var walletContent = await s.Page.ContentAsync();
            Assert.Contains("test", walletContent);
            Assert.Contains("Receive", walletContent);
        }

        [Fact]
        public async Task CanAccessSwapsPage()
        {
            await using var s = CreatePlaywrightTesterWithBoltz();
            await s.StartAsync();
            await s.RegisterNewUser(true);
            var (_, storeId) = await s.CreateNewStore();

            await s.Server.SetupBoltzForStore(storeId);

            await s.GoToUrl($"/plugins/{storeId}/Boltz/swaps");
            await s.Page.AssertNoError();

            var content = await s.Page.ContentAsync();
            Assert.Contains("There are no swaps.", content);
        }

        [Fact]
        public async Task CanAccessConfigurationPage()
        {
            await using var s = CreatePlaywrightTesterWithBoltz();
            await s.StartAsync();
            await s.RegisterNewUser(true);
            var (_, storeId) = await s.CreateNewStore();

            await s.Server.SetupBoltzForStore(storeId);

            await s.GoToUrl($"/plugins/{storeId}/Boltz/configuration");
            await s.Page.AssertNoError();

            var content = await s.Page.ContentAsync();
            Assert.Contains("Configuration", content);
            Assert.Contains("Lightning Wallet", content);
            Assert.Contains("Save", content);
        }

        [Fact]
        public async Task AdminCanAccessAdminPage()
        {
            await using var s = CreatePlaywrightTesterWithBoltz();
            await s.StartAsync();
            await s.RegisterNewUser(true);
            var (_, storeId) = await s.CreateNewStore();

            await s.Server.SetupBoltzForStore(storeId);

            await s.GoToUrl($"/plugins/{storeId}/Boltz/admin");
            await s.Page.AssertNoError();

            var content = await s.Page.ContentAsync();
            Assert.Contains("Logs", content);
            Assert.Contains("Save", content);
        }

        [Fact]
        public async Task BoltzPluginShowsInStoreIntegrations()
        {
            await using var s = CreatePlaywrightTesterWithBoltz();
            await s.StartAsync();
            await s.RegisterNewUser(true);
            var (_, storeId) = await s.CreateNewStore();

            await s.GoToUrl($"/stores/{storeId}/plugins");
            await s.Page.AssertNoError();

            var content = await s.Page.ContentAsync();
            Assert.Contains("Boltz", content);
        }

        [Fact]
        public async Task NonAdminCannotAccessAdminPage()
        {
            await using var s = CreatePlaywrightTesterWithBoltz();
            await s.StartAsync();
            await s.RegisterNewUser(false);
            var (_, storeId) = await s.CreateNewStore();

            await s.Server.SetupBoltzForStore(storeId);

            await s.GoToUrl($"/plugins/{storeId}/Boltz/admin");

            var title = await s.Page.TitleAsync();
            Assert.Contains("Denied", title);
        }
    }
}
