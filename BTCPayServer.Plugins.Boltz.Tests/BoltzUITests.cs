using System;
using System.Threading.Tasks;
using BTCPayServer.Tests;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Integration", "Integration")]
    [Trait("Lightning", "Lightning")]
    public class BoltzUITests : BoltzTestBase
    {
        public BoltzUITests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        [Trait("Playwright", "Playwright")]
        public async Task CanRenderStatusPage()
        {
            await using var tester = CreatePlaywrightTesterWithBoltz();
            var account = await CreatePlaywrightStore(tester);
            await tester.Server.SetupBoltzForStore(account.StoreId);

            await tester.GoToUrl($"/plugins/{account.StoreId}/Boltz/status");
            await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await tester.Page.AssertNoError();

            await Expect(tester.Page.Locator("h4:has-text('Lightning Payments')")).ToBeVisibleAsync();
            await Expect(tester.Page.Locator("body")).ToContainTextAsync("Using wallet");
            await Expect(tester.Page.Locator($"a[href='/plugins/{account.StoreId}/Boltz/wallets/test']")).ToBeVisibleAsync();
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        [Trait("Playwright", "Playwright")]
        public async Task CanRenderWalletPage()
        {
            await using var tester = CreatePlaywrightTesterWithBoltz();
            var account = await CreatePlaywrightStore(tester);
            await tester.Server.SetupBoltzForStore(account.StoreId);

            await tester.GoToUrl($"/plugins/{account.StoreId}/Boltz/wallets/test");
            await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await tester.Page.AssertNoError();

            await Expect(tester.Page.Locator("h2.mb-1")).ToHaveTextAsync("test");
            await Expect(tester.Page.Locator("body")).ToContainTextAsync("Hot wallet");
            await Expect(tester.Page.Locator("a.btn.btn-primary:has-text('Receive')")).ToBeVisibleAsync();
            await Expect(tester.Page.Locator("body")).ToContainTextAsync("No transactions available.");
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        [Trait("Playwright", "Playwright")]
        public async Task CanCompleteSetupFlow()
        {
            await using var tester = CreatePlaywrightTesterWithBoltz();
            var account = await CreatePlaywrightStore(tester);
            await PrepareSetupFlow(tester.Server);

            var suffix = Guid.NewGuid().ToString("N")[..8];
            var lightningWalletName = $"lightning-{suffix}";
            var mainchainWalletName = $"mainchain-{suffix}";

            await tester.GoToUrl($"/plugins/{account.StoreId}/Boltz/setup");
            await ExpectWizardHeading(tester.Page, "How does it work?");

            await tester.Page.Locator("a.btn.btn-success:has-text('Continue')").ClickAsync();
            await ExpectWizardHeading(tester.Page, "Setup L-BTC Wallet");

            await tester.Page.Locator("a.list-group-item:has-text('Create a new wallet')").ClickAsync();
            await ExpectWizardHeading(tester.Page, "Create L-BTC Wallet");
            await tester.Page.FillAsync("#WalletName", lightningWalletName);
            await tester.Page.Locator("button.btn.btn-success:has-text('Create')").ClickAsync();
            await ExpectWizardHeading(tester.Page, "Secure your recovery phrase");
            await tester.Page.Locator("#confirm").CheckAsync();
            await tester.Page.Locator("#submit").ClickAsync();
            await ContinuePastSubaccountSelectionIfNeeded(tester.Page, "Set Up Chain Swaps");

            await tester.Page.Locator("button.btn.btn-success:has-text('Continue')").ClickAsync();
            await ExpectWizardHeading(tester.Page, "Setup BTC Wallet");

            await tester.Page.Locator("a.list-group-item:has-text('Create a new wallet')").ClickAsync();
            await ExpectWizardHeading(tester.Page, "Create BTC Wallet");
            await tester.Page.FillAsync("#WalletName", mainchainWalletName);
            await tester.Page.Locator("button.btn.btn-success:has-text('Create')").ClickAsync();
            await ExpectWizardHeading(tester.Page, "Secure your recovery phrase");
            await tester.Page.Locator("#confirm").CheckAsync();
            await tester.Page.Locator("#submit").ClickAsync();
            await ContinuePastSubaccountSelectionIfNeeded(tester.Page, "Setup Budget");

            await tester.Page.Locator("button.btn.btn-success:has-text('Continue')").ClickAsync();
            await ExpectWizardHeading(tester.Page, "Enable Autoswap");

            await tester.Page.Locator("button.btn.btn-success:has-text('Enable')").ClickAsync();
            await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await tester.Page.AssertNoError();

            Assert.Contains($"/plugins/{account.StoreId}/Boltz/status", tester.Page.Url, StringComparison.Ordinal);
            await Expect(tester.Page.Locator("body")).ToContainTextAsync("Auto swap enabled");
            await Expect(tester.Page.Locator("h4:has-text('Lightning Payments')")).ToBeVisibleAsync();
            await Expect(tester.Page.Locator("body")).ToContainTextAsync(lightningWalletName);

            var boltzService = await tester.Server.GetBoltzService();
            Assert.True(boltzService.StoreConfigured(account.StoreId));

            var settings = boltzService.GetSettings(account.StoreId);
            Assert.NotNull(settings?.StandaloneWallet);
            Assert.Equal(lightningWalletName, settings!.StandaloneWallet!.Name);

            var client = boltzService.GetClient(account.StoreId);
            Assert.NotNull(client);

            var chainConfig = await client!.GetChainConfig();
            Assert.NotNull(chainConfig);
            Assert.True(chainConfig!.Enabled);
            Assert.Equal(lightningWalletName, chainConfig.FromWallet);
            Assert.Equal(mainchainWalletName, chainConfig.ToWallet);
            Assert.Equal(10_000_000UL, chainConfig.MaxBalance);
            Assert.Equal(500_000UL, chainConfig.ReserveBalance);
            Assert.Equal(100_000UL, chainConfig.Budget);
            Assert.Equal((ulong)TimeSpan.FromDays(7).TotalSeconds, chainConfig.BudgetInterval);
            Assert.Equal(1f, chainConfig.MaxFeePercent);
        }

        private static async Task<TestAccount> CreatePlaywrightStore(PlaywrightTester tester)
        {
            await tester.StartAsync();
            await tester.RegisterNewUser();
            await tester.CreateNewStore();
            return tester.AsTestAccount();
        }

        private static async Task PrepareSetupFlow(ServerTester server)
        {
            var boltzService = await server.GetBoltzService();
            await boltzService.SetServerSettings(BoltzTestUtils.CreateTestBoltzServerSettings());

            for (var attempt = 0; attempt < 45; attempt++)
            {
                if (boltzService.AdminClient is not null && boltzService.Daemon.Running)
                {
                    return;
                }

                await Task.Delay(1000);
            }

            await boltzService.SetServerSettings(BoltzTestUtils.CreateTestBoltzServerSettings());
            await TestUtils.EventuallyAsync(() =>
            {
                Assert.NotNull(boltzService.AdminClient);
                Assert.True(boltzService.Daemon.Running);
                return Task.CompletedTask;
            }, TestUtils.TestTimeout);
        }

        private static async Task ExpectWizardHeading(IPage page, string heading)
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.AssertNoError();
            await Expect(page.Locator("h1")).ToHaveTextAsync(heading);
        }

        private static async Task ContinuePastSubaccountSelectionIfNeeded(IPage page, string nextHeading)
        {
            var heading = await WaitForAnyHeading(page, nextHeading, "Choose your wallet subaccount");
            if (!string.Equals(heading, "Choose your wallet subaccount", StringComparison.Ordinal))
            {
                return;
            }

            var newSegwitButton = page.Locator("button:has-text('New SegWit')");
            await Expect(newSegwitButton).ToBeVisibleAsync();
            await newSegwitButton.ClickAsync();
            await ExpectWizardHeading(page, nextHeading);
        }

        private static async Task<string> WaitForAnyHeading(IPage page, params string[] expectedHeadings)
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                var heading = (await page.Locator("h1").TextContentAsync())?.Trim();
                if (!string.IsNullOrEmpty(heading))
                {
                    foreach (var expectedHeading in expectedHeadings)
                    {
                        if (string.Equals(heading, expectedHeading, StringComparison.Ordinal))
                        {
                            await page.AssertNoError();
                            return heading;
                        }
                    }
                }

                await Task.Delay(500);
            }

            var actualHeading = (await page.Locator("h1").TextContentAsync())?.Trim();
            throw new InvalidOperationException(
                $"Expected one of [{string.Join(", ", expectedHeadings)}] but found '{actualHeading}'.");
        }
    }
}
