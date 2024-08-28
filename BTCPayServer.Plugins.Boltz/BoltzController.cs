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
using System.Threading;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Services.Invoices;
using Google.Apis.Util;
using Google.Protobuf;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Newtonsoft.Json;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;
using ChainConfig = Autoswaprpc.ChainConfig;
using LightningConfig = Autoswaprpc.LightningConfig;
using RpcException = Grpc.Core.RpcException;
using StringWriter = System.IO.StringWriter;

namespace BTCPayServer.Plugins.Boltz;

[Route("plugins/{storeId}/Boltz")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class BoltzController(
    BoltzService boltzService,
    BoltzDaemon boltzDaemon,
            InvoiceRepository invoiceRepository,
    BTCPayNetworkProvider btcPayNetworkProvider)
    : Controller
{
    private BoltzClient? Boltz => boltzDaemon.GetClient(Settings);
    private BoltzSettings? Settings => SetupSettings ?? SavedSettings;
    private bool Configured => boltzService.StoreConfigured(CurrentStore.Id);
    private BoltzSettings? SavedSettings => boltzService.GetSettings(CurrentStore.Id);
    private bool IsAdmin => User.IsInRole(Roles.ServerAdmin);

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
    [HttpGet("status/swap/{swapId}")]
    public async Task<IActionResult> Status(string storeId, string swapId)
    {
        ClearSetup();
        if (!Configured || Boltz is null)
        {
            return RedirectSetup();
        }


        var data = new BoltzInfo();
        try
        {
            if (!string.IsNullOrEmpty(swapId))
            {
                //invoiceRepository.GetInvoices()
                data.SwapInfo = await Boltz.GetSwapInfo(swapId);
            }
            else
            {
                data.Info = await Boltz.GetInfo();
                data.Swaps = await Boltz.ListSwaps();
                data.Stats = await Boltz.GetStats();
                if (Settings?.Mode == BoltzMode.Standalone)
                {
                    data.StandaloneWallet = await Boltz.GetWallet(Settings?.StandaloneWallet?.Name!);
                }

                (data.Ln, data.Chain) = await Boltz.GetAutoSwapConfig();
                if (data.Ln is not null || data.Chain is not null)
                {
                    data.Status = await Boltz.GetAutoSwapStatus();
                    data.Recommendations = await Boltz.GetAutoSwapRecommendations();
                }
            }
        }
        catch (RpcException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = e.Message;
        }

        return View(data);
    }


    [HttpGet("configuration")]
    public async Task<IActionResult> Configuration(string storeId)
    {
        ClearSetup();
        if (!Configured || Boltz is null)
        {
            return RedirectSetup();
        }

        var data = new BoltzConfig { Settings = Settings, };

        try
        {
            data.ExistingWallets = await GetExistingWallets(true);

            (data.Ln, data.Chain) = await Boltz!.GetAutoSwapConfig();
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

    [HttpGet("wallet/{walletId}/send")]
    public async Task<IActionResult> WalletSend(ulong walletId, string? swapId, string? transactionId)
    {
        var vm = new WalletSendModel();

        if (Boltz is null)
        {
            return RedirectSetup();
        }

        try
        {
            vm.Wallet = await Boltz.GetWallet(walletId);
            vm.TransactionId = transactionId;
            if (swapId is not null)
            {
                vm.SwapInfo = await Boltz.GetSwapInfo(swapId);
            }
        }
        catch (RpcException)
        {
            return NotFound();
        }

        return View(vm);
    }

    [HttpGet("wallet/{walletId}/credentials")]
    public async Task<IActionResult> WalletCredentials(ulong walletId, string storeId)
    {
        if (Boltz is null)
        {
            return RedirectSetup();
        }
        try
        {
            var creds = await Boltz.GetWalletCredentials(walletId);
            if (string.IsNullOrEmpty(creds.Mnemonic))
            {
                TempData[WellKnownTempData.ErrorMessage] = "Wallet credentials not available";
                return RedirectToAction(nameof(Status), new { storeId, walletId });
            }
            return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
            {
                Mnemonic = creds.Mnemonic,
                ReturnUrl = Url.Action(nameof(Status), new {storeId}),
                IsStored = true,
                RequireConfirm = false,
            });
        }
        catch (RpcException)
        {
            return NotFound();
        }
    }

    [HttpPost("wallet/{walletId}/send")]
    public async Task<IActionResult> WalletSend(WalletSendModel vm, ulong walletId)
    {
        if (Boltz is null)
        {
            return RedirectSetup();
        }

        try
        {
            var storeId = CurrentStore.Id;
            switch (vm.SendType)
            {
                case SendType.Native:
                    var sendRequest = new WalletSendRequest
                    {
                        Address = vm.Destination,
                        Amount = vm.Amount!.Value,
                        Id = walletId,
                    };

                    if (vm.FeeRate.HasValue)
                    {
                        sendRequest.SatPerVbyte = vm.FeeRate.Value;
                    }

                    var sendResponse = await Boltz.WalletSend(sendRequest);
                    return RedirectToAction(nameof(WalletSend),
                        new { storeId, walletId, transactionId = sendResponse.TxId });
                case SendType.Lightning:
                    var request = new CreateSwapRequest
                    {
                        Amount = vm.Amount ?? 0,
                        Invoice = vm.Destination,
                        Pair = new Pair { From = Currency.Lbtc, To = Currency.Btc },
                        SendFromInternal = true,
                        WalletId = walletId,
                        ZeroConf = true,
                    };
                    if (vm.FeeRate.HasValue)
                    {
                        request.SatPerVbyte = vm.FeeRate.Value;
                    }

                    var swap = await Boltz.CreateSwap(request);
                    return RedirectToAction(nameof(WalletSend), new { storeId, walletId, swapId = swap.Id });
                case SendType.Chain:
                    var chainRequest = new CreateChainSwapRequest
                    {
                        Amount = vm.Amount!.Value,
                        ToAddress = vm.Destination,
                        Pair = new Pair { From = Currency.Lbtc, To = Currency.Btc },
                        FromWalletId = walletId,
                        LockupZeroConf = true,
                        AcceptZeroConf = true,
                    };
                    if (vm.FeeRate.HasValue)
                    {
                        chainRequest.SatPerVbyte = vm.FeeRate.Value;
                    }

                    var chainSwap = await Boltz.CreateChainSwap(chainRequest);
                    return RedirectToAction(nameof(WalletSend), new { storeId, walletId, swapId = chainSwap.Id });
            }

            vm.Wallet = await Boltz.GetWallet(walletId);
        }
        catch (RpcException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = e.Status.Detail;
        }

        return RedirectToAction(nameof(WalletSend), new { storeId = CurrentStore.Id, walletId });
    }

    [HttpGet("wallet/{walletId}/receive")]
    public async Task<IActionResult> WalletReceive(WalletReceiveModel vm, ulong walletId, string? swapId)
    {
        if (Boltz is null)
        {
            return RedirectSetup();
        }

        try
        {
            vm.Wallet = await Boltz.GetWallet(walletId);
            if (swapId is not null)
            {
                vm.SwapInfo = await Boltz.GetSwapInfo(swapId);
            }

            return View(vm);
        }
        catch (RpcException)
        {
            return NotFound();
        }
    }

    [HttpPost("wallet/{walletId}/receive")]
    public async Task<IActionResult> WalletReceive(WalletReceiveModel vm, ulong walletId)
    {
        if (Boltz is null)
        {
            return RedirectSetup();
        }


        try
        {
            vm.Wallet = await Boltz.GetWallet(walletId);
            var storeId = CurrentStore.Id;

            switch (vm.SendType)
            {
                case SendType.Native:
                    var receiveResponse = await Boltz.WalletReceive(walletId);
                    return RedirectToAction(nameof(WalletReceive),
                        new { storeId, walletId, address = receiveResponse.Address });
                case SendType.Lightning:
                    var request = new CreateReverseSwapRequest
                    {
                        Amount = vm.AmountLn!.Value,
                        Pair = new Pair { From = Currency.Btc, To = vm.Wallet.Currency },
                        WalletId = walletId,
                        AcceptZeroConf = true,
                    };
                    var reverseSwap = await Boltz.CreateReverseSwap(request);
                    return RedirectToAction(nameof(WalletReceive), new { storeId, walletId, swapId = reverseSwap.Id });
                case SendType.Chain:
                    var chainRequest = new CreateChainSwapRequest
                    {
                        Amount = vm.AmountChain!.Value,
                        Pair = new Pair
                        {
                            From = vm.Wallet.Currency == Currency.Lbtc ? Currency.Btc : Currency.Lbtc,
                            To = vm.Wallet.Currency
                        },
                        ToWalletId = walletId,
                        ExternalPay = true,
                        AcceptZeroConf = true,
                    };
                    var chainSwap = await Boltz.CreateChainSwap(chainRequest);
                    return RedirectToAction(nameof(WalletReceive), new { storeId, walletId, swapId = chainSwap.Id });
            }
        }
        catch (RpcException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = e.Status.Detail;
        }

        return RedirectToAction(nameof(WalletReceive), new { storeId = CurrentStore.Id, walletId });
    }


    [HttpGet("swap/{id}")]
    public async Task<IActionResult> SwapInfo(string id)
    {
        if (Boltz != null)
        {
            try
            {
                var info = await Boltz.GetSwapInfo(id);
                return View(info);
            }
            catch (RpcException e)
            {
                return NotFound();
            }
        }

        return RedirectSetup();
    }

    [HttpGet("swap/{id}/partial")]
    public async Task<IActionResult> SwapInfoPartial(string id)
    {
        if (Boltz != null)
        {
            try
            {
                var info = await Boltz.GetSwapInfo(id);
                return PartialView("_SwapInfoPartial", info);
            }
            catch (RpcException)
            {
                return NotFound();
            }
        }

        return RedirectSetup();
    }

    [HttpGet("swap/{id}/sse")]
    public async Task SwapInfoStream(string id, string storeId)
    {
        if (Boltz is null)
        {
            Response.Redirect(Url.Action(nameof(SetupMode), new { storeId })!);
            return;
        }

        var stream = Boltz.GetSwapInfoStream(id);
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        while (await stream.ResponseStream.MoveNext(CancellationToken.None))
        {
            var info = stream.ResponseStream.Current;
            var swapId = info.Swap?.Id ?? info.ReverseSwap?.Id ?? info.ChainSwap?.Id ?? "";
            await Response.WriteAsync($"data: {swapId}\r\r");
            await Response.Body.FlushAsync();
        }
    }

    [HttpPost("swap/{id}/refund")]
    public async Task<IActionResult> RefundSwap(string id, string address, string storeId)
    {
        if (Boltz is null)
        {
            return RedirectSetup();
        }

        try
        {
            await Boltz.RefundSwap(new RefundSwapRequest
            {
                Id = id,
                Address = address
            });
            TempData[WellKnownTempData.SuccessMessage] = "Swap refunded";
        }
        catch (RpcException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = e.Status.Detail;
        }

        return RedirectToAction(nameof(Status), new { storeId, swapId = id });
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
                if (vm.Ln is not null)
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
                }

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
                // TODO: reenable when client v2.1.2
                //vm.Info = await Boltz.GetInfo();
            }
            catch (RpcException e)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Error connecting to Boltz: " + e.Status.Detail;
            }
        }

        var di = Directory.GetParent(boltzDaemon.LogFile);
        if (di is null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Could not load log files";
            return View(vm);
        }

        var fileName = Path.GetFileName(boltzDaemon.LogFile);

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
            vm.Log.Log = boltzDaemon.RecentOutput;
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
                if (!boltzDaemon.UpdateAvailable)
                {
                    await boltzDaemon.CheckLatestRelease();
                }

                if (boltzDaemon.UpdateAvailable)
                {
                    boltzDaemon.StartUpdate();
                }
                else
                {
                    TempData[WellKnownTempData.SuccessMessage] = "No update available";
                }

                break;
            }
            case "Clear":
            {
                await boltzService.Set(CurrentStore.Id, null);
                TempData[WellKnownTempData.SuccessMessage] = "Boltz plugin credentials cleared";
                break;
            }
            case "Start":
            {
                boltzDaemon.InitiateStart();
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
    public async Task<IActionResult> SetupMode(ModeSetup vm, BoltzMode? mode, string storeId)
    {
        vm.IsAdmin = IsAdmin;

        if (!string.IsNullOrEmpty(boltzDaemon.Error) && vm.IsAdmin)
        {
            return RedirectToAction(nameof(Admin), new { storeId });
        }

        if (mode is null)
        {
            vm.ExistingSettings = SavedSettings;
            vm.ConnectedNode = CurrentStore.GetSupportedPaymentMethods(btcPayNetworkProvider)
                .OfType<LightningSupportedPaymentMethod>()
                .FirstOrDefault();
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

        if (mode == BoltzMode.Rebalance)
        {
            if (!vm.IsAdmin)
            {
                return new UnauthorizedResult();
            }

            LightningSetup = new LightningConfig();
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
                    Balance = wallet.Balance?.Total,
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
    public async Task<RedirectToActionResult> SetupWallet(WalletSetup vm, string storeId)
    {
        if (Boltz != null)
        {
            try
            {
                var walletName = vm.WalletName ?? String.Empty;
                var wallet = await Boltz.GetWallet(walletName);
                if (wallet.Balance is null)
                {
                    return RedirectToAction(nameof(SetupSubaccount), vm.GetRouteData("initialRender", true));
                }

                switch (vm.Flow)
                {
                    case WalletSetupFlow.Standalone:
                        if (SetupSettings?.Mode != BoltzMode.Standalone)
                        {
                            return RedirectSetup();
                        }

                        SetupSettings = await SetStandaloneWallet(SetupSettings!, walletName);
                        return RedirectToAction(nameof(SetupChain), new { storeId });
                    case WalletSetupFlow.Lightning:
                        if (LightningSetup is null)
                        {
                            return RedirectSetup();
                        }

                        LightningSetup = new LightningConfig(LightningSetup)
                        {
                            Currency = vm.Currency!.Value,
                            SwapType = vm.SwapType ?? String.Empty,
                            Wallet = walletName,
                        };
                        return RedirectToAction(nameof(SetupThresholds), new { storeId });
                    case WalletSetupFlow.Chain:
                        if (ChainSetup is null)
                        {
                            return RedirectToAction(nameof(SetupChain), new { storeId });
                        }

                        ChainSetup = new ChainConfig(ChainSetup) { ToWallet = walletName };
                        return RedirectToAction(nameof(SetupBudget),
                            new { storeId, swapperType = SwapperType.Chain });
                    case WalletSetupFlow.Manual:
                        return RedirectToAction(nameof(Configuration), new { storeId });
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


    [HttpGet("setup/wallet/{flow}/{currency}/subaccount")]
    public async Task<IActionResult> SetupSubaccount(WalletSetup vm)
    {
        if (Boltz != null)
        {
            if (!vm.InitialRender)
            {
                try
                {
                    var wallet = await Boltz.GetWallet(vm.WalletName!);
                    var response = await Boltz.GetSubaccounts(wallet.Id);
                    vm.Subaccounts = response.Subaccounts.ToList()
                        .FindAll(account => account.Balance.Total > 0 || account.Type == "p2wpkh");
                    if (vm.Subaccounts.Count == 0)
                    {
                        return await SetupSubaccount(vm, null);
                    }
                }
                catch (RpcException e)
                {
                    TempData[WellKnownTempData.ErrorMessage] = e.Status.Detail;
                }
            }

            return View(vm);
        }

        return RedirectSetup();
    }

    [HttpPost("setup/wallet/{flow}/{currency}/subaccount")]
    public async Task<IActionResult> SetupSubaccount(WalletSetup vm, ulong? subaccount)
    {
        if (Boltz != null)
        {
            try
            {
                var wallet = await Boltz.GetWallet(vm.WalletName!);
                await Boltz.SetSubaccount(wallet.Id, subaccount);
            }
            catch (RpcException e)
            {
                TempData[WellKnownTempData.ErrorMessage] = e.Status.Detail;
            }

            return await SetupWallet(vm, vm.StoreId!);
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
                var walletParams = new WalletParams { Currency = vm.Currency ?? Currency.Lbtc, Name = vm.WalletName };
                if (vm.ImportMethod is null)
                {
                    var response = await Boltz.CreateWallet(walletParams);
                    var next = await SetupWallet(vm, vm.StoreId!);
                    return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
                    {
                        Mnemonic = response.Mnemonic,
                        ReturnUrl = Url.Action(next.ActionName, next.RouteValues),
                        IsStored = true,
                    });
                }

                await Boltz.ImportWallet(walletParams, vm.WalletCredentials);
                return RedirectToAction(nameof(SetupSubaccount), vm.GetRouteData("initialRender", true));
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
                storeId, swapperType = SwapperType.Lightning
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
            if (vm.SwapperType == SwapperType.Lightning)
            {
                if (LightningSetup is null)
                {
                    return RedirectSetup();
                }

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
                if (ChainSetup is null)
                {
                    return RedirectSetup();
                }

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

            if (Settings?.Mode == BoltzMode.Standalone)
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

            await boltzService.Set(CurrentStore.Id, Settings);

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