#nullable enable
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BTCPayServer.Plugins.Boltz.Models.Api;

/// <summary>
/// Request to enable Boltz for a store
/// </summary>
public class BoltzSetupRequest
{
    /// <summary>
    /// The wallet name to use for receiving lightning payments
    /// </summary>
    [Required]
    public string WalletName { get; set; } = string.Empty;
}

/// <summary>
/// Response containing current Boltz setup status
/// </summary>
public class BoltzSetupData
{
    /// <summary>
    /// Whether Boltz is enabled for this store
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The configured mode: "standalone", "rebalance", or null if not configured
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public BoltzMode? Mode { get; set; }

    /// <summary>
    /// The configured wallet, if any
    /// </summary>
    public BoltzWalletData? Wallet { get; set; }
}

/// <summary>
/// Request to create or import a wallet
/// </summary>
public class CreateBoltzWalletRequest
{
    /// <summary>
    /// The name for the wallet
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The currency: "BTC" or "LBTC". Defaults to "LBTC" if not specified.
    /// </summary>
    public string Currency { get; set; } = "LBTC";

    /// <summary>
    /// Optional: 12 or 24 word mnemonic seed phrase for import
    /// </summary>
    public string? Mnemonic { get; set; }

    /// <summary>
    /// Optional: Bitcoin Core descriptor for watch-only import
    /// </summary>
    public string? CoreDescriptor { get; set; }
}

/// <summary>
/// Wallet balance information
/// </summary>
public class BoltzWalletBalance
{
    /// <summary>
    /// Total balance in satoshis
    /// </summary>
    public ulong Total { get; set; }

    /// <summary>
    /// Confirmed balance in satoshis
    /// </summary>
    public ulong Confirmed { get; set; }

    /// <summary>
    /// Unconfirmed balance in satoshis
    /// </summary>
    public ulong Unconfirmed { get; set; }
}

/// <summary>
/// Wallet data returned by the API
/// </summary>
public class BoltzWalletData
{
    /// <summary>
    /// Unique wallet identifier
    /// </summary>
    public ulong Id { get; set; }

    /// <summary>
    /// Wallet name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Wallet currency: "BTC" or "LBTC"
    /// </summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// Wallet balance
    /// </summary>
    public BoltzWalletBalance? Balance { get; set; }

    /// <summary>
    /// Whether this is a watch-only (read-only) wallet
    /// </summary>
    public bool Readonly { get; set; }
}

/// <summary>
/// Response when creating a new wallet (includes mnemonic for backup)
/// </summary>
public class CreateBoltzWalletResponse : BoltzWalletData
{
    /// <summary>
    /// The mnemonic seed phrase. Only returned when creating a new wallet.
    /// </summary>
    public string? Mnemonic { get; set; }
}
