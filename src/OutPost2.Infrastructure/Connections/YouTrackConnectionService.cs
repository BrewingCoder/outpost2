using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Connections;

public interface IYouTrackConnectionService
{
    /// <summary>
    /// Validates a pasted token against the server before we store it, and reports which
    /// account and host it resolved to.
    /// </summary>
    Task<YouTrackProbe> ProbeAsync(string baseUrl, string token, CancellationToken ct = default);

    Task<Connection> ConnectAsync(
        Guid userId, string baseUrl, string token, string label, CancellationToken ct = default);

    /// <summary>
    /// PATs give no advance warning of expiry — the first sign of trouble is a 401 mid-use.
    /// So health is polled rather than inferred, and the connection state updated.
    /// </summary>
    Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default);
}

public sealed record YouTrackProbe(bool Ok, string? Login, string? FullName, string? Error);

public sealed class YouTrackConnectionService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<YouTrackConnectionService> log) : IYouTrackConnectionService
{
    private HttpClient Client(string baseUrl, string token)
    {
        var http = httpFactory.CreateClient("youtrack");
        http.BaseAddress = new Uri(Normalise(baseUrl));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Some YouTrack instances sit behind Cloudflare, which 1010-blocks unfamiliar
        // user agents. Learned the hard way against youtrack.example.com, where the default
        // .NET agent is refused outright.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2/1.0");
        return http;
    }

    private static string Normalise(string baseUrl)
    {
        var u = baseUrl.Trim().TrimEnd('/');
        if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
        return u + "/";
    }

    public async Task<YouTrackProbe> ProbeAsync(string baseUrl, string token, CancellationToken ct = default)
    {
        try
        {
            using var http = Client(baseUrl, token);
            using var resp = await http.GetAsync("api/users/me?fields=login,fullName,guest", ct);

            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                return new YouTrackProbe(false, null, null, "That token was rejected by the server.");

            if (!resp.IsSuccessStatusCode)
                return new YouTrackProbe(false, null, null, $"Server returned HTTP {(int)resp.StatusCode}.");

            var me = await resp.Content.ReadFromJsonAsync<MeResponse>(ct);
            if (me is null) return new YouTrackProbe(false, null, null, "Server returned an unreadable response.");

            // A guest identity means the token is not actually authenticating anyone.
            if (me.Guest) return new YouTrackProbe(false, null, null, "That token resolves to a guest account.");

            return new YouTrackProbe(true, me.Login, me.FullName, null);
        }
        catch (Exception ex)
        {
            return new YouTrackProbe(false, null, null, $"Could not reach that server: {ex.Message}");
        }
    }

    public async Task<Connection> ConnectAsync(
        Guid userId, string baseUrl, string token, string label, CancellationToken ct = default)
    {
        // Validate before storing. A token that does not authenticate is not worth keeping,
        // and finding out later turns a clear setup error into a mystery failure in a
        // voice turn.
        var probe = await ProbeAsync(baseUrl, token, ct);
        if (!probe.Ok) throw new InvalidOperationException(probe.Error ?? "Token validation failed.");

        var instance = Normalise(baseUrl).TrimEnd('/');

        // Everything a PAT can do, we can do — the token's own scope is whatever the user
        // created, and we cannot narrow it. These toggles are OUR restraint, not an
        // enforced boundary, which is why they are flagged self-imposed. Admin starts OFF:
        // "create a project" and "read my tickets" must not be one switch when a voice turn
        // can trigger either.
        var capabilities = new List<Capability>
        {
            new() { Name = Capabilities.IssuesRead,  Granted = true, Enabled = true,  IsSelfImposedOnly = true },
            new() { Name = Capabilities.IssuesWrite, Granted = true, Enabled = true,  IsSelfImposedOnly = true },
            new() { Name = Capabilities.IssuesAdmin, Granted = true, Enabled = false, IsSelfImposedOnly = true },
        };

        log.LogInformation("Connecting YouTrack {Instance} as {Login} (label {Label}).",
            instance, probe.Login, label);

        var connection = await connections.UpsertAsync(
            userId, ProviderKind.YouTrack, instance, probe.Login,
            label, AuthKind.PersonalAccessToken, token, capabilities, ct);

        // Record WHO the token authenticates as, so the UI can show it. A label alone does
        // not tell you which identity is behind a connection, and a Microsoft connection
        // showing only an opaque tenant GUID is exactly how the wrong mailbox got attached
        // once already. A tracker reached with an admin PAT deserves the same scrutiny.
        await connections.SetAccountIdentityAsync(connection, probe.Login, isPersonal: false, ct);

        return connection;
    }

    public async Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default)
    {
        var token = await connections.GetCredentialAsync(connection, ct);
        if (token is null) return false;

        var probe = await ProbeAsync(connection.Instance, token, ct);

        // Record the verdict so the UI and the resolver see a current state rather than a
        // stale "active" that only fails when someone actually asks for something.
        await connections.SetStateAsync(connection.UserId, connection.Id,
            probe.Ok ? ConnectionState.Active : ConnectionState.Expired, ct);

        if (!probe.Ok)
            log.LogWarning("YouTrack connection {Label} failed its health check: {Error}", connection.Label, probe.Error);

        return probe.Ok;
    }

    private sealed record MeResponse(
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("fullName")] string? FullName,
        [property: JsonPropertyName("guest")] bool Guest);
}
