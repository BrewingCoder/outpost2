using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OutPost2.Infrastructure.Security;

/// <summary>
/// Envelope encryption for stored credentials — OAuth refresh tokens and YouTrack PATs
/// (OP2-5, OP2-15).
/// <para>
/// Vault mints a fresh data key per credential; we encrypt locally with AES-256-GCM and
/// persist only the <b>wrapped</b> key alongside the ciphertext. The key-encryption key
/// never leaves Vault, so a stolen database is inert without live Vault access.
/// </para>
/// <para>
/// Per-credential data keys, rather than one key for everything, mean rotation and
/// compromise are both bounded to a single connection.
/// </para>
/// </summary>
public interface ICredentialProtector
{
    Task<ProtectedCredential> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> UnprotectAsync(ProtectedCredential sealedCredential, CancellationToken ct = default);

    /// <summary>
    /// Re-wraps the data key under the current KEK version. The ciphertext is untouched,
    /// so rotation costs one Vault call per credential and <b>no user re-authenticates</b>.
    /// </summary>
    Task<ProtectedCredential> RewrapAsync(ProtectedCredential sealedCredential, CancellationToken ct = default);
}

/// <summary>
/// The stored form. Safe to persist: without Vault it is noise. Serialised as compact JSON
/// into <c>Connection.CredentialRef</c>'s backing store.
/// </summary>
public sealed record ProtectedCredential
{
    /// <summary>The data key, wrapped by Vault. Format: <c>vault:v1:...</c> — the version marks the KEK generation.</summary>
    [JsonPropertyName("k")] public required string WrappedKey { get; init; }

    /// <summary>96-bit GCM nonce. Fresh per encryption; never reused with the same key.</summary>
    [JsonPropertyName("n")] public required string Nonce { get; init; }

    [JsonPropertyName("c")] public required string Ciphertext { get; init; }

    /// <summary>128-bit GCM authentication tag — this is what makes the ciphertext tamper-evident.</summary>
    [JsonPropertyName("t")] public required string Tag { get; init; }

    [JsonPropertyName("v")] public int Version { get; init; } = 1;

    public string ToJson() => JsonSerializer.Serialize(this);

    public static ProtectedCredential FromJson(string json) =>
        JsonSerializer.Deserialize<ProtectedCredential>(json)
        ?? throw new CryptographicException("Stored credential could not be parsed.");
}

public sealed class CredentialProtector(IVaultTransitClient vault) : ICredentialProtector
{
    private const int NonceBytes = 12;   // 96 bits — the GCM standard; other sizes weaken it
    private const int TagBytes = 16;     // 128 bits — full-strength authentication tag

    public async Task<ProtectedCredential> ProtectAsync(string plaintext, CancellationToken ct = default)
    {
        var dataKey = await vault.GenerateDataKeyAsync(ct);
        try
        {
            var payload = Encoding.UTF8.GetBytes(plaintext);
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ciphertext = new byte[payload.Length];
            var tag = new byte[TagBytes];

            using var aes = new AesGcm(dataKey.Plaintext, TagBytes);
            aes.Encrypt(nonce, payload, ciphertext, tag);

            CryptographicOperations.ZeroMemory(payload);

            return new ProtectedCredential
            {
                WrappedKey = dataKey.Wrapped,
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag),
            };
        }
        finally
        {
            // The plaintext DEK must not outlive this method. Vault keeps the wrapped copy;
            // anything we leave in memory is a credential waiting to be read out of a dump.
            CryptographicOperations.ZeroMemory(dataKey.Plaintext);
        }
    }

    public async Task<string> UnprotectAsync(ProtectedCredential sealedCredential, CancellationToken ct = default)
    {
        var key = await vault.DecryptDataKeyAsync(sealedCredential.WrappedKey, ct);
        try
        {
            var ciphertext = Convert.FromBase64String(sealedCredential.Ciphertext);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, TagBytes);

            // Throws AuthenticationTagMismatchException if the stored bytes were altered.
            // We let that surface rather than returning anything — a credential that fails
            // authentication is not a credential.
            aes.Decrypt(Convert.FromBase64String(sealedCredential.Nonce), ciphertext,
                        Convert.FromBase64String(sealedCredential.Tag), plaintext);

            var result = Encoding.UTF8.GetString(plaintext);
            CryptographicOperations.ZeroMemory(plaintext);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<ProtectedCredential> RewrapAsync(ProtectedCredential sealedCredential, CancellationToken ct = default)
    {
        // Vault re-wraps under the newest KEK version without ever revealing the data key,
        // so the credential is never decrypted and never leaves storage in the clear.
        var rewrapped = await vault.RewrapAsync(sealedCredential.WrappedKey, ct);
        return sealedCredential with { WrappedKey = rewrapped };
    }
}
