using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Services;
using BTCPayServer.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    public record BoltzRegtestLndInvoice(string PaymentRequest, string PaymentHash);

    public static class BoltzTestUtils
    {
        private const string DefaultBoltzRegtestLndUrl = "https://127.0.0.1:8081/";

        private static async Task<bool> WaitForAdminClient(BoltzService boltzService, int timeoutSeconds)
        {
            for (var second = 0; second < timeoutSeconds; second++)
            {
                if (boltzService.AdminClient is not null && boltzService.Daemon.Running)
                {
                    return true;
                }

                await Task.Delay(1000);
            }

            return boltzService.AdminClient is not null && boltzService.Daemon.Running;
        }

        public static Task<BoltzService> GetBoltzService(this ServerTester serverTester) =>
            Task.FromResult(serverTester.PayTester.GetService<BoltzService>());

        public static Task<BoltzDaemon> GetBoltzDaemon(this ServerTester serverTester) =>
            Task.FromResult(serverTester.PayTester.GetService<BoltzDaemon>());

        public static async Task EnsureBoltzServerReady(this ServerTester serverTester)
        {
            var boltzService = await serverTester.GetBoltzService();
            await boltzService.SetServerSettings(CreateTestBoltzServerSettings());
            if (!await WaitForAdminClient(boltzService, timeoutSeconds: 45))
            {
                await boltzService.SetServerSettings(CreateTestBoltzServerSettings());
                Assert.True(await WaitForAdminClient(boltzService, timeoutSeconds: 30));
            }
        }

        private static string GetBoltzRegtestPath()
        {
            var configuredPath = Environment.GetEnvironmentVariable("BOLTZ_REGTEST");
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = Environment.GetEnvironmentVariable("BOLTZ_REGTEST_PATH");
            }

            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            var solutionDirectory = TestUtils.TryGetSolutionDirectoryInfo();
            var candidates = new[]
            {
                Path.Combine(solutionDirectory.FullName, "regtest"),
                solutionDirectory.Parent is null
                    ? string.Empty
                    : Path.Combine(solutionDirectory.Parent.FullName, "regtest")
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return candidates[0];
        }

        public static string GetBoltzRegtestLndConnectionString()
        {
            var explicitConnectionString = Environment.GetEnvironmentVariable("TESTS_BOLTZ_LND_CONNECTION");
            if (!string.IsNullOrWhiteSpace(explicitConnectionString))
            {
                return explicitConnectionString;
            }

            var regtestPath = GetBoltzRegtestPath();
            var macaroonPath = Path.Combine(regtestPath, "data", "lnd1", "data", "chain", "bitcoin", "regtest",
                "admin.macaroon");
            var certPath = Path.Combine(regtestPath, "data", "lnd1", "tls.cert");
            Assert.True(File.Exists(macaroonPath), $"Could not find Boltz regtest LND macaroon at {macaroonPath}");
            Assert.True(File.Exists(certPath), $"Could not find Boltz regtest LND TLS certificate at {certPath}");

            var serverUrl = Environment.GetEnvironmentVariable("TESTS_BOLTZ_LND_URL") ?? DefaultBoltzRegtestLndUrl;
            return
                $"type=lnd-rest;server={serverUrl};macaroonfilepath={macaroonPath};certfilepath={certPath};allowinsecure=true";
        }

        public static ILightningClient GetBoltzRegtestLndClient(this ServerTester serverTester)
        {
            var lightningClientFactory = serverTester.PayTester.GetService<LightningClientFactoryService>();
            var network = serverTester.NetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
            return lightningClientFactory.Create(GetBoltzRegtestLndConnectionString(), network);
        }

        private static async Task<string> RunBoltzScriptsCommand(params string[] commandArguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.StartInfo.ArgumentList.Add("exec");
            process.StartInfo.ArgumentList.Add("boltz-scripts");
            foreach (var commandArgument in commandArguments)
            {
                process.StartInfo.ArgumentList.Add(commandArgument);
            }

            Assert.True(process.Start(), $"Could not start docker command: {string.Join(' ', commandArguments)}");

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            Assert.True(
                process.ExitCode == 0,
                $"Failed to run boltz-scripts command '{string.Join(' ', commandArguments)}'. Exit code: {process.ExitCode}{Environment.NewLine}stdout: {standardOutput}{Environment.NewLine}stderr: {standardError}");

            return standardOutput;
        }

        private static Task<string> RunBoltzRegtestLncliCommand(params string[] commandArguments)
        {
            var arguments = new string[5 + commandArguments.Length];
            arguments[0] = "lncli";
            arguments[1] = "--network";
            arguments[2] = "regtest";
            arguments[3] = "--rpcserver=lnd-1:10009";
            arguments[4] = "--lnddir=/root/.lnd-1";
            Array.Copy(commandArguments, 0, arguments, 5, commandArguments.Length);
            return RunBoltzScriptsCommand(arguments);
        }

        public static async Task<BoltzRegtestLndInvoice> CreateBoltzRegtestLndInvoice(this ServerTester serverTester,
            long amountSats, string description = null, TimeSpan? expiry = null)
        {
            ArgumentNullException.ThrowIfNull(serverTester);
            description ??= $"boltz-test-{Guid.NewGuid():N}";
            expiry ??= TimeSpan.FromDays(40);

            var standardOutput = await RunBoltzRegtestLncliCommand(
                "addinvoice",
                $"--amt={amountSats}",
                $"--memo={description}",
                $"--expiry={(long)expiry.Value.TotalSeconds}");

            var invoice = JObject.Parse(standardOutput);
            return new BoltzRegtestLndInvoice(
                invoice["payment_request"]!.Value<string>()!,
                invoice["r_hash"]!.Value<string>()!);
        }

        public static async Task<JObject> GetBoltzRegtestLndInvoice(this ServerTester serverTester, string paymentHash)
        {
            ArgumentNullException.ThrowIfNull(serverTester);
            var standardOutput = await RunBoltzRegtestLncliCommand(
                "lookupinvoice",
                paymentHash);
            return JObject.Parse(standardOutput);
        }

        public static async Task PayWithBoltzRegtestLnd(this ServerTester serverTester, string bolt11)
        {
            ArgumentNullException.ThrowIfNull(serverTester);
            var normalizedBolt11 = bolt11.Replace("lightning:", string.Empty, StringComparison.OrdinalIgnoreCase);
            await RunBoltzRegtestLncliCommand(
                "payinvoice",
                "--force",
                normalizedBolt11);
        }

        public static async Task FundBoltzStandaloneWallet(this ServerTester serverTester, string storeId,
            decimal amountLbtc = 0.01m)
        {
            var boltzService = await serverTester.GetBoltzService();
            var settings = boltzService.GetSettings(storeId);
            Assert.NotNull(settings?.StandaloneWallet);

            var client = boltzService.GetClient(storeId);
            Assert.NotNull(client);

            var receiveResponse = await client!.WalletReceive(settings!.StandaloneWallet!.Id);
            Assert.False(string.IsNullOrWhiteSpace(receiveResponse.Address));

            await RunBoltzScriptsCommand(
                "elements-cli",
                "-rpcconnect=elementsd",
                "-rpcwallet=regtest",
                "sendtoaddress",
                receiveResponse.Address,
                amountLbtc.ToString(CultureInfo.InvariantCulture));

            await RunBoltzScriptsCommand(
                "elements-cli",
                "-rpcconnect=elementsd",
                "-rpcwallet=regtest",
                "-generate",
                "1");

            await TestUtils.EventuallyAsync(async () =>
            {
                var wallet = await client.GetWallet(settings.StandaloneWallet.Id);
                Assert.NotNull(wallet.Balance);
                Assert.True(wallet.Balance.Confirmed > 0);
            }, TestUtils.TestTimeout);
        }

        public static BoltzServerSettings CreateTestBoltzServerSettings()
        {
            return new BoltzServerSettings
            {
                AllowTenants = true
            };
        }

        public static async Task<TestAccount> CreateTestStore(this ServerTester serverTester, string storeName = "Test Store")
        {
            await serverTester.StartAsync();
            await Task.Delay(3000);
            var account = serverTester.NewAccount();
            account.GrantAccess();
            await account.CreateStoreAsync();
            return account;
        }

        public static async Task SetupBoltzForStore(this ServerTester serverTester, string storeId)
        {
            await serverTester.EnsureBoltzServerReady();
            var boltzService = await serverTester.GetBoltzService();

            BoltzSettings settings = null;
            BoltzClient client = null;
            const int maxAttempts = 30;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (boltzService.AdminClient is null)
                {
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(2000);
                        continue;
                    }
                }

                try
                {
                    settings = await boltzService.InitializeStore(storeId);
                    client = boltzService.Daemon.GetClient(settings);
                    if (client is not null)
                    {
                        break;
                    }
                }
                catch (NullReferenceException) when (attempt < maxAttempts)
                {
                    // Boltz daemon/admin client can still be initializing right after test server startup.
                }
                catch (InvalidOperationException) when (attempt < maxAttempts)
                {
                    // Boltz daemon/admin client can still be initializing right after test server startup.
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(2000);
                }
            }

            Assert.NotNull(settings);
            Assert.NotNull(client);
            var wallet = await client.CreateWallet(new Boltzrpc.WalletParams { Name = "test", Currency = Boltzrpc.Currency.Lbtc });
            settings.SetStandaloneWallet(wallet.Wallet);
            await boltzService.Set(storeId, settings);
            Console.WriteLine(settings.ToString());
            Assert.True(boltzService.StoreConfigured(storeId));
        }

        public static void AssertBoltzSettingsEqual(BoltzSettings expected, BoltzSettings actual)
        {
            Assert.Equal(expected.GrpcUrl, actual.GrpcUrl);
            Assert.Equal(expected.CertFilePath, actual.CertFilePath);
            Assert.Equal(expected.Macaroon, actual.Macaroon);
            Assert.Equal(expected.TenantId, actual.TenantId);
        }

        public static void AssertBoltzServerSettingsEqual(BoltzServerSettings expected, BoltzServerSettings actual)
        {
            Assert.Equal(expected.AllowTenants, actual.AllowTenants);
        }
    }
}
