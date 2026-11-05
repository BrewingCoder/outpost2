using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Connections;

public interface IBitbucketConnectionService
{
    Task<BitbucketProbe> ProbeAsync(string workspace, string? email, string token, CancellationToken ct = default);

    Task<Connection> ConnectAsync(
        Guid userId, string workspace, string? email, string token, string label, CancellationToken ct = default);

    /// <summary>
    /// A new token for an existing connection, keeping the email (if any) it was made with:
    /// the replace-token form asks for the token alone.
    /// </summary>
    Task<Connection> ReplaceTokenAsync(Guid userId, Connection connection, string token, CancellationToken ct = default);

    Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default);
}

/// <param name="Uuid">The account's <c>{uuid}</c> — what reviewer and author filters match on.</param>
/// <param name="Login">The account's nickname, for saying who Outpost is acting as.</param>
public sealed record BitbucketProbe(bool Ok, string? Uuid, string? Login, string? Name, string? Error);

/// <summary>
/// Connecting one Bitbucket Cloud workspace with an Atlassian API token (OP2-293).
/// <para>
/// The credential is an API token, because it is the analogue of the PATs the other code
/// providers take: a person's own token, carrying their identity, so "awaiting my review"
/// and "mine" mean them. App passwords are not offered — Atlassian stopped issuing them in
/// September 2025 and switched them off in June 2026. Repository and workspace access
/// tokens are not a person, so "me" would be a bot account.
/// </para>
/// <para>
/// Sent as <c>Bearer</c>, which Atlassian documents for API tokens and which needs no
/// email. If an email is given, it is sent as <c>Basic email:token</c> instead — the other
/// documented form — for an account where the bearer form is refused. The stored
/// credential is then the <c>email:token</c> pair itself; API tokens contain no colon,
/// so the two are told apart by one.
/// </para>
/// <para>
/// One workspace per connection. Bitbucket has no cross-workspace search, and a
/// workspace is the unit everything else is listed by, the way a Gitea server is.
/// </para>
/// </summary>
public sealed class BitbucketConnectionService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<BitbucketConnectionService> log) : IBitbucketConnectionService
{
    public const string ApiBase = "https://api.bitbucket.org/2.0/";
    public const string SiteBase = "https://bitbucket.org/";

    public static HttpClient Configure(HttpClient http, string credential)
    {
        http.BaseAddress = new Uri(ApiBase);
        http.DefaultRequestHeaders.Authorization = credential.Contains(':')
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credential)))
            : new AuthenticationHeaderValue("Bearer", credential);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2/1.0");
        return http;
    }

    /// <summary>What is stored: the token alone, or <c>email:token</c> when an email was given.</summary>
    public static string Credential(string? email, string token) =>
        string.IsNullOrWhiteSpace(email) ? token.Trim() : $"{email.Trim()}:{token.Trim()}";

    /// <summary>
    /// Accepts what a person would type or paste — "acme", "https://bitbucket.org/acme",
    /// "bitbucket.org/acme/some-repo" — and returns the workspace slug.
    /// </summary>
    public static string NormaliseWorkspace(string workspace)
    {
        var w = (workspace ?? "").Trim().Trim('/');
        if (w.Length == 0) throw new InvalidOperationException("Enter the Bitbucket workspace to connect.");

        foreach (var prefix in new[] { "https://", "http://" })
            if (w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) w = w[prefix.Length..];

        foreach (var host in new[] { "www.bitbucket.org/", "bitbucket.org/", "api.bitbucket.org/2.0/workspaces/",
                     "api.bitbucket.org/2.0/repositories/" })
            if (w.StartsWith(host, StringComparison.OrdinalIgnoreCase)) { w = w[host.Length..]; break; }

        w = w.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        // Workspace slugs are lowercase letters, digits, '-', '_' and '.'. Anything else is
        // a typo or a URL we did not recognise, and is said as such rather than sent.
        if (w.Length == 0 || !w.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            throw new InvalidOperationException($"\"{workspace}\" isn't a Bitbucket workspace name.");

        return w.ToLowerInvariant();
    }

    /// <summary>The connection's workspace, from the instance it was stored under.</summary>
    public static string WorkspaceOf(Connection c) =>
        Uri.TryCreate(c.Instance, UriKind.Absolute, out var u) && u.Segments.Length > 1
            ? u.Segments[1].Trim('/')
            : c.Instance;

    public static string InstanceFor(string workspace) => SiteBase + workspace;

    public async Task<BitbucketProbe> ProbeAsync(
        string workspace, string? email, string token, CancellationToken ct = default)
    {
        string ws;
        try { ws = NormaliseWorkspace(workspace); }
        catch (InvalidOperationException ex) { return new BitbucketProbe(false, null, null, null, ex.Message); }

        if (string.IsNullOrWhiteSpace(token))
            return new BitbucketProbe(false, null, null, null, "Paste an Atlassian API token.");

        try
        {
            using var http = Configure(httpFactory.CreateClient("bitbucket"), Credential(email, token));

            // 1. Whose token is it. A rejected credential is the commonest failure, and it
            //    has its own sentence — including the app password that no longer works.
            UserResponse? me;
            using (var resp = await http.GetAsync("user", ct))
            {
                if (resp.StatusCode is HttpStatusCode.Unauthorized)
                    return new BitbucketProbe(false, null, null, null,
                        "Bitbucket rejected that token. Use an Atlassian API token with Bitbucket scopes — app "
                        + "passwords no longer work"
                        + (string.IsNullOrWhiteSpace(email)
                            ? ". If it is an API token, try again with your Atlassian account email filled in."
                            : ", and the email has to be the Atlassian account the token belongs to."));

                if (resp.StatusCode is HttpStatusCode.Forbidden)
                    return new BitbucketProbe(false, null, null, null,
                        "That token is valid but can't read your account. Give it the read:user:bitbucket scope, "
                        + "plus read:repository:bitbucket and read:pullrequest:bitbucket.");

                if (!resp.IsSuccessStatusCode)
                    return new BitbucketProbe(false, null, null, null, $"Bitbucket returned HTTP {(int)resp.StatusCode}.");

                try { me = await resp.Content.ReadFromJsonAsync<UserResponse>(ct); }
                catch (System.Text.Json.JsonException) { me = null; }

                if (me is null || string.IsNullOrWhiteSpace(me.Uuid))
                    return new BitbucketProbe(false, null, null, null, "Bitbucket returned an unreadable account.");
            }

            // 2. Does the workspace exist, and 3. can this token see anything in it. One
            //    call answers both: the listing is 404 for a workspace that does not exist
            //    and an empty page for one that does but shows this token nothing.
            using (var resp = await http.GetAsync($"repositories/{Uri.EscapeDataString(ws)}?pagelen=1", ct))
            {
                if (resp.StatusCode is HttpStatusCode.NotFound)
                    return new BitbucketProbe(false, null, null, null,
                        $"There is no Bitbucket workspace called '{ws}'. Use the name in your workspace's URL, "
                        + "bitbucket.org/<workspace>.");

                if (resp.StatusCode is HttpStatusCode.Forbidden)
                    return new BitbucketProbe(false, null, null, null,
                        $"That token can't list repositories in {ws}. Give it the read:repository:bitbucket scope.");

                if (!resp.IsSuccessStatusCode)
                    return new BitbucketProbe(false, null, null, null,
                        $"Bitbucket returned HTTP {(int)resp.StatusCode} listing repositories in {ws}.");

                RepoPage? page;
                try { page = await resp.Content.ReadFromJsonAsync<RepoPage>(ct); }
                catch (System.Text.Json.JsonException) { page = null; }

                if (page?.Values is not { Count: > 0 })
                    return new BitbucketProbe(false, null, null, null,
                        $"{me.Nickname ?? me.DisplayName ?? "This account"} can see no repositories in the {ws} "
                        + "workspace, so there would be nothing for Outpost to read. Check you're a member of it, "
                        + "or connect a workspace that has repositories.");
            }

            return new BitbucketProbe(true, me.Uuid, me.Nickname ?? me.DisplayName, me.DisplayName, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new BitbucketProbe(false, null, null, null, $"Could not reach Bitbucket: {ex.Message}");
        }
    }

    public async Task<Connection> ConnectAsync(
        Guid userId, string workspace, string? email, string token, string label, CancellationToken ct = default)
    {
        var probe = await ProbeAsync(workspace, email, token, ct);
        if (!probe.Ok) throw new InvalidOperationException(probe.Error ?? "Token validation failed.");

        var ws = NormaliseWorkspace(workspace);

        // As for GitHub and Gitea: write starts OFF, and there is no write tool yet.
        var capabilities = new List<Capability>
        {
            new() { Name = Capabilities.CodeRead,  Granted = true, Enabled = true,  IsSelfImposedOnly = true },
            new() { Name = Capabilities.CodeWrite, Granted = true, Enabled = false, IsSelfImposedOnly = true },
        };

        log.LogInformation("Connecting Bitbucket workspace {Workspace} as {Login} (label {Label}, {Auth}).",
            ws, probe.Login, label, string.IsNullOrWhiteSpace(email) ? "bearer" : "basic");

        // Identity is the account's uuid, which never changes; the nickname is for display.
        var connection = await connections.UpsertAsync(
            userId, ProviderKind.Bitbucket, InstanceFor(ws), probe.Uuid,
            label, AuthKind.PersonalAccessToken, Credential(email, token), capabilities, ct);

        await connections.SetAccountIdentityAsync(connection, probe.Login, isPersonal: false, ct);

        // An API token has an expiry, but Bitbucket does not report it. Null reads as
        // "not known", which is the truth.
        await connections.SetCredentialExpiryAsync(connection, null, ct);

        return connection;
    }

    public async Task<Connection> ReplaceTokenAsync(
        Guid userId, Connection connection, string token, CancellationToken ct = default)
    {
        var old = await connections.GetCredentialAsync(connection, ct);
        var colon = old?.LastIndexOf(':') ?? -1;
        var email = colon > 0 ? old![..colon] : null;

        return await ConnectAsync(userId, WorkspaceOf(connection), email, token, connection.Label, ct);
    }

    public async Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default)
    {
        var credential = await connections.GetCredentialAsync(connection, ct);
        if (credential is null) return false;

        var colon = credential.LastIndexOf(':');
        var probe = colon > 0
            ? await ProbeAsync(WorkspaceOf(connection), credential[..colon], credential[(colon + 1)..], ct)
            : await ProbeAsync(WorkspaceOf(connection), null, credential, ct);

        await connections.SetStateAsync(connection.UserId, connection.Id,
            probe.Ok ? ConnectionState.Active : ConnectionState.Expired, ct);
        return probe.Ok;
    }

    private sealed record UserResponse(
        [property: JsonPropertyName("uuid")] string? Uuid,
        [property: JsonPropertyName("nickname")] string? Nickname,
        [property: JsonPropertyName("display_name")] string? DisplayName);

    private sealed record RepoPage([property: JsonPropertyName("values")] List<System.Text.Json.JsonElement>? Values);
}
