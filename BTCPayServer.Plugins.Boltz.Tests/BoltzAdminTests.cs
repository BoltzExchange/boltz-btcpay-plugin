using System.Threading.Tasks;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Integration", "Integration")]
    public class BoltzAdminTests : BoltzTestBase
    {
        public BoltzAdminTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact]
        public async Task CanAccessAdminPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await account.MakeAdmin();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);
            Assert.NotNull(controller);

            var result = await controller.Admin(account.StoreId, null);
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<AdminModel>(viewResult.Model);

            Assert.NotNull(model.ServerSettings);
            Assert.NotNull(model.Settings);
        }

        [Fact]
        public async Task CanSaveServerSettings()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await account.MakeAdmin();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var boltzService = await serverTester.GetBoltzService();
            var originalSettings = boltzService.ServerSettings;

            var newSettings = new BoltzServerSettings
            {
                AllowTenants = !originalSettings.AllowTenants,
                ConnectNode = originalSettings.ConnectNode,
                LogLevel = "debug"
            };

            var vm = new AdminModel
            {
                ServerSettings = newSettings
            };

            var result = await controller.Admin(account.StoreId, vm, "Save");
            Assert.IsType<RedirectToActionResult>(result);

            var updatedSettings = boltzService.ServerSettings;
            Assert.Equal(newSettings.AllowTenants, updatedSettings.AllowTenants);
            Assert.Equal(newSettings.LogLevel, updatedSettings.LogLevel);
        }

        [Fact]
        public async Task CanClearBoltzSettings()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await account.MakeAdmin();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var boltzService = await serverTester.GetBoltzService();

            Assert.True(boltzService.StoreConfigured(account.StoreId));

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var vm = new AdminModel();
            var result = await controller.Admin(account.StoreId, vm, "Clear");
            Assert.IsType<RedirectToActionResult>(result);

            Assert.False(boltzService.StoreConfigured(account.StoreId));
        }

        [Fact]
        public async Task CanInitiateDaemonRestart()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await account.MakeAdmin();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var vm = new AdminModel();
            var result = await controller.Admin(account.StoreId, vm, "Start");
            Assert.IsType<RedirectToActionResult>(result);

            await Task.Delay(1000);

            var adminResult = await controller.Admin(account.StoreId, null);
            Assert.IsType<ViewResult>(adminResult);
        }

        [Fact]
        public async Task AdminSettingsPersistAcrossRequests()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await account.MakeAdmin();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var boltzService = await serverTester.GetBoltzService();

            var customSettings = new BoltzServerSettings
            {
                AllowTenants = true,
                ConnectNode = false,
                LogLevel = "info"
            };

            await boltzService.SetServerSettings(customSettings);

            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var result = await controller.Admin(account.StoreId, null);
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<AdminModel>(viewResult.Model);

            Assert.Equal(customSettings.AllowTenants, model.ServerSettings?.AllowTenants);
            Assert.Equal(customSettings.ConnectNode, model.ServerSettings?.ConnectNode);
            Assert.Equal(customSettings.LogLevel, model.ServerSettings?.LogLevel);
        }

        [Fact]
        public async Task CanToggleAllowTenants()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await account.MakeAdmin();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var boltzService = await serverTester.GetBoltzService();
            var controller = serverTester.PayTester.GetController<BoltzController>(
                account.UserId, account.StoreId, account.IsAdmin);

            var originalAllowTenants = boltzService.ServerSettings.AllowTenants;

            var vm = new AdminModel
            {
                ServerSettings = new BoltzServerSettings
                {
                    AllowTenants = !originalAllowTenants,
                    ConnectNode = boltzService.ServerSettings.ConnectNode
                }
            };

            await controller.Admin(account.StoreId, vm, "Save");

            Assert.NotEqual(originalAllowTenants, boltzService.ServerSettings.AllowTenants);

            vm.ServerSettings.AllowTenants = originalAllowTenants;
            await controller.Admin(account.StoreId, vm, "Save");

            Assert.Equal(originalAllowTenants, boltzService.ServerSettings.AllowTenants);
        }

        [Fact]
        public async Task DaemonInfoAvailableAfterSetup()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await account.MakeAdmin();
            await serverTester.SetupBoltzForStore(account.StoreId);

            var boltzDaemon = await serverTester.GetBoltzDaemon();

            Assert.NotNull(boltzDaemon.AdminClient);

            var info = await boltzDaemon.AdminClient.GetInfo();
            Assert.NotNull(info);
            Assert.NotNull(info.Version);
        }
    }
}
