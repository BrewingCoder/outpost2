using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Connections;

public interface IGitHubConnectionService
{
    Task<GitHubProbe> ProbeAsync(string baseUrl, string token, CancellationToken ct = default);

    Task<Connection> ConnectAsync(
        Guid userId, string baseUrl, string token, string label, CancellationToken ct = default);

    Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default);
}

public sealed record GitHubProbe(
    bool Ok, string? Login, string? Name, string? Error, DateTimeOffset? TokenExpiresAt = null);

public sealed class GitHubConnectionService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<GitHubConnectionService> log) : IGitHubConnectionService
{
    /// <summary>github.com's API lives on a different host from the site, unlike Enterprise.</summary>
    public const string DotComApi = "https://api.github.com";

    private HttpClient Client(string baseUrl, string token)
    {
        var http = httpFactory.CreateClient("github");
        http.BaseAddress = new Uri(Normalise(baseUrl));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        // GitHub rejects requests with no user agent outright, and pins response shape to
        // the API version header — without it a future default could change field names
        // under us with no deploy on our side.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2/1.0");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    /// <summary>
    /// Accepts what a person would actually type. "github.com" is the site, not the API,
    /// and pointing a client at it yields HTML — so that one specific case is redirected
    /// to api.github.com rather than failing with a confusing parse error. An Enterprise
    /// host keeps its own hostname and gains the /api/v3 prefix it serves from.
    /// </summary>
    public static string Normalise(string baseUrl)
    {
        var u = baseUrl.Trim().TrimEnd('/');
        if (u.Length == 0) return DotComApi + "/";

        if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;

        var host = new Uri(u).Host;
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            return DotComApi + "/";

        if (!u.Contains("/api/", StringComparison.OrdinalIgnoreCase)) u += "/api/v3";
        return u + "/";
    }

    public async Task<GitHubProbe> ProbeAsync(string baseUrl, string token, CancellationToken ct = default)
    {
        try
        {
            using var http = Client(baseUrl, token);
            using var resp = await http.GetAsync("user", ct);

            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                return new GitHubProbe(false, null, null, "That token was rejected by GitHub.");

            // A fine-grained token with no account permissions authenticates but cannot
            // read /user. Saying "forbidden" would send the user looking at the wrong
            // thing, so name the actual fix.
            if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden)
                return new GitHubProbe(false, null, null,
                    "That token is valid but has no access to your account. " +
                    "A fine-grained token needs the 'Profile' or account read permission.");

            if (!resp.IsSuccessStatusCode)
                return new GitHubProbe(false, null, null, $"GitHub returned HTTP {(int)resp.StatusCode}.");

            var me = await resp.Content.ReadFromJsonAsync<UserResponse>(ct);
            if (me is null || string.IsNullOrWhiteSpace(me.Login))
                return new GitHubProbe(false, null, null, "GitHub returned an unreadable response.");

            return new GitHubProbe(true, me.Login, me.Name, null, ReadExpiry(resp));
        }
        catch (Exception ex)
        {
            return new GitHubProbe(false, null, null, $"Could not reach that server: {ex.Message}");
        }
    }

    public async Task<Connection> ConnectAsync(
        Guid userId, string baseUrl, string token, string label, CancellationToken ct = default)
    {
        var probe = await ProbeAsync(baseUrl, token, ct);
        if (!probe.Ok) throw new InvalidOperationException(probe.Error ?? "Token validation failed.");

        var instance = Normalise(baseUrl).TrimEnd('/');

        // Write starts OFF, as it does for YouTrack. Closing someone else's issue or
        // posting a comment under your name is visible to other people and cannot be
        // taken back, so it should not ride in on a token pasted to check PR counts.
        var capabilities = new List<Capability>
        {
            new() { Name = Capabilities.CodeRead,  Granted = true, Enabled = true,  IsSelfImposedOnly = true },
            new() { Name = Capabilities.CodeWrite, Granted = true, Enabled = false, IsSelfImposedOnly = true },
        };

        log.LogInformation("Connecting GitHub {Instance} as {Login} (label {Label}).",
            instance, probe.Login, label);

        var connection = await connections.UpsertAsync(
            userId, ProviderKind.GitHub, instance, probe.Login,
            label, AuthKind.PersonalAccessToken, token, capabilities, ct);

        await connections.SetAccountIdentityAsync(connection, probe.Login, isPersonal: false, ct);
        await connections.SetCredentialExpiryAsync(connection, probe.TokenExpiresAt, ct);

        return connection;
    }

    public async Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default)
    {
        var token = await connections.GetCredentialAsync(connection, ct);
        if (token is null) return false;

        var probe = await ProbeAsync(connection.Instance, token, ct);

        // Refreshed on every health check: a token can be rotated or shortened on
        // GitHub's side, and a stale date is worse than none because it reads as fact.
        if (probe.Ok) await connections.SetCredentialExpiryAsync(connection, probe.TokenExpiresAt, ct);

        // A PAT has no refresh and no advance warning before it expires, so the stored
        // state is the only thing that can tell the user it has lapsed.
        await connections.SetStateAsync(connection.UserId, connection.Id,
            probe.Ok ? ConnectionState.Active : ConnectionState.Expired, ct);

        return probe.Ok;
    }

    /// <summary>
    /// GitHub states a token's expiry on every response, so this costs nothing and is the
    /// only provider here that can be truthful about it. Classic tokens created without an
    /// expiry simply omit the header, which correctly reads as "not known".
    /// </summary>
    private static DateTimeOffset? ReadExpiry(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("github-authentication-token-expiration", out var values))
            return null;

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Sent as "2027-03-10 12:18:17 UTC", which no standard parser accepts as-is.
        raw = raw.Trim();
        if (raw.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^4] + "Z";

        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private sealed record UserResponse(
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("name")] string? Name);
}
