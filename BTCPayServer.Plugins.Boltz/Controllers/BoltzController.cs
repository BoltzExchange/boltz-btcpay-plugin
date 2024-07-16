using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autoswaprpc;
using Boltzrpc;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Plugins.Boltz.Services;
using BTCPayServer.Plugins.Boltz.Views;
using BTCPayServer.Plugins.Shopify;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.Configuration;

namespace BTCPayServer.Plugins.Boltz;

[Route("plugins/{storeId}/Boltz")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class BoltzController : Controller
{
    private readonly BoltzService _boltzService;
    private readonly BTCPayWalletProvider _btcPayWalletProvider;
    private readonly StoreRepository _storeRepository;
    private readonly ExplorerClientProvider _explorerProvider;
    private readonly LightningClientFactoryService _lightningClientFactoryService;

    private BoltzClient _boltz => _boltzService.GetClient(CurrentStore.Id);
    private BoltzSettings _settings => _boltzService.GetSettings(CurrentStore.Id);

    private StoreData CurrentStore => HttpContext.GetStoreData();

    public BoltzController(BoltzService boltzService, StoreRepository storeRepository,
        BTCPayWalletProvider btcPayWalletProvider, IConfiguration configuration,
        ExplorerClientProvider explorerProvider)
    {
        _storeRepository = storeRepository;
        _boltzService = boltzService;
        _btcPayWalletProvider = btcPayWalletProvider;
        _explorerProvider = explorerProvider;

        //configuration.GetConnectionString()


        //storeRepository.FindStore().Id
    }

    public async Task<IActionResult> Index(string storeId)
    {
        var client = _boltzService.GetClient(CurrentStore.Id);
        return RedirectToAction(client is null ? nameof(Connect) : nameof(Info), new { storeId });
    }

    // GET
    [HttpGet("info")]
    public async Task<IActionResult> Info(string storeId)
    {
        var data = new BoltzInfo();

        if (_boltz == null)
        {
            return RedirectToAction(nameof(Connect), new { storeId });
        }

        try
        {
            data.Info = await _boltz.GetInfo();
            data.Swaps = await _boltz.ListSwaps();
            data.Wallets = await _boltz.GetWallets();
            //data.AutoSwapData = await _boltz.GetAutoSwapData();
            data.Status = await _boltz.GetAutoSwapStatus();
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Error connecting to Boltz: " + e.Message;
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


    [HttpGet("configure")]
    public async Task<IActionResult> Configure(string storeId)
    {
        var data = new BoltzConfig();

        if (_boltz == null)
        {
            return RedirectToAction(nameof(Connect), new { storeId });
        }

        try
        {
            data = await _boltz.GetAutoSwapConfig();
            data.Wallets = (await _boltz.GetWallets()).Wallets_.ToList();
            data.LiquidWallet = _settings?.Wallet;
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Error connecting to Boltz: " + e.Message;
        }

        return View(data);
    }

    [HttpPost("configure")]
    public async Task<IActionResult> Configure(string storeId, BoltzConfig vm, string command = "")
    {
        switch (command)
        {
            case "BoltzSetLnConfig":
            {
                await _boltz.UpdateAutoSwapConfig(vm);
                TempData[WellKnownTempData.SuccessMessage] = "AutoSwap settings updated";
                break;
            }
            case "BoltzCreateWallet":
            {
                await _boltz.CreateWallet(vm.CreateWallet);
                TempData[WellKnownTempData.SuccessMessage] = "AutoSwap settings updated";
                break;
            }
        }

        return RedirectToAction(nameof(Info), new { storeId });
    }

    [HttpGet("connect")]
    public async Task<IActionResult> Connect(string storeId)
    {
        var data = new BoltzConnection()
        {
            Settings = _boltzService.GetSettings(CurrentStore.Id),
        };

        if (_boltz != null)
        {
            try
            {
                data.Info = await _boltz.GetInfo();
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Error connecting to Boltz: " + e.Message;
            }
        }

        return View(data);
    }


    [HttpPost("connect")]
    public async Task<IActionResult> Connect(string storeId, BoltzConnection vm, string command = "")
    {
        switch (command)
        {
            case "BoltzSaveCredentials":
            {
                var settings = vm.Settings;
                var validCreds = settings != null && settings?.CredentialsPopulated() == true;
                if (!validCreds)
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Please provide valid credentials";
                    return View(vm);
                }

                try
                {
                    _boltzService.Set(CurrentStore.Id, settings);
                    await _boltz.GetInfo();
                }
                catch (Exception err)
                {
                    TempData[WellKnownTempData.ErrorMessage] = err.Message;
                    return View(vm);
                }

                TempData[WellKnownTempData.SuccessMessage] = "Boltz plugin successfully updated";
                SetLightning();
                break;
            }
            case "BoltzClearCredentials":
            {
                await _boltzService.Set(CurrentStore.Id, null);
                TempData[WellKnownTempData.SuccessMessage] = "Boltz plugin credentials cleared";
                break;
            }
        }

        return RedirectToAction(nameof(Configure), new { storeId });
    }


    private async void SetLightning()
    {
        var cryptoCode = "BTC";
        var network = _explorerProvider.GetNetwork(cryptoCode);
        var paymentMethodId = new PaymentMethodId(network.CryptoCode, PaymentTypes.LightningLike);
        var paymentMethod = new LightningSupportedPaymentMethod
        {
            CryptoCode = paymentMethodId.CryptoCode
        };

        var settings = _boltzService.GetSettings(CurrentStore.Id);
        if (settings is null) return;
        var lightningClient = new BoltzLightningClient(settings.GrpcUrl, settings.Macaroon, settings.Wallet);
        try
        {
            paymentMethod.SetLightningUrl(lightningClient);
        }
        catch (Exception ex)
        {
            //ModelState.AddModelError(nameof(vm.ConnectionString), ex.Message);
            //return View(vm);
        }

        var lnurl = new PaymentMethodId(cryptoCode, PaymentTypes.LNURLPay);
        CurrentStore.SetSupportedPaymentMethod(paymentMethodId, paymentMethod);
        CurrentStore.SetSupportedPaymentMethod(lnurl, new LNURLPaySupportedPaymentMethod()
        {
            CryptoCode = cryptoCode,
            UseBech32Scheme = true,
            LUD12Enabled = false
        });

        await _storeRepository.UpdateStore(CurrentStore);
    }

    [HttpGet("setup/get-started")]
    public IActionResult GetStarted()
    {
        var setup = new BoltzSetup();
        return View(setup);
    }

    [HttpGet("setup/rebalance")]
    public IActionResult SetupRebalance()
    {
        throw new NotImplementedException();
    }

    [HttpGet("setup/lightning")]
    public IActionResult SetupLightning()
    {
        throw new NotImplementedException();
    }
}