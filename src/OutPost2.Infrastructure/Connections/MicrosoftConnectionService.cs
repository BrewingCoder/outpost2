using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Connections;

public sealed class EntraOptions
{
    public const string SectionName = "OutPost2:Entra";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// <c>/organizations</c>, not <c>/common</c>.
    /// <para>
    /// OutPost2 connects real Entra tenants only. <c>/common</c> also offers consumer
    /// accounts, and because a personal Microsoft account can be registered under a work
    /// email address, the picker shows two entries that read identically — which is how
    /// the first real connection ended up on the wrong mailbox. <c>/organizations</c>
    /// removes them at the identity provider, so the wrong one is never offered.
    /// </para>
    /// <para>
    /// Still multi-tenant: the authority must not be pinned to the home tenant or no
    /// other tenant could ever connect.
    /// </para>
    /// </summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com/organizations";

    /// <summary>
    /// Must match a redirect URI on the app registration. No default on purpose: a default
    /// pointing at someone else's deployment would send this instance's users there to
    /// consent, and it would fail in a way that looks like a tenant problem.
    /// </summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>
    /// Delegated only, and deliberately minimal (D2: email first). Capabilities beyond mail
    /// arrive later through incremental consent, not by over-asking now — an app that
    /// requests calendar access while reading no calendars is how an admin is taught to say no.
    /// </summary>
    public string[] Scopes { get; set; } =
        ["https://graph.microsoft.com/Mail.ReadWrite", "https://graph.microsoft.com/Mail.Send",
         "https://graph.microsoft.com/User.Read"];

    /// <summary>
    /// Asked for at consent time and NOT when refreshing silently. Anything here is a
    /// nice-to-have: the connection works without it, with one feature degraded.
    /// <para>
    /// The distinction is load-bearing. Adding a scope to <see cref="Scopes"/> changes what
    /// every silent refresh asks for, and MSAL answers a request for a scope the cached
    /// token does not carry with "interaction required" — so one new scope for one small
    /// feature instantly breaks reading mail on every connection already in the database,
    /// until each is re-authorised by hand. Measured, not predicted: that is exactly what
    /// happened when MailboxSettings went into the list above.
    /// </para>
    /// <para>
    /// Asking only at consent means a connection made from now on carries the scope, a
    /// connection made before it simply lacks it, and nothing breaks either way. Entra
    /// issues one Graph token carrying everything consented for the resource, so the
    /// narrower silent request still comes back able to use it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// MailboxSettings.ReadWrite is for Outlook's colour categories (OP2-76). Mail.ReadWrite
    /// can put a category ON a message but cannot read or add to the mailbox's palette,
    /// which lives in mailbox settings.
    /// </remarks>
    public string[] OptionalScopes { get; set; } =
        ["https://graph.microsoft.com/MailboxSettings.ReadWrite"];

    /// <summary>What to ask the user to consent to: everything we need plus everything we would like.</summary>
    public string[] ConsentScopes => [.. Scopes, .. OptionalScopes];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public interface IMicrosoftConnectionService : OutPost2.Infrastructure.Mail.IMicrosoftConnectionServiceAccessor
{
    Task<Uri> BuildAuthorizeUrlAsync(PendingConnection pending, CancellationToken ct = default);

    /// <summary>Exchanges the authorization code and persists the connection.</summary>
    Task<Connection> CompleteAsync(PendingConnection pending, string code, CancellationToken ct = default);

    /// <summary>
    /// A Graph access token for this connection, refreshed silently. Returns null when the
    /// connection needs interactive re-authorisation.
    /// </summary>
    Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default);
}

public sealed class MicrosoftConnectionService(
    IOptions<EntraOptions> options,
    IConnectionStore connections,
    ILogger<MicrosoftConnectionService> log) : IMicrosoftConnectionService
{
    private readonly EntraOptions _o = options.Value;

    /// <summary>
    /// Builds a confidential client whose MSAL token cache is backed by this connection's
    /// encrypted blob.
    /// <para>
    /// We deliberately persist MSAL's own serialized cache rather than fishing out the raw
    /// refresh token. MSAL does not expose refresh tokens by design, and letting it own
    /// token lifecycle means refresh, expiry, CAE claims challenges and tenant-specific
    /// quirks keep working without us reimplementing any of it.
    /// </para>
    /// </summary>
    private async Task<(IConfidentialClientApplication App, Func<Task> PersistAsync)>
        BuildAppAsync(Connection? connection, CancellationToken ct)
    {
        var app = ConfidentialClientApplicationBuilder
            .Create(_o.ClientId)
            .WithClientSecret(_o.ClientSecret)
            .WithAuthority(_o.Authority, validateAuthority: false)
            .WithRedirectUri(_o.RedirectUri)
            .Build();

        var existing = connection is null ? null : await connections.GetCredentialAsync(connection, ct);

        // The cache can only be serialized from inside the callback — MSAL hands the
        // serializer to the notification args, not to ITokenCache — so capture the bytes
        // there and write them out afterwards.
        byte[]? latest = null;

        app.UserTokenCache.SetBeforeAccess(args =>
        {
            if (!string.IsNullOrEmpty(existing))
                args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(existing), shouldClearExistingCache: true);
        });

        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged) latest = args.TokenCache.SerializeMsalV3();
        });

        async Task PersistAsync()
        {
            if (latest is null || connection is null) return;
            await connections.SetCredentialAsync(connection, Convert.ToBase64String(latest), ct);
        }

        return (app, PersistAsync);
    }

    public async Task<Uri> BuildAuthorizeUrlAsync(PendingConnection pending, CancellationToken ct = default)
    {
        var (app, _) = await BuildAppAsync(null, ct);

        var extra = new Dictionary<string, string> { ["state"] = pending.State };

        // domain_hint skips home-realm discovery and sends the user straight to the named
        // organisation's identity provider.
        //
        // Without it, a cached live.com (MSA) session for the SAME email address is
        // carried into the tenant-scoped request, and Entra then correctly reports
        // AADSTS50020 - "user from identity provider live.com does not exist in tenant" -
        // which reads like a permissions problem but is really the wrong identity
        // provider. prompt=select_account does not help, because federation engages
        // before any picker is shown.
        if (!string.IsNullOrWhiteSpace(pending.TenantHint) && pending.TenantHint.Contains('.'))
            extra["domain_hint"] = pending.TenantHint.Trim();

        var builder = app.GetAuthorizationRequestUrl(_o.ConsentScopes)
            .WithExtraQueryParameters(extra)
            // WithPrompt, NOT an extra query parameter: MSAL emits `prompt` itself and
            // throws MsalClientException("Duplicate query parameter 'prompt'") if it is
            // also supplied by hand.
            .WithPrompt(Prompt.SelectAccount)
            // Force an account picker, or a second connection silently reuses the
            // browser's Microsoft session and a user adding "home" gets a duplicate of
            // "work" with no indication why.
            .WithPkce(out var codeVerifier);

        // MSAL generated this pair, so ITS verifier is the one Entra will expect back.
        // Discarding it (WithPkce(out _)) sends a challenge we cannot answer, and the
        // code redemption fails after the user has already consented.
        pending.CodeVerifier = codeVerifier;

        // A tenant hint sends the user straight to the right directory; /common otherwise.
        if (!string.IsNullOrWhiteSpace(pending.TenantHint))
            builder = builder.WithTenantId(pending.TenantHint);

        return await builder.ExecuteAsync(ct);
    }

    public async Task<Connection> CompleteAsync(PendingConnection pending, string code, CancellationToken ct = default)
    {
        var (app, _) = await BuildAppAsync(null, ct);

        var result = await app.AcquireTokenByAuthorizationCode(_o.ConsentScopes, code)
            .WithPkceCodeVerifier(pending.CodeVerifier)
            .ExecuteAsync(ct);

        var account = result.Account
            ?? throw new InvalidOperationException("Microsoft returned no account for the authorization code.");

        // Before anything is written. A reconnect reuses the existing label, and labels
        // are uniquely indexed per user, so the wrong account here would not update the
        // connection — it would try to insert a second one under a taken name and die on
        // the constraint, after the user had already consented.
        if (pending.ExpectedHomeAccountId is { } expected
            && !string.Equals(expected, account.HomeAccountId?.Identifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"That signed in as {account.Username}, which is a different account from the one " +
                $"'{pending.Label}' is connected to. Reconnect it and pick the original account, " +
                "or add it as a new connection under its own name.");
        }

        var tenantId = result.TenantId ?? account.HomeAccountId?.TenantId ?? "common";

        // Pull the freshly-populated cache out through a no-op access, since the serializer
        // is only reachable from the callback.
        byte[]? blob = null;
        app.UserTokenCache.SetAfterAccess(args => blob = args.TokenCache.SerializeMsalV3());
        await app.GetAccountsAsync();

        var cacheBlob = Convert.ToBase64String(
            blob ?? throw new InvalidOperationException("MSAL token cache was empty after code redemption."));

        // What Entra actually granted may be narrower than what we asked for, so record the
        // response rather than our request. Claiming a capability we were refused turns a
        // clear consent problem into a confusing runtime failure later.
        var granted = (result.Scopes ?? []).Select(s => s.Split('/').Last()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var capabilities = new List<Capability>
        {
            new() { Name = Capabilities.MailRead,
                    Granted = granted.Contains("Mail.ReadWrite") || granted.Contains("Mail.Read"),
                    Enabled = true },
            // ReadWrite only. Mail.Read alone can list and open a message but cannot move,
            // flag or delete one, and claiming otherwise turns a consent decision into a
            // 403 the user meets halfway through tidying their inbox.
            new() { Name = Capabilities.MailWrite,
                    Granted = granted.Contains("Mail.ReadWrite"),
                    Enabled = true },
            new() { Name = Capabilities.MailSend,
                    Granted = granted.Contains("Mail.Send"),
                    Enabled = true },
        };

        // Unreachable via /organizations, but a silently-wrong mailbox is bad enough to
        // be worth a second check that does not depend on the IdP behaving.
        if (string.Equals(tenantId, Connection.ConsumerTenantId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{account.Username} is a personal Microsoft account. OutPost2 connects " +
                "organisational tenants only — pick the work or school account with the same address.");

        log.LogInformation(
            "Completed Microsoft connection for {Username} in tenant {Tenant}; granted {Scopes}.",
            account.Username, tenantId, string.Join(" ", granted));

        var created = await connections.UpsertAsync(
            pending.UserId, ProviderKind.Microsoft, tenantId,
            account.HomeAccountId?.Identifier, pending.Label,
            AuthKind.OAuth2, cacheBlob, capabilities, ct);

        // Record WHO this is, not just which tenant. A personal Microsoft account can
        // carry a work email address, so the tenant is the only thing that distinguishes
        // them — and a raw GUID tells a human nothing.
        await connections.SetAccountIdentityAsync(created,
            account.Username,
            isPersonal: string.Equals(tenantId, Connection.ConsumerTenantId, StringComparison.OrdinalIgnoreCase),
            ct);

        return created;
    }

    public async Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default)
    {
        var (app, persist) = await BuildAppAsync(connection, ct);

        var account = (await app.GetAccountsAsync()).FirstOrDefault(
            a => a.HomeAccountId?.Identifier == connection.HomeAccountId)
            ?? (await app.GetAccountsAsync()).FirstOrDefault();

        if (account is null)
        {
            log.LogWarning("No cached account for connection {Label}; interactive re-auth required.", connection.Label);
            return null;
        }

        // Ask for the optional scopes too, and settle for the required ones if that is
        // refused. A refresh token is bound to the client and the resource, not to the
        // scope set it was first issued with, so a connection can scope UP to anything the
        // tenant has consented to since — no browser, no re-authorising, no lost label.
        //
        // Measured on three real connections: both work mailboxes picked up
        // MailboxSettings on the next refresh after the tenant granted it, while a mailbox
        // in a tenant that had not consented answered "interaction required". Asking for
        // everything unconditionally would have broken that third one for ALL mail; never
        // asking would have left the other two needing a human for no reason.
        if (_o.OptionalScopes.Length > 0 && WideIsWorthTrying(connection.Id))
        {
            try
            {
                var wide = await app.AcquireTokenSilent(_o.ConsentScopes, account).ExecuteAsync(ct);
                await persist();
                await connections.TouchAsync(connection.Id, ct);
                return wide.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Expected wherever the optional scopes are not consented. Remembered, so
                // every mail read does not pay for the same refusal — and remembered with
                // an expiry, so consent granted later is picked up on its own.
                _wideRefusedUntil[connection.Id] = DateTimeOffset.UtcNow.Add(RetryWideAfter);
                log.LogInformation(
                    "Connection {Label} has no consent for the optional scopes; using the required set.",
                    connection.Label);
            }
        }

        try
        {
            var result = await app.AcquireTokenSilent(_o.Scopes, account).ExecuteAsync(ct);

            // MSAL may have rotated the refresh token during this call, so the cache has to
            // be written back. Skipping this quietly re-authorises the user in 90 days.
            await persist();
            await connections.TouchAsync(connection.Id, ct);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            // The refresh token is gone, revoked, or Conditional Access wants a fresh
            // interaction. Not an error to retry — it needs a browser, which means it
            // cannot be resolved inside a voice turn.
            log.LogInformation("Connection {Label} needs interactive re-auth: {Reason}.", connection.Label, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// How long to stop asking for the optional scopes after a refusal. Long enough that a
    /// busy mailbox is not paying for a round trip it will not get, short enough that an
    /// admin granting consent over lunch takes effect without a restart.
    /// </summary>
    private static readonly TimeSpan RetryWideAfter = TimeSpan.FromMinutes(30);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset>
        _wideRefusedUntil = new();

    private static bool WideIsWorthTrying(Guid connectionId) =>
        !_wideRefusedUntil.TryGetValue(connectionId, out var until) || DateTimeOffset.UtcNow >= until;
}
