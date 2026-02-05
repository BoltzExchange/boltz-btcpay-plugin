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
        }

        [Fact]
        public async Task CanAccessStatusPage()
        {
            using var serverTester = CreateServerTesterWithBoltz("CanAccessStatusPage");
            var account = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account.StoreId);
            var boltzService = await serverTester.GetBoltzService();
            Assert.NotNull(boltzService.GetSettings(account.StoreId));

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, account.StoreId, account.IsAdmin);
            Assert.NotNull(controller);
            Assert.NotNull(controller.Settings);
            var result = await controller.Status(account.StoreId);

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<BoltzInfo>(viewResult.Model);
            Assert.NotNull(model.Info);
            Assert.NotNull(model.Stats);
            Assert.NotNull(model.StandaloneWallet);
        }
    }
}
