using System;
using System.Linq;
using System.Threading.Tasks;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Integration", "Integration")]
    public class BoltzConfigurationTests : BoltzTestBase
    {
        public BoltzConfigurationTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact]
        public async Task CanAccessConfigurationPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var result = await controller.Configuration(account.StoreId);
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<BoltzConfig>(viewResult.Model);

            Assert.NotNull(model.Settings);
            Assert.NotNull(model.ExistingWallets);
        }

        [Fact]
        public async Task CanConfigureLightningAutoSwap()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await account.MakeAdmin();

            var boltzService = await serverTester.GetBoltzService();
            var settings = await boltzService.InitializeStore(account.StoreId, BoltzMode.Rebalance);
            var client = boltzService.Daemon.GetClient(settings);

            var wallet = await client.CreateWallet(new Boltzrpc.WalletParams
            {
                Name = "ln-autoswap-test",
                Currency = Boltzrpc.Currency.Lbtc
            });

            await boltzService.Set(account.StoreId, settings);

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var lnConfig = new LightningConfig
            {
                Wallet = wallet.Wallet.Name,
                Currency = Currency.Lbtc,
                InboundBalancePercent = 25,
                OutboundBalancePercent = 25,
                MaxFeePercent = 1.0f,
                Budget = 100000,
                BudgetInterval = 30
            };

            var vm = new BoltzConfig
            {
                Ln = lnConfig,
                Settings = settings
            };

            var result = await controller.Configuration(account.StoreId, vm, "BoltzSetLnConfig");
            Assert.IsType<RedirectToActionResult>(result);

            var (updatedLnConfig, _) = await client.GetAutoSwapConfig();
            Assert.NotNull(updatedLnConfig);
            Assert.Equal(wallet.Wallet.Name, updatedLnConfig.Wallet);
            Assert.Equal(Currency.Lbtc, updatedLnConfig.Currency);
            Assert.Equal(25f, updatedLnConfig.InboundBalancePercent);
            Assert.Equal(25f, updatedLnConfig.OutboundBalancePercent);
            Assert.Equal(1.0f, updatedLnConfig.MaxFeePercent);
            Assert.Equal(100000ul, updatedLnConfig.Budget);
            Assert.Equal((ulong)TimeSpan.FromDays(30).TotalSeconds, updatedLnConfig.BudgetInterval);
        }

        [Fact]
        public async Task CanConfigureChainAutoSwap()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await account.MakeAdmin();
            await serverTester.SetupBoltzForStore(account.StoreId, BoltzMode.Standalone);
            account.RegisterDerivationScheme("BTC");

            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(account.StoreId);
            var client = boltzService.Daemon.GetClient(settings);

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var chainConfig = new ChainConfig
            {
                MaxBalance = 1000000,
                ReserveBalance = 10000,
                MaxFeePercent = 0.5f,
                Budget = 500000,
                BudgetInterval = 7
            };

            var vm = new BoltzConfig
            {
                Chain = chainConfig,
                Settings = settings
            };

            var result = await controller.Configuration(account.StoreId, vm, "BoltzSetChainConfig");
            Assert.IsType<RedirectToActionResult>(result);

            var (_, updatedChainConfig) = await client.GetAutoSwapConfig();
            Assert.NotNull(updatedChainConfig);
            Assert.Equal("test", updatedChainConfig.FromWallet);
            Assert.Equal(1000000ul, updatedChainConfig.MaxBalance);
            Assert.Equal(10000ul, updatedChainConfig.ReserveBalance);
            Assert.Equal(0.5f, updatedChainConfig.MaxFeePercent);
            Assert.Equal(500000ul, updatedChainConfig.Budget);
            Assert.Equal((ulong)TimeSpan.FromDays(7).TotalSeconds, updatedChainConfig.BudgetInterval);
            Assert.False(string.IsNullOrEmpty(updatedChainConfig.ToAddress));
        }

        [Fact]
        public async Task ConfigurationShowsExistingWallets()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(account.StoreId);
            var client = boltzService.Daemon.GetClient(settings);

            await client.CreateWallet(new Boltzrpc.WalletParams
            {
                Name = "extra-wallet-1",
                Currency = Boltzrpc.Currency.Lbtc
            });
            await client.CreateWallet(new Boltzrpc.WalletParams
            {
                Name = "extra-wallet-2",
                Currency = Boltzrpc.Currency.Btc
            });

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var result = await controller.Configuration(account.StoreId);
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<BoltzConfig>(viewResult.Model);

            Assert.True(model.ExistingWallets.Count >= 3);
            Assert.Contains(model.ExistingWallets, wallet => wallet.Name == "extra-wallet-1");
            Assert.Contains(model.ExistingWallets, wallet => wallet.Name == "extra-wallet-2");
        }

        [Fact]
        public async Task ConfigurationRedirectsWhenNotConfigured()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var result = await controller.Configuration(account.StoreId);

            Assert.IsType<RedirectToActionResult>(result);
            var redirect = (RedirectToActionResult)result;
            Assert.Equal("SetupMode", redirect.ActionName);
        }

        [Fact]
        public async Task CanUpdateStandaloneWalletViaConfiguration()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account.StoreId, BoltzMode.Standalone);

            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(account.StoreId);
            var client = boltzService.Daemon.GetClient(settings);

            var newWallet = await client.CreateWallet(new Boltzrpc.WalletParams
            {
                Name = "new-standalone-wallet",
                Currency = Boltzrpc.Currency.Lbtc
            });

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var updatedSettings = new BoltzSettings
            {
                Mode = BoltzMode.Standalone,
                StandaloneWallet = newWallet.Wallet
            };

            var vm = new BoltzConfig
            {
                Settings = updatedSettings,
                Chain = null
            };

            var result = await controller.Configuration(account.StoreId, vm, "BoltzSetChainConfig");
            Assert.IsType<RedirectToActionResult>(result);

            var refreshedSettings = boltzService.GetSettings(account.StoreId);
            Assert.Equal("new-standalone-wallet", refreshedSettings?.StandaloneWallet?.Name);
        }

        [Fact]
        public async Task ConfigurationPreservesExistingSettings()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var boltzService = await serverTester.GetBoltzService();
            var originalSettings = boltzService.GetSettings(account.StoreId);
            var originalMode = originalSettings?.Mode;
            var originalWalletName = originalSettings?.StandaloneWallet?.Name;

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            await controller.Configuration(account.StoreId);
            await controller.Configuration(account.StoreId);

            var currentSettings = boltzService.GetSettings(account.StoreId);
            Assert.Equal(originalMode, currentSettings?.Mode);
            Assert.Equal(originalWalletName, currentSettings?.StandaloneWallet?.Name);
        }

        [Fact]
        public async Task WalletSelectListFiltersCorrectly()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(account.StoreId);
            var client = boltzService.Daemon.GetClient(settings);

            await client.CreateWallet(new Boltzrpc.WalletParams
            {
                Name = "btc-wallet",
                Currency = Boltzrpc.Currency.Btc
            });
            await client.CreateWallet(new Boltzrpc.WalletParams
            {
                Name = "lbtc-wallet",
                Currency = Boltzrpc.Currency.Lbtc
            });

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var result = await controller.Configuration(account.StoreId);
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<BoltzConfig>(viewResult.Model);

            var btcWallets = model.WalletSelectList(Currency.Btc).Cast<SelectListItem>();
            var lbtcWallets = model.WalletSelectList(Currency.Lbtc).Cast<SelectListItem>();
            var allWallets = model.WalletSelectList(null).Cast<SelectListItem>();

            Assert.Contains(btcWallets, wallet => wallet.Text == "btc-wallet");
            Assert.Contains(lbtcWallets, wallet => wallet.Text == "lbtc-wallet");
            Assert.Contains(allWallets, wallet => wallet.Text == "btc-wallet");
            Assert.Contains(allWallets, wallet => wallet.Text == "lbtc-wallet");
        }
    }
}
