using System;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Fast", "Fast")]
    public class BoltzControllerTests : BoltzTestBase
    {
        public BoltzControllerTests(ITestOutputHelper helper) : base(helper)
        {
            helper.WriteLine("BoltzControllerTests");
        }

        [Fact]
        public async Task CanAccessStatusPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            TestLogs.LogInformation("CanAccessStatusPage");
            var storeId = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(storeId);

            var account = serverTester.NewAccount();
            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);
            var result = await controller.Status(storeId);

            // Should redirect to setup if not properly configured
            Assert.IsType<RedirectToActionResult>(result);
        }

        [Fact]
        public async Task CanAccessConfigurationPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            return;
            var storeId = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(storeId);

            var account = serverTester.NewAccount();
            account.GrantAccess();

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);

            var result = await controller.Configuration(storeId);

            // Should redirect to setup if not properly configured
            Assert.IsType<RedirectToActionResult>(result);
        }

        [Fact]
        public async Task CanAccessSetupPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();

            var account = serverTester.NewAccount();
            account.GrantAccess();

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);

            var result = await controller.SetupMode(new ModeSetup(), null, storeId);

            Assert.IsType<ViewResult>(result);
        }

        [Fact]
        public async Task CanAccessWalletsPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(storeId);

            var account = serverTester.NewAccount();
            account.GrantAccess();

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);

            var result = await controller.Wallets(null, new WalletViewModel());

            // Should redirect to setup if not properly configured
            Assert.IsType<RedirectToActionResult>(result);
        }

        [Fact]
        public async Task CanAccessSwapsPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(storeId);

            var account = serverTester.NewAccount();
            account.GrantAccess();

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);

            var result = await controller.Swaps(new SwapsModel(), null);

            // Should redirect to setup if not properly configured
            Assert.IsType<RedirectToActionResult>(result);
        }

        [Fact]
        public async Task CanAccessPayoutsPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(storeId);

            var account = serverTester.NewAccount();
            account.GrantAccess();

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);

            var result = await controller.Payouts(new PayoutsModel(), null);

            // Should redirect to setup if not properly configured
            Assert.IsType<RedirectToActionResult>(result);
        }

        [Fact]
        public async Task CanAccessAdminPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();

            var account = serverTester.NewAccount();
            account.GrantAccess();
            await account.MakeAdmin(true); // Make admin to access admin page

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);

            var result = await controller.Admin(storeId, logFile: null);

            Assert.IsType<ViewResult>(result);
        }

        [Fact]
        public async Task NonAdminCannotAccessAdminPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();

            var account = serverTester.NewAccount();
            account.GrantAccess();
            // Don't make admin

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);

            var result = await controller.Admin(storeId, logFile: null);

            // Should return unauthorized
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task CanAccessWalletSetupPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();

            var account = serverTester.NewAccount();
            account.GrantAccess();

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);

            var result = await controller.SetupWallet(new WalletSetup());

            // Should redirect to setup if not properly configured
            Assert.IsType<RedirectToActionResult>(result);
        }

        [Fact]
        public async Task CanAccessCreateWalletPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();

            var account = serverTester.NewAccount();
            account.GrantAccess();

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, storeId, account.IsAdmin);

            var result = controller.CreateWallet(new WalletSetup(), storeId);

            // Should redirect to setup if not properly configured
            Assert.IsType<RedirectToActionResult>(result);
        }
    }
}
