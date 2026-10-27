using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Tests.Security;

/// <summary>
/// Hits a REAL Vault. The unit tests prove the crypto against a fake; only this proves the
/// wire format — that our JSON matches what Transit actually returns, which no fake can
/// establish. Skips cleanly when Vault is unreachable so CI without the dev stack stays green.
/// </summary>
[Trait("Category", "Integration")]
public class VaultTransitIntegrationTests
{
    private static VaultTransitClient? TryConnect(out string why)
    {
        var o = new VaultOptions
        {
            Address = Environment.GetEnvironmentVariable("OP2_TEST_VAULT_ADDR") ?? "http://localhost:8200",
            AuthMethod = VaultAuthMethod.Token,
            Token = Environment.GetEnvironmentVariable("OP2_TEST_VAULT_TOKEN") ?? "op2-dev-root",
            TransitMount = "op2-transit",
            KeyName = "op2-kek",
            CaCertPath = Environment.GetEnvironmentVariable("OP2_TEST_VAULT_CA"),
        };

        var client = new VaultTransitClient(Options.Create(o), NullLogger<VaultTransitClient>.Instance);
        if (!client.HealthyAsync().GetAwaiter().GetResult())
        {
            client.Dispose();
            why = $"Vault not reachable or sealed at {o.Address}";
            return null;
        }

        why = "";
        return client;
    }

    [SkippableFact]
    public async Task Real_vault_round_trips_a_credential_end_to_end()
    {
        using var vault = TryConnect(out var why);
        Skip.If(vault is null, why);

        var protector = new CredentialProtector(vault!);
        const string secret = "0.AXkA-pretend-entra-refresh-token-value";

        var sealedCred = await protector.ProtectAsync(secret);

        // Transit's own format, not ours — if this ever changes we want to know here.
        Assert.StartsWith("vault:v", sealedCred.WrappedKey);
        Assert.DoesNotContain(secret, sealedCred.ToJson(), StringComparison.Ordinal);

        Assert.Equal(secret, await protector.UnprotectAsync(sealedCred));
    }

    [SkippableFact]
    public async Task Real_vault_rewraps_without_disturbing_the_ciphertext()
    {
        using var vault = TryConnect(out var why);
        Skip.If(vault is null, why);

        var protector = new CredentialProtector(vault!);
        const string secret = "rotate-me";

        var before = await protector.ProtectAsync(secret);
        var after = await protector.RewrapAsync(before);

        // Even without a key rotation in between, rewrap must preserve the payload and
        // still decrypt — this is the mechanism rotation depends on.
        Assert.Equal(before.Ciphertext, after.Ciphertext);
        Assert.Equal(secret, await protector.UnprotectAsync(after));
    }

    [SkippableFact]
    public async Task Real_vault_refuses_a_tampered_wrapped_key()
    {
        using var vault = TryConnect(out var why);
        Skip.If(vault is null, why);

        var protector = new CredentialProtector(vault!);
        var sealedCred = await protector.ProtectAsync("secret");

        var mangled = sealedCred with { WrappedKey = sealedCred.WrappedKey[..^4] + "AAAA" };

        await Assert.ThrowsAsync<VaultException>(() => protector.UnprotectAsync(mangled));
    }
}
