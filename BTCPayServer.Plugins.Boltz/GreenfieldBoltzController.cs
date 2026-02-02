using System;
using System.Linq;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Boltz.Models.Api;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Grpc.Core;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;

namespace BTCPayServer.Plugins.Boltz;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldBoltzController(
    BoltzService boltzService,
    PoliciesSettings policiesSettings
) : ControllerBase
{
    private bool IsAdmin => User.IsInRole(Roles.ServerAdmin);
    private bool AllowImportHot => IsAdmin || policiesSettings.AllowHotWalletRPCImportForAll;
    private bool AllowCreateHot => IsAdmin || policiesSettings.AllowHotWalletForAll;

    private const string BoltzUnavailable = "boltz-unavailable";
    private const string BoltzError = "boltz-error";

    [HttpGet("~/api/v1/stores/{storeId}/boltz/setup")]
    [Authorize(Policy = Policies.CanViewStoreSettings)]
    public async Task<IActionResult> GetSetup(string storeId)
    {
        var settings = boltzService.GetSettings(storeId);
        var response = new BoltzSetupData
        {
            Enabled = settings?.Mode != null,
            Mode = settings?.Mode
        };

        var client = boltzService.GetClient(storeId);
        if (settings?.StandaloneWallet != null && client != null)
        {
            try
            {
                var wallet = await client.GetWallet(settings.StandaloneWallet.Name);
                response.Wallet = ToWalletData(wallet);
            }
            catch (RpcException e)
            {
                return this.CreateAPIError(BoltzError, e.Status.Detail);
            }
        }

        return Ok(response);
    }

    [HttpPost("~/api/v1/stores/{storeId}/boltz/setup")]
    [Authorize(Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> EnableSetup(string storeId, [FromBody] BoltzSetupRequest request)
    {
        if (!ModelState.IsValid)
            return this.CreateValidationError(ModelState);

        try
        {
            var settings = await boltzService.EnableStandalone(storeId, request.WalletName);
            var client = boltzService.GetClient(storeId);
            var wallet = await client!.GetWallet(settings.StandaloneWallet!.Name);

            return Ok(new BoltzSetupData
            {
                Enabled = true,
                Mode = BoltzMode.Standalone,
                Wallet = ToWalletData(wallet)
            });
        }
        catch (RpcException e)
        {
            return this.CreateAPIError(BoltzError, e.Status.Detail);
        }
        catch (InvalidOperationException e)
        {
            return this.CreateAPIError(BoltzUnavailable, e.Message);
        }
        catch (Exception e)
        {
            return this.CreateAPIError("setup-failed", e.Message);
        }
    }

    [HttpDelete("~/api/v1/stores/{storeId}/boltz/setup")]
    [Authorize(Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> DisableSetup(string storeId)
    {
        try
        {
            await boltzService.Set(storeId, null);
            return Ok(new BoltzSetupData { Enabled = false });
        }
        catch (Exception e)
        {
            return this.CreateAPIError("disable-failed", e.Message);
        }
    }

    [HttpGet("~/api/v1/stores/{storeId}/boltz/wallets")]
    [Authorize(Policy = Policies.CanViewStoreSettings)]
    public async Task<IActionResult> ListWallets(string storeId)
    {
        try
        {
            var client = boltzService.GetClient(storeId);
            if (client == null)
            {
                return this.CreateAPIError("boltz-not-configured", "Boltz daemon is not available. Please ensure Boltz is properly configured.");
            }

            var wallets = await client.GetWallets(true);
            var result = wallets.Wallets_.Select(ToWalletData).ToList();
            return Ok(result);
        }
        catch (RpcException e)
        {
            return this.CreateAPIError(BoltzError, e.Status.Detail);
        }
    }

    [HttpPost("~/api/v1/stores/{storeId}/boltz/wallets")]
    [Authorize(Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> CreateWallet(string storeId, [FromBody] CreateBoltzWalletRequest request)
    {
        if (!ModelState.IsValid)
            return this.CreateValidationError(ModelState);

        if (!Enum.TryParse<Currency>(request.Currency, true, out var currency))
        {
            ModelState.AddModelError(nameof(request.Currency), "Invalid currency. Use 'BTC' or 'LBTC'.");
            return this.CreateValidationError(ModelState);
        }

        var walletParams = new WalletParams
        {
            Name = request.Name,
            Currency = currency
        };

        try
        {
            var client = await boltzService.GetOrCreateClient(storeId);
            if (client == null)
            {
                return this.CreateAPIError(BoltzUnavailable, "Boltz daemon is not available, ensure Boltz is properly configured.");
            }

            bool isImport = !string.IsNullOrEmpty(request.Mnemonic) || !string.IsNullOrEmpty(request.CoreDescriptor);

            if (!isImport)
            {
                if (!AllowCreateHot)
                {
                    return this.CreateAPIError(403, "hot-wallet-disabled", "Hot wallet creation is not allowed on this server");
                }

                var createResponse = await client.CreateWallet(walletParams);
                var wallet = await client.GetWallet(request.Name);

                return Ok(new CreateBoltzWalletResponse
                {
                    Id = wallet.Id,
                    Name = wallet.Name,
                    Currency = wallet.Currency.ToString().ToUpper(),
                    Balance = wallet.Balance != null ? new BoltzWalletBalance
                    {
                        Total = wallet.Balance.Total,
                        Confirmed = wallet.Balance.Confirmed,
                        Unconfirmed = wallet.Balance.Unconfirmed
                    } : null,
                    Readonly = wallet.Readonly,
                    Mnemonic = createResponse.Mnemonic
                });
            }
            else
            {
                var credentials = new WalletCredentials();
                if (!string.IsNullOrEmpty(request.Mnemonic))
                {
                    if (!AllowImportHot)
                    {
                        return this.CreateAPIError(403, "mnemonic-import-disabled", "Mnemonic import is not allowed on this server");
                    }
                    credentials.Mnemonic = request.Mnemonic;
                }
                if (!string.IsNullOrEmpty(request.CoreDescriptor))
                {
                    credentials.CoreDescriptor = request.CoreDescriptor;
                }

                var wallet = await client.ImportWallet(walletParams, credentials);

                return Ok(new BoltzWalletData
                {
                    Id = wallet.Id,
                    Name = wallet.Name,
                    Currency = wallet.Currency.ToString().ToUpper(),
                    Balance = wallet.Balance != null ? new BoltzWalletBalance
                    {
                        Total = wallet.Balance.Total,
                        Confirmed = wallet.Balance.Confirmed,
                        Unconfirmed = wallet.Balance.Unconfirmed
                    } : null,
                    Readonly = wallet.Readonly
                });
            }
        }
        catch (RpcException e)
        {
            return this.CreateAPIError(BoltzError, e.Status.Detail);
        }
        catch (Exception e)
        {
            return this.CreateAPIError("wallet-creation-failed", e.Message);
        }
    }

    [HttpDelete("~/api/v1/stores/{storeId}/boltz/wallets/{walletId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> RemoveWallet(string storeId, ulong walletId)
    {
        if (boltzService.IsWalletInUse(storeId, walletId))
        {
            return this.CreateAPIError("wallet-in-use", "Cannot delete the wallet currently configured for lightning payments. Disable Boltz first or switch to a different wallet.");
        }

        try
        {
            var client = await boltzService.GetOrCreateClient(storeId);
            if (client == null)
            {
                    return this.CreateAPIError(BoltzUnavailable, "Boltz daemon is not available, ensure Boltz is properly configured.");
            }

            await client.RemoveWallet(walletId);
            return Ok();
        }
        catch (RpcException e)
        {
            return this.CreateAPIError(BoltzError, e.Status.Detail);
        }
    }

    private static BoltzWalletData ToWalletData(Wallet wallet)
    {
        return new BoltzWalletData
        {
            Id = wallet.Id,
            Name = wallet.Name,
            Currency = wallet.Currency.ToString().ToUpper(),
            Balance = wallet.Balance != null ? new BoltzWalletBalance
            {
                Total = wallet.Balance.Total,
                Confirmed = wallet.Balance.Confirmed,
                Unconfirmed = wallet.Balance.Unconfirmed
            } : null,
            Readonly = wallet.Readonly
        };
    }
}
