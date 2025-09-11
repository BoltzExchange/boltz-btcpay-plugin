using System;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Integration", "Integration")]
    public class BoltzIntegrationTests : BoltzTestBase
    {
        public BoltzIntegrationTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact]
        public async Task CanSetupCompleteBoltzWorkflow()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            
            var boltzService = await serverTester.GetBoltzService();
            
            // Step 1: Initialize store with Boltz
            var settings = await boltzService.InitializeStore(storeId, BoltzMode.Standalone);
            Assert.NotNull(settings);
            Assert.Equal(BoltzMode.Standalone, settings.Mode);
            
            // Step 2: Set the settings
            await boltzService.Set(storeId, settings);
            
            // Step 3: Verify store is configured
            Assert.True(boltzService.StoreConfigured(storeId));
            
            // Step 4: Retrieve settings and verify
            var retrievedSettings = boltzService.GetSettings(storeId);
            Assert.NotNull(retrievedSettings);
            BoltzTestUtils.AssertBoltzSettingsEqual(settings, retrievedSettings);
            
            // Step 5: Set server settings
            var serverSettings = BoltzTestUtils.CreateTestBoltzServerSettings();
            await boltzService.SetServerSettings(serverSettings);
            
            // Step 6: Verify server settings
            Assert.NotNull(boltzService.ServerSettings);
            BoltzTestUtils.AssertBoltzServerSettingsEqual(serverSettings, boltzService.ServerSettings);
        }

        [Fact]
        public async Task CanHandleMultipleStores()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            
            // Create multiple stores
            var store1Id = await serverTester.CreateTestStore("Store 1");
            var store2Id = await serverTester.CreateTestStore("Store 2");
            
            var boltzService = await serverTester.GetBoltzService();
            
            // Setup first store
            var settings1 = await boltzService.InitializeStore(store1Id, BoltzMode.Standalone);
            await boltzService.Set(store1Id, settings1);
            
            // Setup second store with different mode
            var settings2 = await boltzService.InitializeStore(store2Id, BoltzMode.Rebalance);
            await boltzService.Set(store2Id, settings2);
            
            // Verify both stores are configured
            Assert.True(boltzService.StoreConfigured(store1Id));
            Assert.True(boltzService.StoreConfigured(store2Id));
            
            // Verify settings are different
            var retrievedSettings1 = boltzService.GetSettings(store1Id);
            var retrievedSettings2 = boltzService.GetSettings(store2Id);
            
            Assert.Equal(BoltzMode.Standalone, retrievedSettings1.Mode);
            Assert.Equal(BoltzMode.Rebalance, retrievedSettings2.Mode);
        }

        [Fact]
        public async Task CanRemoveAndReconfigureStore()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            
            var boltzService = await serverTester.GetBoltzService();
            
            // Initial setup
            var settings = await boltzService.InitializeStore(storeId, BoltzMode.Standalone);
            await boltzService.Set(storeId, settings);
            Assert.True(boltzService.StoreConfigured(storeId));
            
            // Remove configuration
            await boltzService.Set(storeId, null);
            Assert.False(boltzService.StoreConfigured(storeId));
            
            // Reconfigure with different mode
            var newSettings = await boltzService.InitializeStore(storeId, BoltzMode.Rebalance);
            await boltzService.Set(storeId, newSettings);
            Assert.True(boltzService.StoreConfigured(storeId));
            
            var retrievedSettings = boltzService.GetSettings(storeId);
            Assert.Equal(BoltzMode.Rebalance, retrievedSettings.Mode);
        }

        [Fact]
        public async Task CanHandleStoreWithoutBoltzConfiguration()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var storeId = await serverTester.CreateTestStore();
            
            var boltzService = await serverTester.GetBoltzService();
            
            // Store should not be configured initially
            Assert.False(boltzService.StoreConfigured(storeId));
            Assert.Null(boltzService.GetSettings(storeId));
            Assert.False(boltzService.IsStoreReadonly(storeId));
            Assert.Null(boltzService.GetLightningClient(null));
        }

        [Fact]
        public async Task CanHandleNullStoreId()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            await serverTester.StartAsync();
            
            var boltzService = serverTester.PayTester.GetService<BoltzService>();
            
            // All methods should handle null store ID gracefully
            Assert.False(boltzService.StoreConfigured(null));
            Assert.Null(boltzService.GetSettings(null));
            Assert.False(boltzService.IsStoreReadonly(null));
            Assert.Null(boltzService.GetClient(null));
        }

        [Fact]
        public async Task CanHandleServerSettingsChanges()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            await serverTester.StartAsync();
            
            var boltzService = serverTester.PayTester.GetService<BoltzService>();
            
            // Initial server settings
            var initialSettings = BoltzTestUtils.CreateTestBoltzServerSettings();
            initialSettings.ConnectNode = false;
            initialSettings.AllowTenants = true;
            
            await boltzService.SetServerSettings(initialSettings);
            Assert.False(boltzService.ServerSettings.ConnectNode);
            Assert.True(boltzService.ServerSettings.AllowTenants);
            
            // Change server settings
            var updatedSettings = BoltzTestUtils.CreateTestBoltzServerSettings();
            updatedSettings.ConnectNode = true;
            updatedSettings.AllowTenants = false;
            
            await boltzService.SetServerSettings(updatedSettings);
            Assert.True(boltzService.ServerSettings.ConnectNode);
            Assert.False(boltzService.ServerSettings.AllowTenants);
        }

        [Fact]
        public async Task CanHandleBoltzDaemonIntegration()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            await serverTester.StartAsync();
            
            var boltzService = serverTester.PayTester.GetService<BoltzService>();
            var boltzDaemon = serverTester.PayTester.GetService<BoltzDaemon>();
            
            Assert.NotNull(boltzService.Daemon);
            Assert.NotNull(boltzDaemon);
            Assert.Same(boltzDaemon, boltzService.Daemon);
        }
    }
}
