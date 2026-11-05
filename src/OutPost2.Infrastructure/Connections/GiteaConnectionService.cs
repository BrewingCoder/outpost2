using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Connections;

public interface IGiteaConnectionService
{
    Task<GiteaProbe> ProbeAsync(string baseUrl, string token, CancellationToken ct = default);

    Task<Connection> ConnectAsync(
        Guid userId, string baseUrl, string token, string label, CancellationToken ct = default);

    Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default);
}

/// <param name="Version">
/// What <c>/version</c> answered — "1.22.3" from Gitea, "9.0.1+gitea-1.22.0" from Forgejo.
/// Logged rather than stored: nothing behaves differently by it today.
/// </param>
public sealed record GiteaProbe(bool Ok, string? Login, string? Name, string? Error, string? Version = null);

/// <summary>
/// Connecting a self-hosted Gitea or Forgejo server with a personal access token (OP2-84).
/// <para>
/// Shaped like <see cref="GitHubConnectionService"/> on purpose — a PAT, a probe of
/// <c>/user</c>, read on and write off — with the differences that matter: there is no
/// public default host, so the server is required; the API lives under <c>/api/v1</c> on
/// the site's own host; and the token goes in a <c>token</c> authorization header, which
/// every Gitea and Forgejo release accepts (<c>Bearer</c> only arrived later).
/// </para>
/// </summary>
public sealed class GiteaConnectionService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<GiteaConnectionService> log) : IGiteaConnectionService
{
    public static HttpClient Configure(HttpClient http, string apiBase, string token)
    {
        http.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2/1.0");
        return http;
    }

    /// <summary>
    /// Accepts what a person would type — "git.example.com", "https://git.example.com/",
    /// a server under a sub-path, or the API URL itself — and returns the API base, ending
    /// in <c>/api/v1/</c>. Blank is refused: unlike GitHub there is no default server.
    /// </summary>
    public static string Normalise(string baseUrl)
    {
        var u = (baseUrl ?? "").Trim().TrimEnd('/');
        if (u.Length == 0) throw new InvalidOperationException("Enter your Gitea or Forgejo server's address.");

        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u;

        if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
            throw new InvalidOperationException($"\"{baseUrl}\" isn't a server address.");

        // Whatever came after the host, keep it: a server can live under a sub-path
        // (https://example.com/git). Cut anything from /api on, then put /api/v1 back,
        // so the site URL and the API URL both land in the same place.
        var path = parsed.AbsolutePath.TrimEnd('/');

        // The SEGMENT "/api", not the first place those four characters appear: a server
        // under a sub-path of its own such as https://example.com/api-git keeps it, and
        // only a real /api/... is cut.
        for (var at = path.IndexOf("/api", StringComparison.OrdinalIgnoreCase); at >= 0;
             at = path.IndexOf("/api", at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = at + 4;
            if (after == path.Length || path[after] == '/') { path = path[..at]; break; }
        }

        return $"{parsed.Scheme}://{parsed.Authority}{path}/api/v1/";
    }

    public async Task<GiteaProbe> ProbeAsync(string baseUrl, string token, CancellationToken ct = default)
    {
        string apiBase;
        try { apiBase = Normalise(baseUrl); }
        catch (InvalidOperationException ex) { return new GiteaProbe(false, null, null, ex.Message); }

        try
        {
            using var http = Configure(httpFactory.CreateClient("gitea"), apiBase, token);
            using var resp = await http.GetAsync("user", ct);

            if (resp.StatusCode is HttpStatusCode.Unauthorized)
                return new GiteaProbe(false, null, null, "That token was rejected by the server.");

            // A scoped token without read:user authenticates but cannot read /user, which
            // is what Outpost needs to know whose reviews and assignments to look for.
            if (resp.StatusCode is HttpStatusCode.Forbidden)
                return new GiteaProbe(false, null, null,
                    "That token is valid but can't read your account. Give it the read:user scope, "
                    + "plus read:repository and read:issue.");

            if (resp.StatusCode is HttpStatusCode.NotFound)
                return new GiteaProbe(false, null, null,
                    $"No Gitea or Forgejo API at {apiBase.TrimEnd('/')}. Check the server address.");

            if (!resp.IsSuccessStatusCode)
                return new GiteaProbe(false, null, null, $"The server returned HTTP {(int)resp.StatusCode}.");

            UserResponse? me;
            try { me = await resp.Content.ReadFromJsonAsync<UserResponse>(ct); }
            catch (System.Text.Json.JsonException)
            {
                // A login page or a proxy's HTML: something answered, but not the API.
                return new GiteaProbe(false, null, null,
                    $"Something answered at {apiBase.TrimEnd('/')}, but it isn't a Gitea or Forgejo API.");
            }

            if (me is null || string.IsNullOrWhiteSpace(me.Login))
                return new GiteaProbe(false, null, null, "The server returned an unreadable response.");

            return new GiteaProbe(true, me.Login, me.FullName, null, await VersionAsync(http, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new GiteaProbe(false, null, null, $"Could not reach that server: {ex.Message}");
        }
    }

    /// <summary>Best effort; an old server or a locked-down one may not say.</summary>
    private static async Task<string?> VersionAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync("version", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return (await resp.Content.ReadFromJsonAsync<VersionResponse>(ct))?.Version;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { return null; }
    }

    public async Task<Connection> ConnectAsync(
        Guid userId, string baseUrl, string token, string label, CancellationToken ct = default)
    {
        var probe = await ProbeAsync(baseUrl, token, ct);
        if (!probe.Ok) throw new InvalidOperationException(probe.Error ?? "Token validation failed.");

        var instance = Normalise(baseUrl).TrimEnd('/');

        // As for GitHub: write starts OFF. A comment or a closed issue is seen by other
        // people and cannot be taken back.
        var capabilities = new List<Capability>
        {
            new() { Name = Capabilities.CodeRead,  Granted = true, Enabled = true,  IsSelfImposedOnly = true },
            new() { Name = Capabilities.CodeWrite, Granted = true, Enabled = false, IsSelfImposedOnly = true },
        };

        log.LogInformation("Connecting {Flavour} {Instance} (version {Version}) as {Login} (label {Label}).",
            probe.Version?.Contains("gitea", StringComparison.OrdinalIgnoreCase) == true ? "Forgejo" : "Gitea",
            instance, probe.Version ?? "unknown", probe.Login, label);

        var connection = await connections.UpsertAsync(
            userId, ProviderKind.Gitea, instance, probe.Login,
            label, AuthKind.PersonalAccessToken, token, capabilities, ct);

        await connections.SetAccountIdentityAsync(connection, probe.Login, isPersonal: false, ct);

        // Gitea tokens carry no expiry, and the API does not report one. Null reads as
        // "not known", which is the truth.
        await connections.SetCredentialExpiryAsync(connection, null, ct);

        return connection;
    }

    public async Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default)
    {
        var token = await connections.GetCredentialAsync(connection, ct);
        if (token is null) return false;

        var probe = await ProbeAsync(connection.Instance, token, ct);
        await connections.SetStateAsync(connection.UserId, connection.Id,
            probe.Ok ? ConnectionState.Active : ConnectionState.Expired, ct);
        return probe.Ok;
    }

    private sealed record UserResponse(
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("full_name")] string? FullName);

    private sealed record VersionResponse([property: JsonPropertyName("version")] string? Version);
}
