using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Connections;

public interface IJiraConnectionService
{
    Task<JiraProbe> ProbeAsync(string site, string email, string apiToken, CancellationToken ct = default);

    Task<Connection> ConnectAsync(
        Guid userId, string site, string email, string apiToken, string label, CancellationToken ct = default);

    Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default);
}

/// <param name="AccountId">Atlassian's opaque account id: what "assign to me" is sent as.</param>
public sealed record JiraProbe(bool Ok, string? AccountId, string? DisplayName, string? Error);

/// <summary>
/// Connecting a Jira Cloud site with an Atlassian account email and API token (OP2-82).
/// <para>
/// Basic auth over the site's own address, not OAuth 2.0 (3LO). It is the honor-system
/// shape every other pasted-token connection here has — paste, probe, store — and it
/// needs no app registered with Atlassian by whoever runs this server. The cost is the
/// same as YouTrack's: the token can do whatever the account can, and the capability
/// switches are Outpost's own restraint. 3LO would buy enforced scopes and is the better
/// answer if this is ever offered to people who do not trust the deployment.
/// </para>
/// <para>
/// The credential is stored as <c>email:token</c>, which is exactly what Basic encodes,
/// so the service never has to put the pair back together. A classic, unscoped API token
/// is assumed: Atlassian's scoped tokens only work through <c>api.atlassian.com/ex/jira/
/// {cloudId}</c> and are refused at the site address, which the probe reports as a
/// rejected credential.
/// </para>
/// </summary>
public sealed class JiraConnectionService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<JiraConnectionService> log) : IJiraConnectionService
{
    public static HttpClient Configure(HttpClient http, string site, string credential)
    {
        http.BaseAddress = new Uri(site.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credential)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2/1.0");
        return http;
    }

    /// <summary>The stored credential: what Basic sends, with the email trimmed.</summary>
    public static string Credential(string email, string apiToken) => $"{email.Trim()}:{apiToken.Trim()}";

    /// <summary>
    /// Accepts what a person would type or paste — "acme", "acme.atlassian.net", or any URL
    /// on the site such as a board link — and returns the site root with no trailing slash.
    /// A bare word is taken as an Atlassian site name, because that is what people call
    /// their Jira. Anything with a dot is kept as given: Jira Cloud also serves custom domains.
    /// </summary>
    public static string Normalise(string site)
    {
        var u = (site ?? "").Trim().TrimEnd('/');
        if (u.Length == 0) throw new InvalidOperationException("Enter your Jira site, e.g. acme.atlassian.net.");

        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!u.Contains('.') && !u.Contains('/') && !u.Contains(':')) u += ".atlassian.net";
            u = "https://" + u;
        }

        if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host)
            || parsed.Host.Contains(' '))
            throw new InvalidOperationException($"\"{site}\" isn't a Jira site address.");

        // The path is dropped on purpose: a Jira Cloud site is always served from its root,
        // and what people paste is usually a board or issue link somewhere beneath it.
        return $"{parsed.Scheme}://{parsed.Authority}";
    }

    public async Task<JiraProbe> ProbeAsync(string site, string email, string apiToken, CancellationToken ct = default)
    {
        string root;
        try { root = Normalise(site); }
        catch (InvalidOperationException ex) { return new JiraProbe(false, null, null, ex.Message); }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return new JiraProbe(false, null, null,
                "Enter the email address you sign in to Atlassian with. Jira takes it together with the API token.");

        var host = new Uri(root).Host;

        try
        {
            using var http = Configure(httpFactory.CreateClient("jira"), root, Credential(email, apiToken));
            using var resp = await http.GetAsync("rest/api/3/myself", ct);

            // A site that does not exist is not a 404 from Jira: Atlassian answers on the
            // wildcard and sends the browser somewhere else. Following that redirect lands
            // on a page that is not this site at all.
            if (resp.RequestMessage?.RequestUri is { } landed
                && !string.Equals(landed.Host, host, StringComparison.OrdinalIgnoreCase))
                return new JiraProbe(false, null, null,
                    $"There's no Jira Cloud site at {host} — it sent Outpost on to {landed.Host}. Check the site name.");

            if (resp.StatusCode is HttpStatusCode.Unauthorized)
                return new JiraProbe(false, null, null,
                    $"{host} rejected that email and API token. Use the email you sign in to Atlassian with, and an "
                    + "API token from id.atlassian.com (a classic token, not a scoped one, and not your password).");

            if (resp.StatusCode is HttpStatusCode.Forbidden)
            {
                // After repeated failures Atlassian stops checking the password and wants a
                // CAPTCHA solved in a browser. No credential will work until somebody does.
                var reason = resp.Headers.TryGetValues("X-Seraph-LoginReason", out var r) ? string.Join(",", r) : "";
                return new JiraProbe(false, null, null,
                    reason.Contains("AUTHENTICATION_DENIED", StringComparison.OrdinalIgnoreCase)
                        ? $"{host} wants a CAPTCHA solved for this account after failed sign-ins. Sign in to "
                          + "Jira once in a browser, then connect again."
                        : $"That account signs in, but {host} won't let it use Jira. Ask a site admin for Jira access.");
            }

            if (resp.StatusCode is HttpStatusCode.NotFound)
                return new JiraProbe(false, null, null,
                    $"No Jira Cloud site at {host}. Check the site name. (Jira Server and Data Center aren't supported.)");

            if (!resp.IsSuccessStatusCode)
                return new JiraProbe(false, null, null, $"{host} returned HTTP {(int)resp.StatusCode}.");

            Myself? me;
            try { me = await resp.Content.ReadFromJsonAsync<Myself>(ct); }
            catch (System.Text.Json.JsonException)
            {
                return new JiraProbe(false, null, null,
                    $"Something answered at {host}, but it isn't the Jira Cloud API.");
            }

            if (me is null || string.IsNullOrWhiteSpace(me.AccountId))
                return new JiraProbe(false, null, null, $"{host} returned an unreadable response.");

            return new JiraProbe(true, me.AccountId, me.DisplayName, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new JiraProbe(false, null, null, $"Could not reach {host}: {ex.Message}");
        }
    }

    public async Task<Connection> ConnectAsync(
        Guid userId, string site, string email, string apiToken, string label, CancellationToken ct = default)
    {
        var probe = await ProbeAsync(site, email, apiToken, ct);
        if (!probe.Ok) throw new InvalidOperationException(probe.Error ?? "Credential validation failed.");

        var instance = Normalise(site);

        // As for YouTrack: read and write on, and no admin at all (see the catalog). A
        // comment or a transition is visible to the team, like YouTrack's; it is not mail
        // sent under the user's name to somebody outside, which is where Outpost starts off.
        var capabilities = new List<Capability>
        {
            new() { Name = Capabilities.IssuesRead,  Granted = true, Enabled = true, IsSelfImposedOnly = true },
            new() { Name = Capabilities.IssuesWrite, Granted = true, Enabled = true, IsSelfImposedOnly = true },
        };

        log.LogInformation("Connecting Jira {Instance} as {Account} (label {Label}).", instance, probe.AccountId, label);

        var connection = await connections.UpsertAsync(
            userId, ProviderKind.Jira, instance, probe.AccountId,
            label, AuthKind.PersonalAccessToken, Credential(email, apiToken), capabilities, ct);

        // The email the person typed, not the one /myself reports: Atlassian hides that
        // field under the account's privacy settings, and the typed one demonstrably signs in.
        await connections.SetAccountIdentityAsync(connection, email.Trim(), isPersonal: false, ct);

        // API tokens do expire now (a year at most, chosen when made), but nothing in the
        // API reports when. Null reads as "not known", which is the truth.
        await connections.SetCredentialExpiryAsync(connection, null, ct);

        return connection;
    }

    public async Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default)
    {
        var credential = await connections.GetCredentialAsync(connection, ct);
        if (credential is null) return false;

        var at = credential.IndexOf(':');
        var probe = at < 0
            ? new JiraProbe(false, null, null, "The stored credential is malformed.")
            : await ProbeAsync(connection.Instance, credential[..at], credential[(at + 1)..], ct);

        await connections.SetStateAsync(connection.UserId, connection.Id,
            probe.Ok ? ConnectionState.Active : ConnectionState.Expired, ct);

        if (!probe.Ok)
            log.LogWarning("Jira connection {Label} failed its health check: {Error}", connection.Label, probe.Error);

        return probe.Ok;
    }

    private sealed record Myself(
        [property: JsonPropertyName("accountId")] string? AccountId,
        [property: JsonPropertyName("displayName")] string? DisplayName);
}
