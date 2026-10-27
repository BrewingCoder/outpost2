using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OutPost2.Infrastructure.Security;

/// <summary>
/// Minimal Vault client covering only what OutPost2 needs: authenticate, and drive the
/// Transit engine. Raw HTTP rather than a client library — the wire format is four calls,
/// it was verified directly against the lab Vault, and it keeps a dependency out of the
/// path that guards other people's mailbox credentials.
/// </summary>
public interface IVaultTransitClient
{
    /// <summary>
    /// Asks Vault to mint a data key. Returns the plaintext DEK <b>and</b> the same key
    /// wrapped under the KEK. We encrypt with the plaintext, store only the wrapped form,
    /// and never persist the plaintext anywhere.
    /// </summary>
    Task<DataKey> GenerateDataKeyAsync(CancellationToken ct = default);

    /// <summary>Unwraps a stored DEK. The KEK itself never leaves Vault.</summary>
    Task<byte[]> DecryptDataKeyAsync(string wrappedKey, CancellationToken ct = default);

    /// <summary>
    /// Re-wraps a DEK under the current KEK version without ever exposing it.
    /// This is what makes key rotation possible without touching a single stored
    /// credential or forcing any user to re-authenticate.
    /// </summary>
    Task<string> RewrapAsync(string wrappedKey, CancellationToken ct = default);

    Task<bool> HealthyAsync(CancellationToken ct = default);
}

public readonly record struct DataKey(byte[] Plaintext, string Wrapped);

public sealed class VaultTransitClient : IVaultTransitClient, IDisposable
{
    private readonly VaultOptions _o;
    private readonly ILogger<VaultTransitClient> _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public VaultTransitClient(IOptions<VaultOptions> options, ILogger<VaultTransitClient> log)
    {
        _o = options.Value;
        _log = log;

        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(_o.CaCertPath) && File.Exists(_o.CaCertPath))
        {
            // The lab Vault presents a private-CA certificate. Pin to that CA explicitly
            // rather than disabling validation — an unvalidated TLS connection to the thing
            // holding the master key would defeat the point of having one.
            var ca = X509CertificateLoader.LoadCertificateFromFile(_o.CaCertPath);
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (cert is null || chain is null) return false;
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert);
            };
        }

        _http = new HttpClient(handler) { BaseAddress = new Uri(_o.Address), Timeout = TimeSpan.FromSeconds(20) };
    }

    // ── auth ─────────────────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - _o.RenewBefore)
            return _token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - _o.RenewBefore)
                return _token;

            switch (_o.AuthMethod)
            {
                case VaultAuthMethod.Token:
                    _token = _o.Token ?? throw new InvalidOperationException("Vault token auth selected but no token configured.");
                    _tokenExpiresAt = DateTimeOffset.MaxValue;
                    break;

                case VaultAuthMethod.AppRole:
                    await LoginAsync("/v1/auth/approle/login",
                        new { role_id = _o.RoleId, secret_id = _o.SecretId }, ct);
                    break;

                case VaultAuthMethod.Kubernetes:
                    var jwt = await File.ReadAllTextAsync(_o.KubernetesJwtPath, ct);
                    await LoginAsync("/v1/auth/kubernetes/login",
                        new { role = _o.KubernetesRole, jwt = jwt.Trim() }, ct);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported Vault auth method {_o.AuthMethod}.");
            }

            return _token!;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task LoginAsync(string path, object body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(path, body, ct);
        await ThrowIfFailed(resp, "Vault login", ct);

        var payload = await resp.Content.ReadFromJsonAsync<LoginResponse>(ct)
                      ?? throw new InvalidOperationException("Vault login returned no body.");

        _token = payload.Auth.ClientToken;
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.Auth.LeaseDuration);

        _log.LogInformation("Vault authenticated via {Method}; lease {Seconds}s, policies {Policies}.",
            _o.AuthMethod, payload.Auth.LeaseDuration, string.Join(",", payload.Auth.TokenPolicies ?? []));
    }

    // ── transit ──────────────────────────────────────────────────────────────

    public async Task<DataKey> GenerateDataKeyAsync(CancellationToken ct = default)
    {
        var r = await PostAsync<DataKeyResponse>(
            $"/v1/{_o.TransitMount}/datakey/plaintext/{_o.KeyName}", new { }, ct);

        return new DataKey(Convert.FromBase64String(r.Data.Plaintext!), r.Data.Ciphertext);
    }

    public async Task<byte[]> DecryptDataKeyAsync(string wrappedKey, CancellationToken ct = default)
    {
        var r = await PostAsync<DecryptResponse>(
            $"/v1/{_o.TransitMount}/decrypt/{_o.KeyName}", new { ciphertext = wrappedKey }, ct);

        return Convert.FromBase64String(r.Data.Plaintext);
    }

    public async Task<string> RewrapAsync(string wrappedKey, CancellationToken ct = default)
    {
        var r = await PostAsync<RewrapResponse>(
            $"/v1/{_o.TransitMount}/rewrap/{_o.KeyName}", new { ciphertext = wrappedKey }, ct);

        return r.Data.Ciphertext;
    }

    public async Task<bool> HealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/v1/sys/health", ct);
            var h = await resp.Content.ReadFromJsonAsync<HealthResponse>(ct);
            return h is { Sealed: false, Initialized: true };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vault health check failed against {Address}.", _o.Address);
            return false;
        }
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Vault-Token", token);

        using var resp = await _http.SendAsync(req, ct);

        // A rejected token means the lease ended early — drop it and retry once, so a
        // revoked or prematurely expired token costs a retry rather than a failed user action.
        if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden && _o.AuthMethod != VaultAuthMethod.Token)
        {
            _log.LogInformation("Vault rejected the cached token; re-authenticating.");
            _token = null;
            _tokenExpiresAt = DateTimeOffset.MinValue;

            var fresh = await GetTokenAsync(ct);
            using var retry = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            retry.Headers.Add("X-Vault-Token", fresh);
            using var retryResp = await _http.SendAsync(retry, ct);
            await ThrowIfFailed(retryResp, path, ct);
            return (await retryResp.Content.ReadFromJsonAsync<T>(ct))!;
        }

        await ThrowIfFailed(resp, path, ct);
        return (await resp.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private static async Task ThrowIfFailed(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        // Body may name the mount and policy, which is diagnostic, not secret — Vault
        // never echoes key material in an error.
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new VaultException($"{what} failed: HTTP {(int)resp.StatusCode}. {body}");
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }

    // ── wire types ───────────────────────────────────────────────────────────

    private sealed record LoginResponse([property: JsonPropertyName("auth")] AuthBlock Auth);
    private sealed record AuthBlock(
        [property: JsonPropertyName("client_token")] string ClientToken,
        [property: JsonPropertyName("lease_duration")] int LeaseDuration,
        [property: JsonPropertyName("token_policies")] string[]? TokenPolicies);

    private sealed record DataKeyResponse([property: JsonPropertyName("data")] DataKeyData Data);
    private sealed record DataKeyData(
        [property: JsonPropertyName("plaintext")] string? Plaintext,
        [property: JsonPropertyName("ciphertext")] string Ciphertext);

    private sealed record DecryptResponse([property: JsonPropertyName("data")] DecryptData Data);
    private sealed record DecryptData([property: JsonPropertyName("plaintext")] string Plaintext);

    private sealed record RewrapResponse([property: JsonPropertyName("data")] RewrapData Data);
    private sealed record RewrapData([property: JsonPropertyName("ciphertext")] string Ciphertext);

    private sealed record HealthResponse(
        [property: JsonPropertyName("sealed")] bool Sealed,
        [property: JsonPropertyName("initialized")] bool Initialized);
}

public sealed class VaultException(string message) : Exception(message);
