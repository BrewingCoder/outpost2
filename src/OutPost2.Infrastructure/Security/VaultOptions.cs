namespace OutPost2.Infrastructure.Security;

/// <summary>
/// Everything about reaching Vault is configuration, never code. Dev runs a throwaway
/// container, integration points at the lab Vault over the LAN, production uses the
/// in-cluster address with Kubernetes auth — and none of those is a code change.
/// </summary>
public sealed class VaultOptions
{
    public const string SectionName = "OutPost2:Vault";

    /// <summary>
    /// Dev: <c>http://vault-dev:8200</c>.
    /// Integration: <c>https://vault.internal.example</c> (LAN only — never exposed via Cloudflare).
    /// Production: <c>https://keyvault.keyvault.svc:8200</c>.
    /// </summary>
    public string Address { get; set; } = "http://vault-dev:8200";

    public VaultAuthMethod AuthMethod { get; set; } = VaultAuthMethod.Token;

    /// <summary>Dev only. Never set in production — Kubernetes auth stores no secret at all.</summary>
    public string? Token { get; set; }

    public string? RoleId { get; set; }
    public string? SecretId { get; set; }

    /// <summary>Kubernetes auth role. Bound to ServiceAccount <c>outpost</c> in namespace <c>outpost</c>.</summary>
    public string? KubernetesRole { get; set; }

    public string KubernetesJwtPath { get; set; } = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    /// <summary>Transit mount. The lab Vault provisions <c>op2-transit</c> for us; dev mirrors it.</summary>
    public string TransitMount { get; set; } = "op2-transit";

    /// <summary>The key-encryption key. Non-exportable: plaintext never leaves Vault.</summary>
    public string KeyName { get; set; } = "op2-kek";

    /// <summary>
    /// PEM path for a private CA. The lab Vault uses the BrewingCoder Lab CA, so without
    /// this the TLS handshake fails against <c>vault.internal.example</c>.
    /// </summary>
    public string? CaCertPath { get; set; }

    /// <summary>
    /// Renew a token once this much of its lease remains. Tokens are 1h, renewable to 4h,
    /// so a voice turn must never be the thing that discovers an expiry.
    /// </summary>
    public TimeSpan RenewBefore { get; set; } = TimeSpan.FromMinutes(10);
}

public enum VaultAuthMethod
{
    /// <summary>Dev container with a known root token.</summary>
    Token,

    /// <summary>Workstation dev against the lab Vault.</summary>
    AppRole,

    /// <summary>Production. The pod's service-account JWT is exchanged for a token; nothing is stored.</summary>
    Kubernetes,
}
