#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using System.IO;
using System.Net;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Models;
using Google.Protobuf;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Newtonsoft.Json;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;
using ChainConfig = Autoswaprpc.ChainConfig;
using RpcException = Grpc.Core.RpcException;

namespace BTCPayServer.Plugins.Boltz;

[Route("plugins/{storeId}/Boltz")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class BoltzController(
    BoltzService boltzService,
    BTCPayNetworkProvider btcPayNetworkProvider)
    : Controller
{
    private BoltzClient? Boltz => Settings?.Client;
    private BoltzSettings? Settings => SetupSettings ?? SavedSettings;
    private BoltzSettings? SavedSettings => boltzService.GetSettings(CurrentStore.Id);

    private const string BtcPayName = "BTCPay";
    private const string BackUrl = "BackUrl";

    private StoreData CurrentStore => HttpContext.GetStoreData();

    private BoltzSettings? SetupSettings
    {
        get
        {
            var setupSettings = (string?)TempData.Peek("setupSettings");
            return String.IsNullOrEmpty(setupSettings)
                ? null
                : JsonConvert.DeserializeObject<BoltzSettings>(setupSettings);
        }
        set => TempData["setupSettings"] = JsonConvert.SerializeObject(value);
    }

    private ChainConfig? ChainSetup
    {
        get
        {
            var chainConfig = (string?)TempData.Peek("chainSetup");
            return String.IsNullOrEmpty(chainConfig)
                ? null
                : JsonParser.Default.Parse<ChainConfig>(chainConfig);
        }
        set => TempData["chainSetup"] = value is null ? null : JsonFormatter.Default.Format(value);
    }

    private LightningConfig? LightningSetup
    {
        get
        {
            var lightningConfig = (string?)TempData.Peek("lightningSetup");
            return String.IsNullOrEmpty(lightningConfig)
                ? null
                : JsonParser.Default.Parse<LightningConfig>(lightningConfig);
        }
        set => TempData["lightningSetup"] = value is null ? null : JsonFormatter.Default.Format(value);
    }

    [HttpGet("")]
    public IActionResult Index(string storeId)
    {
        return RedirectToAction(nameof(Status), new { storeId });
    }

    private void ClearSetup()
    {
        SetupSettings = null;
        ChainSetup = null;
        LightningSetup = null;
    }

    // GET
    [HttpGet("status")]
    public async Task<IActionResult> Status(string storeId)
    {
        ClearSetup();
        if (Settings?.Mode is null)
        {
            return RedirectSetup();
        }

        var data = new BoltzInfo();
        try
        {
            data.Info = await Boltz!.GetInfo();
            data.Swaps = await Boltz.ListSwaps();
            data.Stats = await Boltz.GetStats();
            data.Wallets = await Boltz.GetWallets(true);

            (data.Ln, data.Chain) = await Boltz.GetAutoSwapConfig();
            data.Status = await Boltz.GetAutoSwapStatus();
            data.Recommendations = await Boltz.GetAutoSwapRecommendations();
        }
        catch (RpcException e)
        {
            if (e.Status.Detail != "autoswap not configured")
            {
                TempData[WellKnownTempData.ErrorMessage] = e.Message;
            }
        }

        return View(data);
    }

    [HttpGet("configuration")]
    public async Task<IActionResult> Configuration(string storeId)
    {
        ClearSetup();
        if (Settings?.Mode == null)
        {
            return RedirectSetup();
        }

        var data = new BoltzConfig { Settings = Settings, };

        try
        {
            data.ExistingWallets = await GetExistingWallets(true);

            (data.Ln, data.Chain) = await Boltz.GetAutoSwapConfig();
            if (data.Ln is not null)
            {
                data.Ln.BudgetInterval = SecondsToDays(data.Ln.BudgetInterval);
            }

            if (data.Chain is not null)
            {
                data.Chain.BudgetInterval = SecondsToDays(data.Chain.BudgetInterval);
            }
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Error connecting to Boltz: " + e.Message;
        }

        return View(data);
    }

    private async Task<BoltzSettings> SetStandaloneWallet(BoltzSettings settings, string name)
    {
        var wallet = await Boltz!.GetWallet(name);
        settings.StandaloneWallet = new BoltzSettings.Wallet { Id = wallet.Id, Name = wallet.Name };
        return settings;
    }

    [HttpPost("configuration")]
    public async Task<IActionResult> Configuration(string storeId, BoltzConfig vm, string command = "")
    {
        switch (command)
        {
            case "BoltzSetLnConfig":
            {
                if (vm.Ln.Wallet != "")
                {
                    var wallet = await Boltz!.GetWallet(vm.Ln.Wallet);
                    vm.Ln.Currency = wallet.Currency;
                }
                else
                {
                    vm.Ln.Currency = Currency.Btc;
                }

                await SetLightningConfig(vm.Ln);
                TempData[WellKnownTempData.SuccessMessage] = "AutoSwap settings updated";
                break;
            }
            case "BoltzSetChainConfig":
            {
                var name = vm.Settings?.StandaloneWallet?.Name;
                if (name is not null)
                {
                    await boltzService.Set(CurrentStore.Id, await SetStandaloneWallet(Settings!, name));
                }

                if (vm.Chain is not null)
                {
                    await SetChainConfig(vm.Chain);
                }

                TempData[WellKnownTempData.SuccessMessage] = "AutoSwap settings updated";
                break;
            }
        }

        return RedirectToAction(nameof(Configuration), new { storeId });
    }

    [HttpGet("admin")]
    [Authorize(Policy = Policies.CanModifyServerSettings)]
    public async Task<IActionResult> Admin(string storeId, string? logFile, int offset = 0, bool download = false)
    {
        var vm = new AdminModel { Settings = Settings, };

        if (Boltz != null)
        {
            try
            {
                vm.Info = await Boltz.GetInfo();
            }
            catch (RpcException e)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Error connecting to Boltz: " + e.Status.Detail;
            }
        }

        var di = Directory.GetParent(boltzService.Daemon.LogFile);
        if (di is null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Could not load log files";
            return View(vm);
        }

        var fileName = Path.GetFileName(boltzService.Daemon.LogFile);

        // We are checking if "di" is null above yet accessing GetFiles on it, this could lead to an exception?
        var logFiles = di.GetFiles($"{Path.GetFileNameWithoutExtension(fileName)}*{Path.GetExtension(fileName)}");
        vm.Log.LogFileCount = logFiles.Length;
        vm.Log.LogFiles = logFiles
            .OrderBy(info => info.LastWriteTime)
            .Skip(offset)
            .Take(5)
            .ToList();
        vm.Log.LogFileOffset = offset;

        if (string.IsNullOrEmpty(logFile))
        {
            vm.Log.Log = boltzService.Daemon.RecentOutput;
        }
        else
        {
            try
            {
                var stream = System.IO.File.OpenRead(Path.Combine(di.FullName, logFile));
                if (download)
                {
                    return new FileStreamResult(stream, "text/plain")
                    {
                        FileDownloadName = logFile
                    };
                }
                vm.Log.Log = await new StreamReader(stream).ReadToEndAsync();
            }
            catch (Exception)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Could not load log file";
            }

        }

        return View(vm);
    }


    [HttpPost("admin")]
    [Authorize(Policy = Policies.CanModifyServerSettings)]
    public async Task<IActionResult> Admin(string storeId, AdminModel vm, string command = "")
    {
        switch (command)
        {
            case "Save":
            {
                var settings = vm.Settings;
                var validCreds = settings != null && settings.CredentialsPopulated();
                if (!validCreds)
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Please provide valid credentials";
                    return View(vm);
                }

                try
                {
                    await boltzService.Set(CurrentStore.Id, settings);
                }
                catch (Exception err)
                {
                    TempData[WellKnownTempData.ErrorMessage] = err.Message;
                    return View(vm);
                }

                TempData[WellKnownTempData.SuccessMessage] = "Boltz plugin successfully updated";
                break;
            }
            case "Update":
            {
                boltzService.Daemon.StartUpdate();
                TempData[WellKnownTempData.SuccessMessage] = "Boltz update initiated";
                break;
            }
            case "Clear":
            {
                await boltzService.Set(CurrentStore.Id, null);
                TempData[WellKnownTempData.SuccessMessage] = "Boltz plugin credentials cleared";
                break;
            }
        }

        return RedirectToAction(nameof(Admin), new { storeId });
    }

    [NonAction]
    private RedirectToActionResult RedirectSetup()
    {
        return RedirectToAction(nameof(SetupMode), new { storeId = CurrentStore.Id });
    }

    [HttpGet("setup/{mode?}")]
    //[Authorize(Policy = Policies.CanUseInternalLightningNode)]
    public async Task<IActionResult> SetupMode(ModeSetup vm, BoltzMode? mode, string storeId)
    {
        if (mode is null)
        {
            vm.ExistingSettings = SavedSettings;
            vm.ConnectedNode = CurrentStore.GetSupportedPaymentMethods(btcPayNetworkProvider)
                .OfType<LightningSupportedPaymentMethod>()
                .FirstOrDefault();
            vm.IsAdmin = User.IsInRole(Roles.ServerAdmin);
            vm.HasInternal = boltzService.InternalLightning is not null;
            vm.ConnectedInternal = boltzService.Daemon.Node is not null;
            if (vm.IsAdmin)
            {
                var store = await boltzService.GetRebalanceStore();
                if (store?.Id != CurrentStore.Id)
                {
                    vm.RebalanceStore = store;
                }
            }

            return View(vm);
        }

        try
        {
            SetupSettings = await boltzService.InitializeStore(storeId, mode.Value);
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Could not initialize store settings: " + e.Message;
        }

        return View(mode == BoltzMode.Rebalance ? "SetupRebalance" : "SetupStandalone", vm);
    }

    private async Task<List<ExistingWallet>> GetExistingWallets(bool allowReadonly, Currency? currency = null)
    {
        var response = await Boltz!.GetWallets(allowReadonly);
        var result = response.Wallets_.ToList()
            .FindAll(wallet => currency is null || wallet.Currency == currency)
            .Select(
                wallet => new ExistingWallet
                {
                    Balance = wallet.Balance.Total,
                    IsReadonly = wallet.Readonly,
                    Name = wallet.Name,
                    Currency = wallet.Currency,
                }
            ).ToList();

        if (currency != Currency.Lbtc)
        {
            var derivation = CurrentStore.GetDerivationSchemeSettings(btcPayNetworkProvider, "BTC");
            if (derivation is not null && (derivation.IsHotWallet || allowReadonly))
            {
                var balance = await boltzService.BtcWallet.GetBalance(derivation.AccountDerivation);
                result.Add(new ExistingWallet
                {
                    Name = BtcPayName,
                    IsBtcpay = true,
                    IsReadonly = !derivation.IsHotWallet,
                    Currency = Currency.Btc,
                    Balance = (ulong)(balance.Total.GetValue(boltzService.BtcNetwork) *
                                      (decimal)MoneyUnit.BTC)
                });
            }
        }

        return result;
    }

    [HttpGet("setup/wallet/{flow}/{currency?}")]
    public async Task<IActionResult> SetupWallet(WalletSetup vm)
    {
        if (Boltz != null)
        {
            vm.Currency = vm.Flow switch
            {
                WalletSetupFlow.Standalone => Currency.Lbtc,
                WalletSetupFlow.Chain => Currency.Btc,
                _ => vm.Currency
            };
            if (vm.Currency is null)
            {
                return View("SetupCurrency", vm);
            }

            try
            {
                vm.ExistingWallets = vm.Flow == WalletSetupFlow.Manual
                    ? new()
                    : await GetExistingWallets(vm.AllowReadonly, vm.Currency);
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Could not fetch existing wallets: " + e.Message;
                return RedirectSetup();
            }

            return View(vm);
        }

        return RedirectSetup();
    }

    [HttpPost("setup/wallet/{flow}/{currency}")]
    public async Task<RedirectToActionResult> SetupWallet(WalletSetup vm, string? walletName)
    {
        if (Boltz != null)
        {
            try
            {
                walletName ??= String.Empty;
                switch (vm.Flow)
                {
                    case WalletSetupFlow.Standalone:
                        SetupSettings = await SetStandaloneWallet(SetupSettings!, walletName);
                        return RedirectToAction(nameof(SetupChain), new { storeId = vm.StoreId });
                    case WalletSetupFlow.Lightning:
                        LightningSetup = new LightningConfig
                        {
                            Currency = vm.Currency!.Value,
                            SwapType = vm.SwapType ?? String.Empty,
                            Wallet = walletName,
                        };
                        return RedirectToAction(nameof(SetupThresholds), new { storeId = vm.StoreId });
                    case WalletSetupFlow.Chain:
                        ChainSetup = new ChainConfig(ChainSetup) { ToWallet = walletName };
                        return RedirectToAction(nameof(SetupBudget),
                            new { storeId = vm.StoreId, swapperType = SwapperType.Chain });
                    case WalletSetupFlow.Manual:
                        return RedirectToAction(nameof(Configuration), new { storeId = vm.StoreId });
                }
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Could not setup wallet: " + e.Message;
                return RedirectSetup();
            }
        }

        return RedirectSetup();
    }

    [HttpGet("setup/wallet/{flow}/{currency}/create")]
    public IActionResult CreateWallet(WalletSetup vm, string storeId)
    {
        if (Boltz != null)
        {
            return View(vm);
        }

        return RedirectSetup();
    }

    [HttpPost("setup/wallet/{flow}/{currency}/create")]
    public async Task<IActionResult> CreateWallet(WalletSetup vm)
    {
        if (Boltz != null)
        {
            try
            {
                vm.WalletParams.Currency = vm.Currency!.Value;
                if (vm.ImportMethod is null)
                {
                    var response = await Boltz.CreateWallet(vm.WalletParams);
                    var next = await SetupWallet(vm, response.Wallet.Name);
                    return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
                    {
                        Mnemonic = response.Mnemonic,
                        ReturnUrl = Url.Action(next.ActionName, next.RouteValues),
                        IsStored = true,
                    });
                }

                var wallet = await Boltz.ImportWallet(vm.WalletParams, vm.WalletCredentials);
                return await SetupWallet(vm, wallet.Name);
            }
            catch (RpcException e)
            {
                TempData[WellKnownTempData.ErrorMessage] = e.Status.Detail;
                return RedirectToAction(vm.ImportMethod is null ? "CreateWallet" : "ImportWallet", vm.RouteData);
            }
        }

        return RedirectSetup();
    }

    [HttpGet("setup/wallet/{flow}/{currency}/import")]
    public IActionResult ImportWallet(WalletSetup vm)
    {
        if (Boltz != null)
        {
            if (!vm.AllowReadonly)
            {
                vm.ImportMethod = WalletImportMethod.Mnemonic;
            }

            if (vm.ImportMethod == null)
            {
                return View("ImportWalletOptions", vm);
            }

            return View("CreateWallet", vm);
        }

        return RedirectSetup();
    }

    [HttpGet("setup/thresholds")]
    public IActionResult SetupThresholds()
    {
        if (LightningSetup is not null)
        {
            var vm = new BalanceSetup { Ln = LightningSetup };
            vm.Ln.InboundBalancePercent = 25;
            vm.Ln.OutboundBalancePercent = 25;
            ViewData[BackUrl] = Url.Action(nameof(SetupWallet),
                new { flow = WalletSetupFlow.Lightning, currency = vm.Ln.Currency, storeId = CurrentStore.Id });
            return View(vm);
        }

        return RedirectSetup();
    }

    [HttpPost("setup/thresholds")]
    public IActionResult SetupThresholds(BalanceSetup vm, string storeId)
    {
        if (LightningSetup is not null)
        {
            var setup = LightningSetup;
            setup.MergeFrom(vm.Ln);
            LightningSetup = setup;
            return RedirectToAction(nameof(SetupBudget), new
            {
                storeId, swapperType = SwapperType.Ln
            });
        }

        return RedirectSetup();
    }

    [HttpGet("setup/{swapperType}/budget")]
    public IActionResult SetupBudget(BudgetSetup vm)
    {
        if (Boltz != null)
        {
            vm.Budget = 100_000;
            vm.BudgetIntervalDays = 7;
            vm.MaxFeePercent = 1;
            if (vm.SwapperType == SwapperType.Chain)
            {
                ViewData[BackUrl] = Url.Action(nameof(SetupWallet),
                    new { storeId = CurrentStore.Id, Flow = WalletSetupFlow.Chain });
            }

            return View(vm);
        }

        return RedirectSetup();
    }

    [HttpPost("setup/{swapperType}/budget")]
    public IActionResult SetupBudget(BudgetSetup vm, string storeId)
    {
        if (Boltz != null)
        {
            if (vm.SwapperType == SwapperType.Ln)
            {
                LightningSetup = new LightningConfig(LightningSetup)
                {
                    Budget = vm.Budget,
                    BudgetInterval = vm.BudgetIntervalDays,
                    MaxFeePercent = vm.MaxFeePercent,
                };
                if (LightningSetup.Currency == Currency.Lbtc)
                {
                    return RedirectToAction(nameof(SetupChain), new { storeId });
                }
            }
            else
            {
                ChainSetup = new ChainConfig(ChainSetup)
                {
                    Budget = vm.Budget,
                    BudgetInterval = vm.BudgetIntervalDays,
                    MaxFeePercent = vm.MaxFeePercent
                };
            }

            return RedirectToAction(nameof(Enable), new { storeId });
        }

        return RedirectSetup();
    }

    async Task SetLightningConfig(LightningConfig config, IEnumerable<string>? paths = null)
    {
        if (config is { Wallet: "", StaticAddress: "" })
        {
            config.StaticAddress = await boltzService.GenerateNewAddress(CurrentStore);
            paths = paths?.Append("static_address");
        }

        config.BudgetInterval = DaysToSeconds(config.BudgetInterval);

        await Boltz!.UpdateAutoSwapLightningConfig(config, paths);
    }

    private static ulong DaysToSeconds(ulong days)
    {
        return (ulong)TimeSpan.FromDays(days).TotalSeconds;
    }

    private static ulong SecondsToDays(ulong seconds)
    {
        return (ulong)TimeSpan.FromSeconds(seconds).TotalDays;
    }

    async Task SetChainConfig(ChainConfig config)
    {
        if (Settings!.StandaloneWallet is not null)
        {
            config.FromWallet = Settings.StandaloneWallet.Name;
        }
        else
        {
            var ln = await Boltz!.GetLightningConfig();
            if (ln is not null)
            {
                config.FromWallet = ln.Wallet;
            }
        }

        if (config is { ToWallet: "", ToAddress: "" })
        {
            config.ToAddress = await boltzService.GenerateNewAddress(CurrentStore);
        }

        config.BudgetInterval = DaysToSeconds(config.BudgetInterval);

        await Boltz!.UpdateAutoSwapChainConfig(config);
    }

    [HttpGet("setup/chain")]
    public async Task<IActionResult> SetupChain(string storeId)
    {
        if (Boltz != null)
        {
            var info = await Boltz.GetPairInfo(new Pair { From = Currency.Lbtc, To = Currency.Btc }, SwapType.Chain);
            var vm = new ChainSetup
            {
                // TODO: remove buffer once proper sweep is implemented
                MaxBalance = 10_000_000,
                PairInfo = info
            };

            if (SetupSettings?.Mode == BoltzMode.Standalone)
            {
                ViewData[BackUrl] = Url.Action(nameof(SetupWallet),
                    new { flow = WalletSetupFlow.Standalone, storeId });
            }

            return View(vm);
        }

        return RedirectSetup();
    }

    [HttpPost("setup/chain")]
    public IActionResult SetupChain(ChainConfig vm, string storeId, string command)
    {
        if (Boltz != null)
        {
            if (command == "Skip")
            {
                ChainSetup = null;
                return RedirectToAction(nameof(Enable), new { storeId });
            }

            ChainSetup = vm;
            return RedirectToAction(nameof(SetupWallet),
                new { storeId, flow = WalletSetupFlow.Chain, currency = Currency.Btc });
        }

        return RedirectSetup();
    }

    [HttpGet("setup/enable")]
    public async Task<IActionResult> Enable(string storeId)
    {
        if (Boltz != null)
        {
            if (LightningSetup is not null)
            {
                await SetLightningConfig(LightningSetup);
            }

            if (ChainSetup is not null)
            {
                await SetChainConfig(ChainSetup);
            }
            else
            {
                await Boltz.ResetChainConfig();
            }

            await boltzService.Set(CurrentStore.Id, Settings!);

            if (await Boltz.IsAutoSwapConfigured())
            {
                var vm = await Boltz.GetAutoSwapRecommendations();
                return View(vm);
            }

            return RedirectToAction(nameof(Status),
                new { storeId });
        }

        return RedirectSetup();
    }

    [HttpPost("setup/enable")]
    public async Task<IActionResult> Enable(string storeId, string command)
    {
        if (Boltz != null)
        {
            if (command == "Enable")
            {
                await Boltz.EnableAutoSwap();
                TempData[WellKnownTempData.SuccessMessage] = "AutoSwap enabled";
            }

            return RedirectToAction(nameof(Status),
                new { storeId });
        }

        return RedirectSetup();
    }
}