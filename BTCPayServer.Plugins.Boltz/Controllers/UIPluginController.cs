using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autoswaprpc;
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

[Route("~/plugins/template")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class UIPluginController : Controller
{
    private readonly MyPluginService _PluginService;
    private readonly BoltzService _boltzService;
    private readonly BTCPayWalletProvider _btcPayWalletProvider;
    private readonly StoreRepository _storeRepository;
    private readonly ExplorerClientProvider _ExplorerProvider;
    private readonly LightningClientFactoryService _lightningClientFactoryService;

    private BoltzClient _boltz => _boltzService.GetClient(CurrentStore.Id);

    private StoreData CurrentStore => HttpContext.GetStoreData();

    public UIPluginController(MyPluginService PluginService, BoltzService boltzService, StoreRepository storeRepository,
        BTCPayWalletProvider btcPayWalletProvider, IConfiguration configuration)
    {
        _PluginService = PluginService;
        _storeRepository = storeRepository;
        _boltzService = boltzService;
        _btcPayWalletProvider = btcPayWalletProvider;

        //configuration.GetConnectionString()


        //storeRepository.FindStore().Id
    }

    // GET
    public async Task<IActionResult> Index()
    {
        var data = new BoltzData()
        {
            Settings = _boltzService.GetSettings(CurrentStore.Id),
            Data = await _PluginService.Get()
        };

        if (_boltz != null)
        {
            try
            {
                data.Info = await _boltz.GetInfo();
                data.Swaps = await _boltz.ListSwaps();
                data.Wallets = await _boltz.GetWallets();
                data.AutoSwapData = await _boltz.GetAutoSwapData();
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Error connecting to Boltz: " + e.Message;
            }
        }

        return View(data);
    }

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

    private async void SetLightning()
    {
        var cryptoCode = "BTC";
        var network = _ExplorerProvider.GetNetwork(cryptoCode);
        var paymentMethodId = new PaymentMethodId(network.CryptoCode, PaymentTypes.LightningLike);
        var url = "";
        var paymentMethod = new LightningSupportedPaymentMethod
        {
            CryptoCode = paymentMethodId.CryptoCode
        };

        try
        {
            var lightningClient = _lightningClientFactoryService.Create(url, network);
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


    [HttpPost()]
    public async Task<IActionResult> Index(BoltzData vm, string command = "")
    {
        switch (command)
        {
            case "BoltzSaveCredentials":
            {
                var settings = vm.Settings;
                var validCreds = settings != null && settings?.CredentialsPopulated() == true;
                if (!validCreds)
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Please provide valid Shopify credentials";
                    return View(new BoltzData());
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
                /*
                var apiClient = new ShopifyApiClient(_clientFactory, shopify.CreateShopifyApiCredentials());
                try
                {
                    await apiClient.OrdersCount();
                }
                catch (ShopifyApiException err)
                {
                    TempData[WellKnownTempData.ErrorMessage] = err.Message;
                    return View(vm);
                }

                var scopesGranted = await apiClient.CheckScopes();
                if (!scopesGranted.Contains("read_orders") || !scopesGranted.Contains("write_orders"))
                {
                    TempData[WellKnownTempData.ErrorMessage] =
                        "Please grant the private app permissions for read_orders, write_orders";
                    return View(vm);
                }

                // everything ready, proceed with saving Shopify integration credentials
                shopify.IntegratedAt = DateTimeOffset.Now;
                */

                await _boltzService.Set(CurrentStore.Id, settings);
                TempData[WellKnownTempData.SuccessMessage] = "Boltz plugin successfully updated";
                break;
            }
            case "BoltzClearCredentials":
            {
                await _boltzService.Set(CurrentStore.Id, null);
                TempData[WellKnownTempData.SuccessMessage] = "Boltz plugin credentials cleared";
                break;
            }
            case "BoltzSetAutoSwap":
            {
                await _boltz.UpdateAutoSwapData(vm.AutoSwapData);
                TempData[WellKnownTempData.SuccessMessage] = "AutoSwap settings updated";
                break;
            }
        }

        return RedirectToAction(nameof(Index), new { storeId = CurrentStore.Id });
    }
}