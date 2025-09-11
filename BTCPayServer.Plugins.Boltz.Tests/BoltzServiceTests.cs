using System;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Fast", "Fast")]
    public class BoltzServiceTests : BoltzTestBase
    {
        public BoltzServiceTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact]
        public async Task CanInitializeBoltzService()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            await serverTester.StartAsync();
            
            var boltzService = serverTester.PayTester.GetService<BoltzService>();
            Assert.NotNull(boltzService);
        }

        [Fact]
        public async Task CanGetBoltzSettings()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(storeId);
            
            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(storeId);
            
            Assert.NotNull(settings);
            Assert.Equal(BoltzMode.Standalone, settings.Mode);
        }

        [Fact]
        public async Task CanSetBoltzSettings()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            
            var boltzService = await serverTester.GetBoltzService();
            var settings = BoltzTestUtils.CreateTestBoltzSettings(BoltzMode.Standalone);
            
            await boltzService.Set(storeId, settings);
            
            var retrievedSettings = boltzService.GetSettings(storeId);
            Assert.NotNull(retrievedSettings);
            BoltzTestUtils.AssertBoltzSettingsEqual(settings, retrievedSettings);
        }

        [Fact]
        public async Task CanCheckStoreConfigured()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            
            var boltzService = await serverTester.GetBoltzService();
            
            // Initially not configured
            Assert.False(boltzService.StoreConfigured(storeId));
            
            // Configure the store
            await serverTester.SetupBoltzForStore(storeId);
            
            // Now should be configured
            Assert.True(boltzService.StoreConfigured(storeId));
        }

        [Fact]
        public async Task CanRemoveBoltzSettings()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(storeId);
            
            var boltzService = await serverTester.GetBoltzService();
            
            // Should be configured initially
            Assert.True(boltzService.StoreConfigured(storeId));
            
            // Remove settings
            await boltzService.Set(storeId, null);
            
            // Should no longer be configured
            Assert.False(boltzService.StoreConfigured(storeId));
        }

        [Fact]
        public async Task CanSetServerSettings()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            await serverTester.StartAsync();
            
            var boltzService = serverTester.PayTester.GetService<BoltzService>();
            var serverSettings = BoltzTestUtils.CreateTestBoltzServerSettings();
            
            await boltzService.SetServerSettings(serverSettings);
            
            Assert.NotNull(boltzService.ServerSettings);
            BoltzTestUtils.AssertBoltzServerSettingsEqual(serverSettings, boltzService.ServerSettings);
        }

        [Fact]
        public async Task CanGenerateNewAddress()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            await serverTester.StartAsync();
            
            var boltzService = serverTester.PayTester.GetService<BoltzService>();
            var store = await serverTester.PayTester.StoreRepository.FindStore(storeId);
            
            // This will throw if no BTC wallet is configured, which is expected
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await boltzService.GenerateNewAddress(store);
            });
        }

        [Fact]
        public async Task CanInitializeStore()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            
            var boltzService = await serverTester.GetBoltzService();
            var settings = await boltzService.InitializeStore(storeId, BoltzMode.Standalone);
            
            Assert.NotNull(settings);
            Assert.Equal(BoltzMode.Standalone, settings.Mode);
            Assert.NotNull(settings.GrpcUrl);
            Assert.NotNull(settings.CertFilePath);
        }

        [Fact]
        public async Task CanGetLightningClient()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(storeId);
            
            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(storeId);
            
            // Without standalone wallet configured, should return null
            var lightningClient = boltzService.GetLightningClient(settings);
            Assert.Null(lightningClient);
        }

        [Fact]
        public async Task CanCheckIsStoreReadonly()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(storeId);
            
            var boltzService = await serverTester.GetBoltzService();
            
            // Default should be false (not readonly)
            Assert.False(boltzService.IsStoreReadonly(storeId));
        }
    }
}
