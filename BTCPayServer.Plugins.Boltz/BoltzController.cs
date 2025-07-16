#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Data;
using System.Threading;
using BTCPayServer.Client;
using Autoswaprpc;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Google.Protobuf;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Newtonsoft.Json;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;
using ChainConfig = Autoswaprpc.ChainConfig;
using LightningConfig = Autoswaprpc.LightningConfig;
using RpcException = Grpc.Core.RpcException;
using BTCPayServer.Abstractions.Models;

namespace BTCPayServer.Plugins.Boltz;

[Route("plugins/{storeId}/Boltz")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class BoltzController(
    BoltzService boltzService,
    BoltzDaemon boltzDaemon,
    PoliciesSettings policiesSettings,
    BTCPayNetworkProvider btcPayNetworkProvider,
    PaymentMethodHandlerDictionary handlers
)
    : Controller
{
    private BoltzClient? Boltz => boltzDaemon.GetClient(Settings);
    private BoltzSettings? Settings => SetupSettings ?? SavedSettings;
    private bool Configured => boltzService.StoreConfigured(CurrentStoreId);
    private BoltzSettings? SavedSettings => boltzService.GetSettings(CurrentStoreId);
    private bool IsAdmin => User.IsInRole(Roles.ServerAdmin);
    private bool AllowImportHot => IsAdmin || policiesSettings.AllowHotWalletRPCImportForAll;
    private bool AllowCreateHot => IsAdmin || policiesSettings.AllowHotWalletForAll;

    private const string BtcPayName = "BTCPay";
    private const string BackUrl = "BackUrl";

    private StoreData? CurrentStore => HttpContext.GetStoreData();
    private string? CurrentStoreId => HttpContext.GetCurrentStoreId();

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
        if (!Configured || Boltz is null)
        {
            return RedirectSetup();
        }


        var data = new BoltzInfo();
        try
        {
            data.Info = await Boltz.GetInfo();
            data.Stats = await Boltz.GetStats();
            if (Settings?.Mode == BoltzMode.Standalone)
            {
                data.StandaloneWallet = await Boltz.GetWallet(Settings?.StandaloneWallet?.Name!);
            }

            (data.Ln, data.Chain) = await Boltz.GetAutoSwapConfig();
            if (data.Ln is not null || data.Chain is not null)
            {
                var response = await Boltz.ListSwaps(new ListSwapsRequest
                { Include = IncludeSwaps.Auto, Unify = true, State = SwapState.Pending });
                data.PendingAutoSwaps = response.AllSwaps.ToList();
                data.Status = await Boltz.GetAutoSwapStatus();
                data.Recommendations = await Boltz.GetAutoSwapRecommendations();
                if (data.Ln != null)
                {
                    foreach (var recommendation in data.Recommendations.Lightning)
                    {
                        if (recommendation.Swap?.DismissedReasons.Contains("pending swap") ?? false)
                        {
                            recommendation.Swap = null;
                        }
                    }

                    data.RebalanceWallet = await Boltz.GetWallet(data.Ln.Wallet);
                }
            }
        }
        catch (RpcException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = e.Message;
        }

        return View(data);
    }


    [HttpPost("status")]
    public async Task<IActionResult> Status(string storeId, string? lnRecommendation, string? chainRecommendation)
    {

        if (Boltz is null)
        {
            return RedirectSetup();
        }

        try
        {
            var request = new ExecuteRecommendationsRequest { Force = true };
            if (!string.IsNullOrEmpty(chainRecommendation))
                request.Chain.Add(JsonParser.Default.Parse<ChainRecommendation>(chainRecommendation));
            if (!string.IsNullOrEmpty(lnRecommendation))
                request.Lightning.Add(JsonParser.Default.Parse<LightningRecommendation>(lnRecommendation));
            await Boltz.ExecuteAutoSwapRecommendations(request);
            TempData[WellKnownTempData.SuccessMessage] = "Recommendations executed";
        }
        catch (RpcException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = e.Message;
        }

        return RedirectToAction(nameof(Status), new { storeId });
    }

    [HttpGet("wallets/{walletName?}")]
    public async Task<IActionResult> Wallets(string? walletName)
    {
        if (Boltz is null)
        {
            return RedirectSetup();
        }

        try
        {
            if (string.IsNullOrEmpty(walletName))
            {
                var wallets = await Boltz.GetWallets(true);
                return View(new WalletsModel { Wallets = wallets.Wallets_.ToList() });
            }
            var vm = new WalletViewModel();
            vm.Wallet = await Boltz.GetWallet(walletName);
            var response = await Boltz.ListWalletTransactions(new ListWalletTransactionsRequest
            {
                Id = vm.Wallet.Id,
                Limit = Math.Min((ulong)vm.Count, 30),
                Offset = (ulong)vm.Skip,
            });
            vm.Transactions = response.Transactions.ToList();
            return View("Wallet", vm);
        }
        catch (RpcException e)
        {
            if (!e.Status.Detail.Contains("not implemented"))
            {
                TempData[WellKnownTempData.ErrorMessage] = e.Status.Detail;
            }
            return RedirectToAction(nameof(Status), new { storeId = CurrentStoreId });
        }
    }

    [HttpGet("wallets/{walletId}/remove")]
    public IActionResult WalletRemove()
    {
        return View(
            "Confirm",
            new ConfirmModel(
                "Delete Wallet",
                "This action will remove the wallet. You will not be able to recover any funds if you don't have a backup.",
                "Delete"
            )
        );
    }

    [HttpPost("wallets/{walletId}/remove")]
    public async Task<IActionResult> WalletRemovePost(ulong walletId)
    {
        if (Boltz is null)
        {
            return RedirectSetup();
        }

        try
        {
            var standaloneWallet = Settings?.StandaloneWallet;
            if (standaloneWallet?.Id == walletId)
            {
                TempData[WellKnownTempData.ErrorMessage] = "You cannot delete the wallet used for lightning payments";
                return RedirectToAction(nameof(Wallets), new { storeId = CurrentStoreId, walletName = standaloneWallet?.Name });
            }
            await Boltz.RemoveWallet(walletId);
            TempData[WellKnownTempData.SuccessMessage] = "Wallet deleted";
        }
        catch (RpcException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = e.Status.Detail;
        }

        return RedirectToAction(nameof(Status), new { storeId = CurrentStoreId });
    }

    [HttpGet("swaps/{swapId?}")]
    public async Task<IActionResult> Swaps(SwapsModel vm, string? swapId)
    {
        ClearSetup();
        if (!Configured || Boltz is null)
        {
            return RedirectSetup();
        }

        try
        {
            if (!string.IsNullOrEmpty(swapId))
            {
                vm.SwapInfo = await Boltz.GetSwapInfo(swapId);
            }
            else
            {
                vm.Swaps = await Boltz.ListSwaps(new ListSwapsRequest
                {
                    Limit = (ulong)vm.Count,
                    Offset = (ulong)vm.Skip,
                    Unify = true,
                });
            }
        }
        catch (RpcException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = e.Message;
        }

        return View(vm);
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
            else if (vm.Wallet.Balance.Confirmed > 0)
            {
                vm.WalletSendFee = await Boltz.GetWalletSendFee(new WalletSendRequest
                {
                    Id = walletId,
                    SendAll = true,
                    IsSwapAddress = true,
                });

                var chainConfig = await Boltz.GetChainConfig();
                if (chainConfig != null)
                {
                    vm.ReserveBalance = chainConfig.ReserveBalance;
                }

                var pair = new Pair { From = vm.Wallet.Currency, To = Currency.Btc };
                var maxSend = (ulong)Math.Max((long)(vm.WalletSendFee.Amount - vm.ReserveBalance), 0);
                vm.LnInfo = await Boltz.GetPairInfo(pair, SwapType.Submarine);
                vm.ChainInfo = await Boltz.GetPairInfo(pair, SwapType.Chain);
                var fees = vm.LnInfo.Fees;
                // the service fee is applied to the LN amount
                var maxLn = (ulong)Math.Floor((maxSend - fees.MinerFees) / (1 + fees.Percentage / 100));
                vm.LnInfo.Limits.Maximal = Math.Min(vm.LnInfo.Limits.Maximal, maxLn);
                vm.ChainInfo.Limits.Maximal = Math.Min(vm.ChainInfo.Limits.Maximal, maxSend);
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
                ReturnUrl = Url.Action(nameof(Status), new { storeId }),
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
            var storeId = CurrentStoreId;
            switch (vm.SendType)
            {
                case SendType.Native:
                    var sendRequest = new WalletSendRequest
                    {
                        Address = vm.Destination,
                        Amount = vm.Amount,
                        Id = walletId,
                        SendAll = vm.SendAll,
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
                        Amount = vm.Amount,
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
                        Amount = vm.Amount,
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

        return RedirectToAction(nameof(WalletSend), new { storeId = CurrentStoreId, walletId });
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
            var storeId = CurrentStoreId;

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

        return RedirectToAction(nameof(WalletReceive), new { storeId = CurrentStoreId, walletId });
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
            catch (RpcException)
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
                    await boltzService.Set(CurrentStoreId!, await SetStandaloneWallet(Settings!, name));
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
        var vm = new AdminModel { ServerSettings = boltzService.ServerSettings, Settings = Settings };

        if (boltzDaemon.AdminClient != null)
        {
            try
            {
                vm.Info = await boltzDaemon.AdminClient.GetInfo();
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
                try
                {
                    var settings = vm.ServerSettings!;
                    await boltzService.SetServerSettings(settings);

                    if (settings is { ConnectNode: true, NodeConfig: null })
                    {
                        settings.NodeConfig = boltzDaemon.GetNodeConfig(boltzService.InternalLightning);
                    }

                    if (!settings.ConnectNode)
                    {
                        settings.NodeConfig = null;
                    }
                }
                catch (Exception err)
                {
                    TempData[WellKnownTempData.ErrorMessage] = err.Message;
                    return View(vm);
                }

                TempData[WellKnownTempData.SuccessMessage] = "Settings updated";
                break;
            }
            case "Clear":
            {
                await boltzService.Set(CurrentStoreId!, null);
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
        return RedirectToAction(nameof(SetupMode), new { storeId = CurrentStoreId });
    }

    [HttpGet("setup/{mode?}")]
    public async Task<IActionResult> SetupMode(ModeSetup vm, BoltzMode? mode, string storeId)
    {
        vm.IsAdmin = IsAdmin;
        vm.Enabled = IsAdmin || boltzService.ServerSettings.AllowTenants;

        if (vm.Enabled)
        {
            if (!string.IsNullOrEmpty(boltzDaemon.Error) && vm.IsAdmin)
            {
                return RedirectToAction(nameof(Admin), new { storeId });
            }

            if (mode is null)
            {
                vm.ExistingSettings = SavedSettings;
                vm.ConnectedNode =
                    CurrentStore!.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                        PaymentTypes.LN.GetPaymentMethodId("BTC"), handlers);
                vm.HasInternal = boltzService.InternalLightning is not null;
                vm.ConnectedInternal = boltzService.Daemon.Node is not null;
                vm.ConnectNodeSetting = boltzService.ServerSettings.ConnectNode;
                if (vm.IsAdmin)
                {
                    var store = await boltzService.GetRebalanceStore();
                    if (store?.Id != CurrentStoreId)
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

        return View(vm);
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
            var derivation = CurrentStore!.GetDerivationSchemeSettings(handlers, "BTC");
            if (derivation is not null && allowReadonly)
            {
                var balance = await boltzService.BtcWallet.GetBalance(derivation.AccountDerivation);
                result.Add(new ExistingWallet
                {
                    Name = BtcPayName,
                    IsBtcpay = true,
                    // even if its a hot wallet, we cant import it to boltz-client and not send properly
                    IsReadonly = true,
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
            vm.AllowCreateHot = AllowCreateHot;

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
                var walletName = vm.WalletName == null || vm.WalletName == BtcPayName ? String.Empty : vm.WalletName;
                var wallet = string.IsNullOrEmpty(walletName) ? null : await Boltz.GetWallet(walletName);
                if (wallet is { Balance: null })
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
                        await boltzService.Set(CurrentStoreId!, SetupSettings);

                        if (wallet!.Readonly)
                        {
                            return RedirectToAction(nameof(Status), new { storeId });
                        }

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

                        ChainSetup = new ChainConfig(ChainSetup)
                        { ToWallet = walletName };
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
                    if (!AllowCreateHot)
                    {
                        TempData[WellKnownTempData.ErrorMessage] = "Hot wallet creation is not allowed";
                        return RedirectToAction(nameof(CreateWallet), new { storeId = CurrentStoreId });
                    }

                    var response = await Boltz.CreateWallet(walletParams);
                    var next = await SetupWallet(vm, vm.StoreId!);
                    return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
                    {
                        Mnemonic = response.Mnemonic,
                        ReturnUrl = Url.Action(next.ActionName, next.RouteValues),
                        IsStored = true,
                    });
                }

                if (vm.ImportMethod == WalletImportMethod.Mnemonic && !AllowImportHot)
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Mnemonic import is not allowed";
                    return RedirectToAction(nameof(CreateWallet), new { storeId = CurrentStoreId });
                }

                await Boltz.ImportWallet(walletParams, vm.WalletCredentials);
                if (string.IsNullOrEmpty(vm.WalletCredentials.Mnemonic))
                {
                    return await SetupWallet(vm, vm.StoreId!);
                }

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
            vm.AllowImportHot = AllowImportHot;

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
                new { flow = WalletSetupFlow.Lightning, currency = vm.Ln.Currency, storeId = CurrentStoreId });
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
                storeId,
                swapperType = SwapperType.Lightning
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
                    new { storeId = CurrentStoreId, Flow = WalletSetupFlow.Chain });
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
            config.StaticAddress = await boltzService.GenerateNewAddress(CurrentStore!);
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
        config.FromWallet = await GetChainSwapsFromWallet();

        if (config is { ToWallet: "", ToAddress: "" })
        {
            config.ToAddress = await boltzService.GenerateNewAddress(CurrentStore!);
        }

        config.BudgetInterval = DaysToSeconds(config.BudgetInterval);

        await Boltz!.UpdateAutoSwapChainConfig(config);
    }

    [HttpGet("setup/chain")]
    public async Task<IActionResult> SetupChain(string storeId)
    {
        if (Boltz != null)
        {
            var fromWallet = await GetChainSwapsFromWallet();
            if (fromWallet is null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "No suitable wallet found for chain swaps";
                return RedirectToAction(nameof(Status), new { storeId });
            }

            var info = await Boltz.GetPairInfo(new Pair { From = Currency.Lbtc, To = Currency.Btc }, SwapType.Chain);
            var vm = new ChainSetup { PairInfo = info };

            if (Settings?.Mode == BoltzMode.Standalone)
            {
                vm.MaxBalance = 10_000_000;
                vm.ReserveBalance = 500_000;
                ViewData[BackUrl] = Url.Action(nameof(SetupWallet),
                    new { flow = WalletSetupFlow.Standalone, storeId });
            }
            else
            {
                var recommendations = await Boltz.GetAutoSwapRecommendations();
                foreach (var recommendation in recommendations.Lightning)
                {
                    vm.MaxBalance += recommendation.Channel.Capacity;
                }

                var swapType = LightningSetup?.SwapType;
                if (swapType is null)
                {
                    var (ln, _) = await Boltz.GetAutoSwapConfig();
                    swapType = ln?.SwapType;
                }

                if (swapType != "reverse")
                {
                    vm.ReserveBalance = Math.Max(vm.ReserveBalance, vm.MaxBalance / 2);
                }
            }

            ChainSetup = new ChainConfig { FromWallet = fromWallet };
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

            ChainSetup = new ChainConfig(ChainSetup)
            {
                MaxBalance = vm.MaxBalance,
                ReserveBalance = vm.ReserveBalance
            };
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

            await boltzService.Set(CurrentStoreId!, Settings);

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
                TempData[WellKnownTempData.SuccessMessage] = "Auto swap enabled";
            }

            if (LightningSetup != null && await GetChainSwapsFromWallet() != null)
            {
                LightningSetup = null;
                return RedirectToAction(nameof(SetupChain),
                    new { storeId });
            }

            return RedirectToAction(nameof(Status),
                new { storeId });
        }

        return RedirectSetup();
    }


    private async Task<string?> GetChainSwapsFromWallet()
    {
        if (Boltz != null)
        {
            var name = Settings?.StandaloneWallet?.Name;

            if (name is null)
            {
                var ln = await Boltz.GetLightningConfig();
                name = ln?.Wallet;
            }

            if (name != null)
            {
                var fromWallet = await Boltz.GetWallet(name);
                return fromWallet.Readonly || fromWallet.Currency != Currency.Lbtc ? null : name;
            }
        }

        return null;
    }
}
