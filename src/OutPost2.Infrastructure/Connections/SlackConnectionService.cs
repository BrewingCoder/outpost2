using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Teams;

namespace OutPost2.Infrastructure.Connections;

public interface ISlackConnectionService
{
    Task<SlackProbe> ProbeAsync(string token, CancellationToken ct = default);

    Task<Connection> ConnectAsync(Guid userId, string token, string label, CancellationToken ct = default);

    Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default);
}

/// <param name="UserId">The user's Slack id (U…), which is how "me" is recognised in a conversation.</param>
/// <param name="Url">The workspace, e.g. https://acme.slack.com — what tells two of them apart.</param>
/// <param name="CanSend">Whether the token carries <c>chat:write</c>.</param>
public sealed record SlackProbe(
    bool Ok, string? UserId, string? User, string? Team, string? Url, string? Error, bool CanSend = false);

/// <summary>
/// Connecting a Slack workspace with a pasted user token (OP2-86).
/// <para>
/// <b>A user token, not a bot token.</b> A user token (<c>xoxp-</c>) acts as the person:
/// it reads their DMs and group DMs and every channel they are in, and what it posts
/// appears under their name — which is what this product is for. A bot token
/// (<c>xoxb-</c>) acts as the app, cannot read a DM it is not part of, and can only see a
/// channel it has been invited into, so "has anyone messaged me" would always answer from
/// the bot's inbox rather than the user's. Both are refused here before Slack is asked.
/// </para>
/// <para>
/// <b>Pasted, not signed in.</b> A Slack OAuth flow needs a Slack app registered for the
/// DEPLOYMENT, with its client secret in configuration and its redirect URL pointed at
/// this server — the way Google is optional behind <c>GoogleOptions.IsConfigured</c>. A
/// pasted token needs none of that: the user creates an app in their own workspace (or
/// uses one their admin made), installs it, and copies the User OAuth Token. It is the
/// same honour-system shape as YouTrack, GitHub and Gitea, with one difference worth
/// having: Slack's scopes are real limits, so a scope the token lacks is reported as not
/// granted rather than as a switch.
/// </para>
/// <para>
/// <b>Scopes are per method and per conversation type</b>, so the probe reads the
/// <c>x-oauth-scopes</c> header from <c>auth.test</c> and names every reading scope that
/// is missing. <c>chat:write</c> is optional: without it the connection reads and its
/// send capability is recorded as not granted.
/// </para>
/// </summary>
public sealed class SlackConnectionService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<SlackConnectionService> log) : ISlackConnectionService
{
    public async Task<SlackProbe> ProbeAsync(string token, CancellationToken ct = default)
    {
        token = (token ?? "").Trim();
        if (token.Length == 0) return Fail("Paste a Slack user token.");

        if (token.StartsWith("xoxb-", StringComparison.Ordinal))
            return Fail("That's a bot token (xoxb-). Outpost needs the User OAuth Token (xoxp-): a bot can't "
                        + "read your DMs, only conversations it has been invited into.");

        if (token.StartsWith("xapp-", StringComparison.Ordinal))
            return Fail("That's an app-level token (xapp-), which can't read or send messages. "
                        + "Copy the User OAuth Token (xoxp-) from OAuth & Permissions instead.");

        // Token rotation: a 12-hour access token that needs a refresh token and the app's
        // client secret to renew. Outpost holds neither, so it would stop working by tonight.
        if (token.StartsWith("xoxe", StringComparison.Ordinal))
            return Fail("That's a rotating Slack token, which expires after 12 hours and can't be renewed from here. "
                        + "Turn off token rotation for the app and copy its User OAuth Token (xoxp-).");

        try
        {
            using var http = SlackApi.Configure(httpFactory.CreateClient("slack"), token);
            using var resp = await http.PostAsync("auth.test", null, ct);

            AuthTest me;
            try { me = await SlackApi.ReadAsync<AuthTest>(resp, "this token", "sign in", ct); }
            catch (SlackApiException ex)
            {
                return Fail(ex.Error is "invalid_auth" or "not_authed" or "token_revoked" or "token_expired" or "account_inactive"
                    ? "Slack rejected that token."
                    : ex.Message);
            }

            if (me.BotId is not null)
                return Fail("That token belongs to a bot. Outpost needs the User OAuth Token (xoxp-), which acts as you.");

            if (string.IsNullOrWhiteSpace(me.UserId))
                return Fail("Slack accepted the token but didn't say whose it is.");

            var scopes = SlackApi.Scopes(resp);
            var missing = scopes is null ? [] : SlackApi.ReadScopes.Where(s => !scopes.Contains(s)).ToList();

            if (missing.Count > 0)
                return Fail($"That token is missing the {Join(missing)} {(missing.Count == 1 ? "scope" : "scopes")}. "
                            + "Add them as User Token Scopes under OAuth & Permissions, reinstall the app, and paste the new token.");

            // No header means Slack did not say, not that nothing was granted. Calls name a
            // missing scope themselves, so this is allowed through rather than refused.
            if (scopes is null)
                log.LogInformation("Slack's auth.test carried no x-oauth-scopes header; scopes unchecked.");

            return new SlackProbe(true, me.UserId, me.User, me.Team, me.Url?.TrimEnd('/'), null,
                CanSend: scopes is null || scopes.Contains(SlackApi.SendScope));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Fail($"Could not reach Slack: {ex.Message}");
        }
    }

    private static SlackProbe Fail(string error) => new(false, null, null, null, null, error);

    private static string Join(IReadOnlyList<string> xs) =>
        xs.Count == 1 ? xs[0] : string.Join(", ", xs.Take(xs.Count - 1)) + " and " + xs[^1];

    public async Task<Connection> ConnectAsync(Guid userId, string token, string label, CancellationToken ct = default)
    {
        token = (token ?? "").Trim();
        var probe = await ProbeAsync(token, ct);
        if (!probe.Ok) throw new InvalidOperationException(probe.Error ?? "Token validation failed.");

        // Read on. Send is granted only when the token really carries chat:write, and
        // starts OFF either way, as code write does for GitHub and Gitea: a Slack message
        // lands under the user's name on somebody's phone and cannot be recalled.
        var capabilities = new List<Capability>
        {
            new() { Name = Capabilities.ChatRead, Granted = true, Enabled = true, IsSelfImposedOnly = true },
            new() { Name = Capabilities.ChatSend, Granted = probe.CanSend, Enabled = false, IsSelfImposedOnly = true },
        };

        var instance = probe.Url ?? "https://slack.com";

        log.LogInformation("Connecting Slack workspace {Team} ({Instance}) as {User} (label {Label}, can send: {CanSend}).",
            probe.Team, instance, probe.User, label, probe.CanSend);

        var connection = await connections.UpsertAsync(
            userId, ProviderKind.Slack, instance, probe.UserId,
            label, AuthKind.PersonalAccessToken, token, capabilities, ct);

        await connections.SetAccountIdentityAsync(connection, probe.User, isPersonal: false, ct);

        // A non-rotating user token has no expiry, and Slack reports none.
        await connections.SetCredentialExpiryAsync(connection, null, ct);

        return connection;
    }

    public async Task<bool> CheckHealthAsync(Connection connection, CancellationToken ct = default)
    {
        var token = await connections.GetCredentialAsync(connection, ct);
        if (token is null) return false;

        var probe = await ProbeAsync(token, ct);
        await connections.SetStateAsync(connection.UserId, connection.Id,
            probe.Ok ? ConnectionState.Active : ConnectionState.Expired, ct);
        return probe.Ok;
    }

    private sealed record AuthTest(string? Url, string? Team, string? User, string? TeamId, string? UserId, string? BotId);
}
