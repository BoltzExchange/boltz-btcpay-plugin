using System;
using System.Threading.Tasks;
using BTCPayServer.Tests;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    /// <summary>
    /// Extension methods for Playwright testing of the Boltz plugin.
    /// Provides helper methods for common Boltz UI navigation and interaction patterns.
    /// </summary>
    public static class BoltzPlaywrightExtensions
    {
        /// <summary>
        /// Navigate to the Boltz plugin root page for a store.
        /// </summary>
        public static async Task GoToBoltzPlugin(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz");
        }

        /// <summary>
        /// Navigate to the Boltz status page.
        /// </summary>
        public static async Task GoToBoltzStatus(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/status");
        }

        /// <summary>
        /// Navigate to the Boltz wallets page.
        /// </summary>
        public static async Task GoToBoltzWallets(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/wallets");
        }

        /// <summary>
        /// Navigate to the Boltz swaps page.
        /// </summary>
        public static async Task GoToBoltzSwaps(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/swaps");
        }

        /// <summary>
        /// Navigate to the Boltz configuration page.
        /// </summary>
        public static async Task GoToBoltzConfiguration(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/configuration");
        }

        /// <summary>
        /// Navigate to the Boltz admin page.
        /// Requires admin privileges.
        /// </summary>
        public static async Task GoToBoltzAdmin(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/admin");
        }

        /// <summary>
        /// Navigate to the Boltz setup mode selection page.
        /// </summary>
        public static async Task GoToBoltzSetup(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/setup");
        }

        /// <summary>
        /// Start Boltz setup in Standalone mode.
        /// </summary>
        public static async Task StartBoltzStandaloneSetup(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/setup/Standalone");
        }

        /// <summary>
        /// Start Boltz setup in Rebalance mode.
        /// </summary>
        public static async Task StartBoltzRebalanceSetup(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/setup/Rebalance");
        }

        /// <summary>
        /// Select Standalone mode from the setup page.
        /// </summary>
        public static async Task SelectBoltzStandaloneMode(this PlaywrightTester tester)
        {
            await tester.Page.ClickAsync("a[href*='mode=Standalone']");
        }

        /// <summary>
        /// Select Rebalance mode from the setup page.
        /// Requires Lightning node to be configured.
        /// </summary>
        public static async Task SelectBoltzRebalanceMode(this PlaywrightTester tester)
        {
            await tester.Page.ClickAsync("a[href*='mode=Rebalance']");
        }

        /// <summary>
        /// Check if the Boltz daemon is running by looking for error messages.
        /// </summary>
        public static async Task<bool> IsBoltzDaemonRunning(this PlaywrightTester tester)
        {
            var content = await tester.Page.ContentAsync();
            return !content.Contains("Daemon is not yet running");
        }

        /// <summary>
        /// Check if Boltz is configured for the current store.
        /// </summary>
        public static async Task<bool> IsBoltzConfigured(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToBoltzStatus(storeId);

            var content = await tester.Page.ContentAsync();
            // If we see the status page content, it's configured
            return content.Contains("Lightning Payments") || content.Contains("AutoSwap");
        }

        /// <summary>
        /// Wait for the Boltz page to fully load.
        /// </summary>
        public static async Task WaitForBoltzPageLoad(this PlaywrightTester tester)
        {
            // Wait for the main content area to be visible
            await tester.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        /// <summary>
        /// Navigate to a specific wallet by name.
        /// </summary>
        public static async Task GoToBoltzWallet(this PlaywrightTester tester, string walletName, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToBoltzWallets(storeId);

            // Find and click the wallet link
            var walletLink = tester.Page.Locator($"a:has-text('{walletName}')");
            await walletLink.ClickAsync();
        }

        /// <summary>
        /// Navigate to a specific swap by ID.
        /// </summary>
        public static async Task GoToBoltzSwap(this PlaywrightTester tester, string swapId, string storeId = null)
        {
            storeId ??= tester.StoreId;
            await tester.GoToUrl($"/plugins/{storeId}/Boltz/swaps/{swapId}");
        }

        /// <summary>
        /// Check if the current page is the Boltz setup page.
        /// </summary>
        public static async Task<bool> IsOnBoltzSetupPage(this PlaywrightTester tester)
        {
            var content = await tester.Page.ContentAsync();
            return content.Contains("Get Started with Boltz");
        }

        /// <summary>
        /// Click the primary action button on the current page.
        /// </summary>
        public static async Task ClickBoltzPrimaryButton(this PlaywrightTester tester)
        {
            await tester.Page.ClickAsync("button.btn-primary, input[type='submit'].btn-primary");
        }

        /// <summary>
        /// Fill in the wallet creation form.
        /// </summary>
        public static async Task FillWalletCreationForm(this PlaywrightTester tester, string walletName)
        {
            var nameInput = tester.Page.Locator("input[name='Name'], #Name");
            if (await nameInput.IsVisibleAsync())
            {
                await nameInput.FillAsync(walletName);
            }
        }

        /// <summary>
        /// Select a currency in a dropdown.
        /// </summary>
        public static async Task SelectBoltzCurrency(this PlaywrightTester tester, string currency)
        {
            var currencySelect = tester.Page.Locator("select[name='Currency'], #Currency");
            if (await currencySelect.IsVisibleAsync())
            {
                await currencySelect.SelectOptionAsync(new SelectOptionValue { Label = currency });
            }
        }

        /// <summary>
        /// Get the current wallet balance displayed on the page.
        /// </summary>
        public static async Task<string> GetDisplayedBalance(this PlaywrightTester tester)
        {
            var balanceElement = tester.Page.Locator(".balance, [data-balance]");
            if (await balanceElement.IsVisibleAsync())
            {
                return await balanceElement.TextContentAsync();
            }
            return null;
        }

        /// <summary>
        /// Wait for a success alert message to appear.
        /// </summary>
        public static async Task WaitForBoltzSuccessMessage(this PlaywrightTester tester)
        {
            await tester.Page.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10000
            });
        }

        /// <summary>
        /// Wait for an error alert message to appear.
        /// </summary>
        public static async Task WaitForBoltzErrorMessage(this PlaywrightTester tester)
        {
            await tester.Page.Locator(".alert-danger, .alert-warning").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10000
            });
        }

        /// <summary>
        /// Check if there is an error message displayed.
        /// </summary>
        public static async Task<bool> HasBoltzErrorMessage(this PlaywrightTester tester)
        {
            var errorAlert = tester.Page.Locator(".alert-danger");
            return await errorAlert.IsVisibleAsync();
        }

        /// <summary>
        /// Click the save/submit button in a form.
        /// </summary>
        public static async Task SubmitBoltzForm(this PlaywrightTester tester, string buttonText = null)
        {
            if (buttonText != null)
            {
                await tester.Page.ClickAsync($"button:has-text('{buttonText}'), input[value='{buttonText}']");
            }
            else
            {
                await tester.Page.ClickAsync("button[type='submit'], input[type='submit']");
            }
        }

        /// <summary>
        /// Navigate through the Boltz sidebar menu.
        /// </summary>
        public static async Task ClickBoltzNavItem(this PlaywrightTester tester, string navItemText)
        {
            var navItem = tester.Page.Locator($"nav a:has-text('{navItemText}'), .sidebar a:has-text('{navItemText}')");
            await navItem.ClickAsync();
        }

        /// <summary>
        /// Setup Boltz for a store programmatically and then navigate to the status page.
        /// Combines API setup with UI navigation.
        /// </summary>
        public static async Task SetupBoltzAndNavigate(this PlaywrightTester tester, string storeId = null)
        {
            storeId ??= tester.StoreId;

            // Setup via API
            await tester.Server.SetupBoltzForStore(storeId);

            // Navigate to status page
            await tester.GoToBoltzStatus(storeId);
            await tester.Page.AssertNoError();
        }

        /// <summary>
        /// Create a store and setup Boltz in one operation.
        /// </summary>
        public static async Task<string> CreateStoreWithBoltz(this PlaywrightTester tester)
        {
            var (_, storeId) = await tester.CreateNewStore();
            await tester.Server.SetupBoltzForStore(storeId);
            return storeId;
        }

        /// <summary>
        /// Verify the Boltz status page loads correctly.
        /// </summary>
        public static async Task AssertBoltzStatusPageLoaded(this PlaywrightTester tester)
        {
            await tester.Page.AssertNoError();
            var content = await tester.Page.ContentAsync();

            // Status page should have certain elements
            if (!content.Contains("Lightning Payments") && !content.Contains("AutoSwap") && !content.Contains("Status"))
            {
                throw new Exception("Boltz status page did not load correctly");
            }
        }
    }
}
