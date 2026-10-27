using System.Security.Cryptography;
using System.Text;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Tests.Security;

/// <summary>
/// Exercises the envelope-encryption contract against a fake Vault, so the crypto is
/// tested without needing a live server. A separate integration test covers real Vault.
/// </summary>
public class CredentialProtectorTests
{
    /// <summary>
    /// Stands in for Transit. Mirrors the real semantics that matter: a fresh key per
    /// call, wrapped keys that carry a version, and rewrap that changes the wrapping
    /// without changing the key underneath.
    /// </summary>
    private sealed class FakeVault : IVaultTransitClient
    {
        private readonly Dictionary<string, byte[]> _wrapped = [];
        private int _n;
        public int KekVersion { get; set; } = 1;
        public int GenerateCalls { get; private set; }

        public Task<DataKey> GenerateDataKeyAsync(CancellationToken ct = default)
        {
            GenerateCalls++;
            var key = RandomNumberGenerator.GetBytes(32);
            var handle = $"vault:v{KekVersion}:fake{_n++}";
            _wrapped[handle] = key;
            return Task.FromResult(new DataKey((byte[])key.Clone(), handle));
        }

        public Task<byte[]> DecryptDataKeyAsync(string wrappedKey, CancellationToken ct = default) =>
            _wrapped.TryGetValue(wrappedKey, out var k)
                ? Task.FromResult((byte[])k.Clone())
                : throw new VaultException($"unknown wrapped key {wrappedKey}");

        public Task<string> RewrapAsync(string wrappedKey, CancellationToken ct = default)
        {
            var key = _wrapped[wrappedKey];
            var handle = $"vault:v{KekVersion}:fake{_n++}";
            _wrapped[handle] = key;              // same DEK, new wrapping
            return Task.FromResult(handle);
        }

        public Task<bool> HealthyAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private const string Secret = "0.AXkAeyJhbGciOiJ...a-refresh-token-shaped-string";

    [Fact]
    public async Task Round_trips_a_credential()
    {
        var p = new CredentialProtector(new FakeVault());
        var sealedCred = await p.ProtectAsync(Secret);

        Assert.Equal(Secret, await p.UnprotectAsync(sealedCred));
    }

    [Fact]
    public async Task Ciphertext_does_not_contain_the_plaintext()
    {
        var p = new CredentialProtector(new FakeVault());
        var sealedCred = await p.ProtectAsync(Secret);

        var blob = sealedCred.ToJson();
        Assert.DoesNotContain(Secret, blob, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token-shaped", blob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stored_form_never_carries_the_plaintext_data_key()
    {
        // The whole point: a stolen database must be inert without live Vault access.
        var vault = new FakeVault();
        var p = new CredentialProtector(vault);
        var sealedCred = await p.ProtectAsync(Secret);

        Assert.StartsWith("vault:v", sealedCred.WrappedKey);
        Assert.Throws<FormatException>(() => Convert.FromBase64String(sealedCred.WrappedKey));
    }

    [Fact]
    public async Task Each_credential_gets_its_own_data_key()
    {
        // Per-credential keys bound the blast radius of any single compromise.
        var vault = new FakeVault();
        var p = new CredentialProtector(vault);

        var a = await p.ProtectAsync("first");
        var b = await p.ProtectAsync("second");

        Assert.NotEqual(a.WrappedKey, b.WrappedKey);
        Assert.Equal(2, vault.GenerateCalls);
    }

    [Fact]
    public async Task Same_plaintext_twice_produces_different_ciphertext()
    {
        var p = new CredentialProtector(new FakeVault());

        var a = await p.ProtectAsync(Secret);
        var b = await p.ProtectAsync(Secret);

        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
        Assert.NotEqual(a.Nonce, b.Nonce);
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected_rather_than_returning_garbage()
    {
        var p = new CredentialProtector(new FakeVault());
        var sealedCred = await p.ProtectAsync(Secret);

        var bytes = Convert.FromBase64String(sealedCred.Ciphertext);
        bytes[0] ^= 0xFF;
        var tampered = sealedCred with { Ciphertext = Convert.ToBase64String(bytes) };

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => p.UnprotectAsync(tampered));
    }

    [Fact]
    public async Task Tampered_tag_is_rejected()
    {
        var p = new CredentialProtector(new FakeVault());
        var sealedCred = await p.ProtectAsync(Secret);

        var tag = Convert.FromBase64String(sealedCred.Tag);
        tag[0] ^= 0xFF;

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => p.UnprotectAsync(sealedCred with { Tag = Convert.ToBase64String(tag) }));
    }

    [Fact]
    public async Task Rewrap_changes_the_wrapping_but_preserves_the_credential()
    {
        // This is the property that lets the KEK rotate without any user re-authenticating.
        var vault = new FakeVault();
        var p = new CredentialProtector(vault);

        var v1 = await p.ProtectAsync(Secret);

        vault.KekVersion = 2;
        var v2 = await p.RewrapAsync(v1);

        Assert.NotEqual(v1.WrappedKey, v2.WrappedKey);
        Assert.StartsWith("vault:v2:", v2.WrappedKey);

        // Ciphertext untouched: rotation never decrypts or rewrites the credential itself.
        Assert.Equal(v1.Ciphertext, v2.Ciphertext);
        Assert.Equal(v1.Nonce, v2.Nonce);
        Assert.Equal(Secret, await p.UnprotectAsync(v2));
    }

    [Fact]
    public async Task Survives_a_json_persistence_round_trip()
    {
        var vault = new FakeVault();
        var p = new CredentialProtector(vault);

        var json = (await p.ProtectAsync(Secret)).ToJson();
        Assert.Equal(Secret, await p.UnprotectAsync(ProtectedCredential.FromJson(json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("unicode: ü ñ 日本語 🔑")]
    public async Task Handles_edge_case_payloads(string payload)
    {
        var p = new CredentialProtector(new FakeVault());
        Assert.Equal(payload, await p.UnprotectAsync(await p.ProtectAsync(payload)));
    }

    [Fact]
    public async Task Handles_a_realistically_long_token()
    {
        // Entra refresh tokens run to a couple of kilobytes.
        var big = new string('A', 4096);
        var p = new CredentialProtector(new FakeVault());

        Assert.Equal(big, await p.UnprotectAsync(await p.ProtectAsync(big)));
    }
}
