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
        private const string ContinueButton = ".btn.btn-success:has-text('Continue')";
        private const string CreateWalletLink = "a.list-group-item:has-text('Create a new wallet')";
        private const string CreateButton = "button.btn.btn-success:has-text('Create')";
        private const string EnableButton = "button.btn.btn-success:has-text('Enable')";
        private const string RecoveryPhraseHeading = "Secure your recovery phrase";

        public BoltzUITests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        [Trait("Playwright", "Playwright")]
        public async Task CanRenderStatusPage()
        {
            await using var tester = CreatePlaywrightTesterWithBoltz();
            var account = await CreateConfiguredPlaywrightStore(tester);
            var page = tester.Page;

            await GoToBoltzPage(tester, account.StoreId, "status");

            await Expect(page.Locator("h4:has-text('Lightning Payments')")).ToBeVisibleAsync();
            await Expect(page.Locator("body")).ToContainTextAsync("Using wallet");
            await Expect(page.Locator($"a[href='/plugins/{account.StoreId}/Boltz/wallets/test']")).ToBeVisibleAsync();
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        [Trait("Playwright", "Playwright")]
        public async Task CanRenderWalletPage()
        {
            await using var tester = CreatePlaywrightTesterWithBoltz();
            var account = await CreateConfiguredPlaywrightStore(tester);
            var page = tester.Page;

            await GoToBoltzPage(tester, account.StoreId, "wallets/test");

            await Expect(page.Locator("h2.mb-1")).ToHaveTextAsync("test");
            await Expect(page.Locator("body")).ToContainTextAsync("Hot wallet");
            await Expect(page.Locator("a.btn.btn-primary:has-text('Receive')")).ToBeVisibleAsync();
            await Expect(page.Locator("body")).ToContainTextAsync("No transactions available.");
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        [Trait("Playwright", "Playwright")]
        public async Task CanCompleteSetupFlow()
        {
            await using var tester = CreatePlaywrightTesterWithBoltz();
            var account = await CreatePlaywrightStore(tester);
            var page = tester.Page;
            await PrepareSetupFlow(tester.Server);

            var suffix = Guid.NewGuid().ToString("N")[..8];
            var lightningWalletName = $"lightning-{suffix}";
            var mainchainWalletName = $"mainchain-{suffix}";

            await GoToBoltzPage(tester, account.StoreId, "setup");
            await ExpectWizardHeading(page, "How does it work?");

            await CreateWalletInSetupFlow(page, "Setup L-BTC Wallet", "Create L-BTC Wallet", lightningWalletName,
                "Set Up Chain Swaps");
            await CreateWalletInSetupFlow(page, "Setup BTC Wallet", "Create BTC Wallet", mainchainWalletName,
                "Setup Budget");
            await EnableAutoswap(page);

            Assert.Contains($"/plugins/{account.StoreId}/Boltz/status", page.Url, StringComparison.Ordinal);
            await Expect(page.Locator("body")).ToContainTextAsync("Auto swap enabled");
            await Expect(page.Locator("h4:has-text('Lightning Payments')")).ToBeVisibleAsync();
            await Expect(page.Locator("body")).ToContainTextAsync(lightningWalletName);

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

        private static async Task<TestAccount> CreateConfiguredPlaywrightStore(PlaywrightTester tester)
        {
            var account = await CreatePlaywrightStore(tester);
            await tester.Server.SetupBoltzForStore(account.StoreId);
            return account;
        }

        private static async Task GoToBoltzPage(PlaywrightTester tester, string storeId, string path)
        {
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/{path}");
            await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await tester.Page.AssertNoError();
        }

        private static async Task PrepareSetupFlow(ServerTester server)
        {
            await server.EnsureBoltzServerReady();
        }

        private static async Task CreateWalletInSetupFlow(IPage page, string setupHeading, string createHeading,
            string walletName, string nextHeading)
        {
            await ClickAndExpectHeading(page, ContinueButton, setupHeading);
            await ClickAndExpectHeading(page, CreateWalletLink, createHeading);
            await page.FillAsync("#WalletName", walletName);
            await ClickAndExpectHeading(page, CreateButton, RecoveryPhraseHeading);
            await ConfirmRecoveryPhrase(page);
            await ContinuePastSubaccountSelectionIfNeeded(page, nextHeading);
        }

        private static async Task ConfirmRecoveryPhrase(IPage page)
        {
            await page.Locator("#confirm").CheckAsync();
            await page.Locator("#submit").ClickAsync();
        }

        private static async Task EnableAutoswap(IPage page)
        {
            await ClickAndExpectHeading(page, ContinueButton, "Enable Autoswap");
            await page.Locator(EnableButton).ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.AssertNoError();
        }

        private static async Task ClickAndExpectHeading(IPage page, string selector, string heading)
        {
            await page.Locator(selector).ClickAsync();
            await ExpectWizardHeading(page, heading);
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
