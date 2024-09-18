#nullable enable
using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Boltz;

public enum BoltzMode
{
    Rebalance,
    Standalone
}

public class BoltzSettings
{
    [Display(Name = "GRPC Url")] public Uri? GrpcUrl { get; set; }

    [Display(Name = "Macaroon")] public string? Macaroon { get; set; }

    public BoltzMode? Mode { get; set; }

    public ulong TenantId { get; set; }
    public ulong ActualTenantId => Mode == BoltzMode.Rebalance ? 1 : TenantId;

    public class Wallet
    {
        public ulong Id { get; set; }
        public string? Name { get; set; }
    }

    public Wallet? StandaloneWallet { get; set; }

    public bool CredentialsPopulated()
    {
        return
            !string.IsNullOrWhiteSpace(GrpcUrl?.ToString()) &&
            !string.IsNullOrWhiteSpace(Macaroon);
    }
}