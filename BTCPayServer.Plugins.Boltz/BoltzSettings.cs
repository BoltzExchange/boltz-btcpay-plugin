#nullable enable
using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Boltz;

public enum SetupFlow
{
    Standalone,
    Rebalance,
    OnchainPayments
}

public enum BoltzMode
{
    Rebalance,
    Standalone
}

public class BoltzServerSettings
{
    [Display(Name = "Allow Plugin for non-admin users")]
    public bool AllowTenants { get; set; }
    
    [Display(Name = "Connect to Internal Lightning Node")]
    public bool ConnectNode { get; set; }

    public NodeConfig? NodeConfig { get; set; }
}

public class BoltzSettings
{
    [Display(Name = "GRPC Url", Description = "Test")]
    public Uri? GrpcUrl { get; set; }

    [Display(Name = "Macaroon")] public string? Macaroon { get; set; }

    [Display(Name = "Certificate File Path")] public string? CertFilePath { get; set; }

    public BoltzMode? Mode { get; set; }

    public ulong TenantId { get; set; }
    public ulong ActualTenantId => Mode == BoltzMode.Rebalance ? 1 : TenantId;

    public class Wallet
    {
        public ulong Id { get; set; }
        public string Name { get; set; }
    }

    public Wallet? StandaloneWallet { get; set; }

    public bool CredentialsPopulated()
    {
        return
            !string.IsNullOrWhiteSpace(GrpcUrl?.ToString()) &&
            !string.IsNullOrWhiteSpace(Macaroon);
    }
}
