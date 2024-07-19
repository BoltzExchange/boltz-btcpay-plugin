#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Services.Stores;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Newtonsoft.Json;
using ChainConfig = Autoswaprpc.ChainConfig;

namespace BTCPayServer.Plugins.Boltz;

[Route("plugins/{storeId}/Boltz")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class BoltzController(
    BoltzService boltzService,
    StoreRepository storeRepository,
    ExplorerClientProvider explorerProvider,
    BTCPayNetworkProvider btcPayNetworkProvider)
    : Controller
{
    private BoltzClient? Boltz => boltzService.GetClient(CurrentStore.Id);
    private BoltzSettings? Settings => boltzService.GetSettings(CurrentStore.Id);
    private const string BtcPayName = "BTCPay";

    private StoreData CurrentStore => HttpContext.GetStoreData();

    private ChainSetup ChainSetup
    {
        get
        {
            if (TempData.TryGetValue("chainSetup", out var chainConfig))
            {
                return JsonConvert.DeserializeObject<ChainSetup>((string)chainConfig!)!;
            }

            return new ChainSetup();
        }
        set => TempData["chainSetup"] = JsonConvert.SerializeObject(value);
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string storeId)
    {
        return RedirectToAction(Boltz is null ? nameof(SetupMode) : nameof(Info), new { storeId });
    }

    // GET
    [HttpGet("info")]
    public async Task<IActionResult> Info(string storeId)
    {
        if (Boltz == null)
        {
            return RedirectGetStarted();
        }

        var data = new BoltzInfo();
        try
        {
            data.Info = await Boltz.GetInfo();
            data.Swaps = await Boltz.ListSwaps();
            data.Wallets = await Boltz.GetWallets(true);
            (data.Ln, data.Chain) = await Boltz.GetAutoSwapConfig();
            data.Status = await Boltz.GetAutoSwapStatus();
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

    /*
    [HttpGet("{storeId}/dashboard/boltz/stats")]
    public async Task<IActionResult> Stats()
    {
        var vm = new BoltzStats();
        if (_boltz != null)
        {
            vm.Wallet = await _boltz.GetAutoSwapWallet();
        }
        return ViewComponent("Boltz/Stats", new { vm } );
    }
    */


    [HttpGet("configuration")]
    public async Task<IActionResult> Configuration(string storeId)
    {
        if (Boltz == null)
        {
            return RedirectGetStarted();
        }

        var data = new BoltzConfig
        {
            Settings = Settings,
        };

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

    [HttpPost("configuration")]
    public async Task<IActionResult> Configuration(string storeId, BoltzConfig vm, string command = "")
    {
        switch (command)
        {
            case "BoltzSetLnConfig":
            {
                await SetLightningConfig(vm.Ln);
                TempData[WellKnownTempData.SuccessMessage] = "AutoSwap settings updated";
                break;
            }
            case "BoltzSetChainConfig":
            {
                await SetChainConfig(vm.Chain);
                TempData[WellKnownTempData.SuccessMessage] = "AutoSwap settings updated";
                break;
            }
            case "BoltzSetStandaloneWallet":
            {
                await SetStandaloneWallet(vm.Settings.StandaloneWallet?.Name);
                TempData[WellKnownTempData.SuccessMessage] = "Standalone Wallet updated";
                break;
            }
        }

        return RedirectToAction(nameof(Configuration), new { storeId });
    }

    [HttpGet("admin")]
    [Authorize(Policy = Policies.CanModifyServerSettings)]
    public async Task<IActionResult> Admin(string storeId)
    {
        var data = new BoltzConnection()
        {
            Settings = boltzService.GetSettings(CurrentStore.Id),
        };

        if (Boltz != null)
        {
            try
            {
                data.Info = await Boltz.GetInfo();
            }
            catch (RpcException e)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Error connecting to Boltz: " + e.Status.Detail;
            }
        }

        return View(data);
    }


    [HttpPost("admin")]
    [Authorize(Policy = Policies.CanModifyServerSettings)]
    public async Task<IActionResult> Admin(string storeId, BoltzConnection vm, string command = "")
    {
        switch (command)
        {
            case "BoltzSaveCredentials":
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
                    await Boltz.GetInfo();
                    SetLightning();
                }
                catch (Exception err)
                {
                    TempData[WellKnownTempData.ErrorMessage] = err.Message;
                    return View(vm);
                }

                TempData[WellKnownTempData.SuccessMessage] = "Boltz plugin successfully updated";
                break;
            }
            case "BoltzClearCredentials":
            {
                await boltzService.Set(CurrentStore.Id, null);
                TempData[WellKnownTempData.SuccessMessage] = "Boltz plugin credentials cleared";
                break;
            }
        }

        return RedirectToAction(nameof(Admin), new { storeId });
    }


    private async void SetLightning()
    {
        var cryptoCode = "BTC";
        var network = explorerProvider.GetNetwork(cryptoCode);
        var paymentMethodId = new PaymentMethodId(network.CryptoCode, PaymentTypes.LightningLike);
        var paymentMethod = new LightningSupportedPaymentMethod
        {
            CryptoCode = paymentMethodId.CryptoCode
        };

        var settings = boltzService.GetSettings(CurrentStore.Id);
        if (settings is null) return;
        var lightningClient = boltzService.GetLightningClient(settings)!;
        paymentMethod.SetLightningUrl(lightningClient);

        CurrentStore.SetSupportedPaymentMethod(paymentMethodId, paymentMethod);

        var lnurl = new PaymentMethodId(cryptoCode, PaymentTypes.LNURLPay);
        CurrentStore.SetSupportedPaymentMethod(lnurl, new LNURLPaySupportedPaymentMethod()
        {
            CryptoCode = cryptoCode,
            UseBech32Scheme = true,
            LUD12Enabled = false
        });

        await storeRepository.UpdateStore(CurrentStore);
    }


    [NonAction]
    public RedirectToActionResult RedirectGetStarted()
    {
        return RedirectToAction(nameof(SetupMode), new { storeId = CurrentStore.Id });
    }


    [HttpGet("setup/{mode?}")]
    //[Authorize(Policy = Policies.CanUseInternalLightningNode)]
    public async Task<IActionResult> SetupMode(BoltzSetup vm, BoltzMode? mode, string storeId)
    {
        if (boltzService.Daemon.Node is not null)
        {
            var rebalanceStore = boltzService.RebalanceStore;
            vm.AllowRebalance = (rebalanceStore is null || rebalanceStore == Settings) &&
                                User.IsInRole(Roles.ServerAdmin);
        }

        if (mode is null)
        {
            return View(vm);
        }

        try
        {
            await boltzService.InitializeStore(storeId, mode.Value);
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
                return RedirectGetStarted();
            }

            return View(vm);
        }

        return RedirectGetStarted();
    }

    private async Task SetStandaloneWallet(string name)
    {
        var settings = Settings;
        var wallet = await Boltz.GetWallet(name);
        settings.Mode = BoltzMode.Standalone;
        settings.StandaloneWallet = new BoltzSettings.Wallet { Id = wallet.Id, Name = wallet.Name };
        await boltzService.Set(CurrentStore.Id, settings);
        SetLightning();
    }

    [HttpPost("setup/wallet/{flow}/{currency}")]
    public async Task<RedirectToActionResult> SetupWallet(WalletSetup vm, string walletName,
        bool isBtcPay)
    {
        if (Boltz != null)
        {
            try
            {
                switch (vm.Flow)
                {
                    case WalletSetupFlow.Standalone:
                        await SetStandaloneWallet(walletName);
                        break;
                    case WalletSetupFlow.Lightning:
                        await SetLightningConfig(new LightningConfig
                            {
                                Currency = vm.Currency!.Value,
                                SwapType = vm.SwapType!,
                                Wallet = walletName
                            },
                            new[]
                            {
                                "wallet", "currency", "swap_type",
                            }
                        );
                        return RedirectToAction(nameof(SetupThresholds), new { storeId = vm.StoreId });
                    case WalletSetupFlow.Chain:
                        var config = ChainSetup;
                        config.ToWallet = walletName;
                        ChainSetup = config;
                        return RedirectToAction(nameof(SetupBudget),
                            new { storeId = vm.StoreId, swapperType = SwapperType.Chain });
                    case WalletSetupFlow.Manual:
                        return RedirectToAction(nameof(Configuration), new { storeId = vm.StoreId });
                }
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Could not setup wallet: " + e.Message;
                return RedirectGetStarted();
            }

            return RedirectToAction(nameof(SetupChain), new { storeId = vm.StoreId });
        }

        return RedirectGetStarted();
    }

    [HttpGet("setup/wallet/{flow}/{currency}/create")]
    public async Task<IActionResult> CreateWallet(WalletSetup vm, string storeId)
    {
        if (Boltz != null)
        {
            return View(vm);
        }

        return RedirectGetStarted();
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
                    var next = await SetupWallet(vm, response.Wallet.Name, false);
                    return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
                    {
                        Mnemonic = response.Mnemonic,
                        ReturnUrl = Url.Action(next.ActionName, next.RouteValues),
                        IsStored = true,
                    });
                }

                var wallet = await Boltz.ImportWallet(vm.WalletParams, vm.WalletCredentials);
                return await SetupWallet(vm, wallet.Name, false);
            }
            catch (RpcException e)
            {
                TempData[WellKnownTempData.ErrorMessage] = e.Status.Detail;
                return RedirectToAction(vm.ImportMethod is null ? "CreateWallet" : "ImportWallet", vm.RouteData);
            }
        }

        return RedirectGetStarted();
    }

    [HttpGet("setup/wallet/{flow}/{currency}/import")]
    public async Task<IActionResult> ImportWallet(WalletSetup vm)
    {
        if (Boltz != null)
        {
            if (vm.ImportMethod == null)
            {
                return View("ImportWalletOptions", vm);
            }

            return View("CreateWallet", vm);
        }

        return RedirectGetStarted();
    }

    [HttpGet("setup/thresholds")]
    public async Task<IActionResult> SetupThresholds()
    {
        if (Boltz != null)
        {
            var vm = new BalanceSetup
            {
                Ln = await Boltz.GetLightningConfig()
            };
            return View(vm);
        }

        return RedirectGetStarted();
    }

    [HttpPost("setup/thresholds")]
    public async Task<IActionResult> SetupThresholds(BalanceSetup vm, string storeId)
    {
        if (Boltz != null)
        {
            await SetLightningConfig(vm.Ln, new[]
            {
                "outbound_balance_percent", "inbound_balance_percent", "outbound_balance", "inbound_balance"
            });
            return RedirectToAction(nameof(SetupBudget), new
            {
                storeId, swapperType = SwapperType.Ln
            });
        }

        return RedirectGetStarted();
    }

    [HttpGet("setup/{swapperType}/budget")]
    public async Task<IActionResult> SetupBudget(BudgetSetup vm)
    {
        if (Boltz != null)
        {
            vm.Budget = 100_000;
            vm.BudgetIntervalDays = 7;
            vm.MaxFeePercent = 1;
            return View(vm);
        }

        return RedirectGetStarted();
    }

    [HttpPost("setup/{swapperType}/budget")]
    public async Task<IActionResult> SetupBudget(BudgetSetup vm, string storeId)
    {
        if (Boltz != null)
        {
            var paths = new[]
            {
                "budget", "budget_interval", "max_fee_percent"
            };
            if (vm.SwapperType == SwapperType.Ln)
            {
                var config = await SetLightningConfig(new LightningConfig
                {
                    Budget = vm.Budget,
                    BudgetInterval = vm.BudgetIntervalDays,
                    MaxFeePercent = vm.MaxFeePercent,
                }, paths);

                if (config.Currency == Currency.Lbtc)
                {
                    return RedirectToAction(nameof(SetupChain), new { storeId });
                }
            }
            else
            {
                var setup = ChainSetup;
                await SetChainConfig(new ChainConfig
                {
                    Enabled = false,
                    ToWallet = setup.ToWallet,
                    MaxBalance = setup.MaxBalance,
                    Budget = vm.Budget,
                    BudgetInterval = vm.BudgetIntervalDays,
                    MaxFeePercent = vm.MaxFeePercent,
                });
            }

            return RedirectToAction(nameof(Enable), new { storeId });
        }

        return RedirectGetStarted();
    }

    async Task<LightningConfig> SetLightningConfig(LightningConfig config, IEnumerable<string>? paths = null)
    {
        if (config.Wallet == BtcPayName)
        {
            config.Wallet = "";
            if (config.StaticAddress is null || config.StaticAddress == "")
            {
                config.StaticAddress = await boltzService.GenerateNewAddress(CurrentStore);
                paths = paths?.Append("static_address");
            }
        }

        config.BudgetInterval = DaysToSeconds(config.BudgetInterval);

        return await Boltz.UpdateAutoSwapLightningConfig(config, paths);
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
        if (Settings.StandaloneWallet is not null)
        {
            config.FromWallet = Settings.StandaloneWallet.Name;
        }
        else
        {
            var ln = await Boltz.GetLightningConfig();
            if (ln is not null)
            {
                config.FromWallet = ln.Wallet;
            }
        }

        if (config.ToWallet == BtcPayName)
        {
            if (config.ToAddress is null || config.ToAddress == "")
            {
                config.ToAddress = await boltzService.GenerateNewAddress(CurrentStore);
            }

            config.ToWallet = "";
        }

        config.BudgetInterval = DaysToSeconds(config.BudgetInterval);

        await Boltz.UpdateAutoSwapChainConfig(config);
    }

    [HttpGet("setup/chain")]
    public async Task<IActionResult> SetupChain()
    {
        if (Boltz != null)
        {
            return View();
        }

        return RedirectGetStarted();
    }

    [HttpPost("setup/chain")]
    public async Task<IActionResult> SetupChain(ulong maxBalance, string storeId, string command)
    {
        if (Boltz != null)
        {
            if (command == "Skip")
            {
                return RedirectToAction(await Boltz.IsAutoSwapConfigured() ? nameof(Enable) : nameof(Info),
                    new { storeId });
            }

            ChainSetup = new ChainSetup { MaxBalance = maxBalance };
            return RedirectToAction(nameof(SetupWallet),
                new { storeId, flow = WalletSetupFlow.Chain, currency = Currency.Btc });
        }

        return RedirectGetStarted();
    }


    [HttpGet("setup/enable")]
    public async Task<IActionResult> Enable()
    {
        if (Boltz != null)
        {
            var vm = await Boltz.GetAutoSwapRecommendations();
            return View(vm);
        }

        return RedirectGetStarted();
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

            return RedirectToAction(nameof(Info), new { storeId });
        }

        return RedirectGetStarted();
    }
}