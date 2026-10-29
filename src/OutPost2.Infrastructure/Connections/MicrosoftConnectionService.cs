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
    /// Where Entra sends an administrator after they approve (or refuse) tenant-wide
    /// consent. Blank means "the same place as the sign-in callback", which is the
    /// default on purpose: Entra string-matches redirect URIs, so a separate path would
    /// have to be added to the app registration by hand before admin consent worked at
    /// all — a silent AADSTS50011 for anybody who deployed without reading the notes.
    /// <para>
    /// The two responses are told apart by Entra's own <c>admin_consent=True</c>
    /// parameter, which it sets on BOTH the success and the error redirect, and which
    /// never appears on a sign-in callback.
    /// </para>
    /// </summary>
    public string AdminConsentRedirectUri { get; set; } = "";

    /// <summary>The admin-consent redirect, falling back to the sign-in one.</summary>
    public string EffectiveAdminConsentRedirectUri =>
        string.IsNullOrWhiteSpace(AdminConsentRedirectUri) ? RedirectUri : AdminConsentRedirectUri;

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
    /// <para>
    /// MailboxSettings.ReadWrite is for Outlook's colour categories (OP2-76). Mail.ReadWrite
    /// can put a category ON a message but cannot read or add to the mailbox's palette,
    /// which lives in mailbox settings. The calendar leans on it too, for the mailbox's
    /// timezone — without it every time has to be read out in UTC.
    /// </para>
    /// <para>
    /// Calendars.ReadWrite is the calendar (OP2-75). Optional rather than required for the
    /// reason above: a connection made before it existed keeps working and simply has no
    /// calendar, instead of every mailbox needing a browser the moment this shipped.
    /// </para>
    /// <para>
    /// Calendars.Read.Shared is for <c>findMeetingTimes</c> ALONE (OP2-78). Checked
    /// against the v1.0 permissions table rather than assumed: the neighbouring
    /// <c>getSchedule</c> needs only Calendars.ReadBasic, which Calendars.ReadWrite
    /// already covers — so plain free/busy works without this and only the ranked
    /// suggestions need it.
    /// </para>
    /// <para>
    /// People.Read, User.ReadBasic.All and Contacts.ReadWrite are people lookup and the
    /// address book (OP2-77). User.ReadBasic.All, not User.Read.All: the basic one needs
    /// no admin consent and still carries name, address, job title and department, which
    /// is everything "email Justin" has to resolve. Reading a person's MANAGER needs
    /// User.Read.All, which does require admin consent, so it is deliberately not asked
    /// for and the feature is not offered.
    /// </para>
    /// <para>
    /// Chat.Read and ChatMessage.Send are Teams chats (OP2-79). Both verified against the
    /// v1.0 permissions tables: Chat.Read is least-privileged for BOTH listing chats and
    /// reading their messages, and ChatMessage.Send is least-privileged for posting — the
    /// combined channel-or-chat reference page lists ChannelMessage.Send against the chat
    /// endpoint, which is a documentation artefact of the two APIs sharing a page.
    /// </para>
    /// <para>
    /// Conspicuously absent: ChannelMessage.Read.All. It is the only Teams permission
    /// requiring ADMIN consent, and consent is all-or-nothing — one admin-consent scope
    /// here would mean an administrator had to approve mail and calendar too. Teams
    /// channels are a deliberate omission with a price attached, not an oversight.
    /// </para>
    /// </remarks>
    public string[] OptionalScopes { get; set; } =
    [
        "https://graph.microsoft.com/MailboxSettings.ReadWrite",
        "https://graph.microsoft.com/Calendars.ReadWrite",
        "https://graph.microsoft.com/Calendars.Read.Shared",
        "https://graph.microsoft.com/People.Read",
        "https://graph.microsoft.com/User.ReadBasic.All",
        "https://graph.microsoft.com/Contacts.ReadWrite",
        "https://graph.microsoft.com/Chat.Read",
        "https://graph.microsoft.com/ChatMessage.Send",
    ];

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
    /// The link a blocked user forwards to their tenant administrator (OP2-100).
    /// </summary>
    /// <param name="tenant">
    /// Tenant id, verified domain, or <see cref="Connection.UnknownTenant"/>. Never
    /// <c>common</c>: Microsoft documents that a personal account cannot give admin
    /// consent outside a tenant context, so <c>common</c> is the one value that is
    /// explicitly wrong here even though it works everywhere else.
    /// </param>
    /// <param name="state">
    /// Echoed back on the callback. This is how the approval finds its way to the right
    /// connection, since the administrator arrives in their own browser, possibly days
    /// later, and is not signed in to OutPost2 at all.
    /// </param>
    Uri BuildAdminConsentUrl(string tenant, string state);

    /// <summary>
    /// Every capability a Microsoft connection can have, with none of them granted — what
    /// a connection parked for admin approval declares (OP2-100).
    /// <para>
    /// From the same list that a real sign-in populates, so the two cannot drift. A
    /// parked connection that forgot to declare, say, the calendar would answer
    /// "what's on my diary" with "no connection called work" rather than "waiting on
    /// your admin", which is the one answer that is actively misleading.
    /// </para>
    /// </summary>
    IReadOnlyList<Capability> DeclaredCapabilities { get; }

    // GetAccessTokenAsync is NOT redeclared here. It used to be, which hid the base
    // interface's identical member and produced CS0108 (OP2-147) — and on the token path
    // a hidden member is worse than untidy, because which implementation runs would then
    // depend on which of the two interfaces the caller happened to hold. It is inherited
    // from IMicrosoftConnectionServiceAccessor, where it is documented.
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

        // The tuple-valued overload, because the Dictionary<string,string> one is obsolete
        // (OP2-147). includeInCacheKey is false for both of these and that is the whole
        // point of the new signature: `state` is a one-shot CSRF value and `domain_hint`
        // steers the browser, so folding either into the token cache key would partition
        // the cache by values that have nothing to do with what the token is for.
        var extra = new Dictionary<string, (string value, bool includeInCacheKey)>
        {
            ["state"] = (pending.State, false),
        };

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
            extra["domain_hint"] = (pending.TenantHint.Trim(), false);

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

    /// <summary>
    /// What a Microsoft connection can do, given the scopes Entra actually granted.
    /// <para>
    /// Extracted so a connection parked for admin approval can declare the same
    /// capabilities with NOTHING granted (OP2-100). That is not bookkeeping: the resolver
    /// only offers an explanation for a connection that DECLARES the capability, so a
    /// parked mailbox with an empty list would answer "check work email" with "no
    /// connection called work" — a lie that sends the user hunting for something they can
    /// see on the page in front of them.
    /// </para>
    /// </summary>
    private static List<Capability> CapabilitiesFor(HashSet<string> granted)
    {
        return new List<Capability>
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

            new() { Name = Capabilities.CalendarRead,
                    Granted = granted.Contains("Calendars.ReadWrite") || granted.Contains("Calendars.Read"),
                    Enabled = true },

            // ReadWrite only, for the same reason as mail: Calendars.Read can show the
            // diary but cannot move anything in it, and claiming otherwise turns a
            // consent decision into a 403 met halfway through rescheduling.
            new() { Name = Capabilities.CalendarWrite,
                    Granted = granted.Contains("Calendars.ReadWrite"),
                    Enabled = true },

            // Any one of the three is enough to answer "who is Justin" from SOME source,
            // and each degrades on its own: no People.Read loses the relevance ranking,
            // no User.ReadBasic.All loses the directory, no Contacts.Read loses the
            // address book. Requiring all three would switch the whole feature off over
            // one scope a tenant happened to withhold.
            new() { Name = Capabilities.PeopleRead,
                    Granted = granted.Contains("People.Read")
                              || granted.Contains("User.ReadBasic.All")
                              || granted.Contains("Contacts.ReadWrite")
                              || granted.Contains("Contacts.Read"),
                    Enabled = true },

            new() { Name = Capabilities.ContactsWrite,
                    Granted = granted.Contains("Contacts.ReadWrite"),
                    Enabled = true },

            // Chat.ReadWrite is a superset and some tenants grant it in place of the
            // narrower scope, so both count. Chat.ReadBasic deliberately does NOT: it can
            // list chats but cannot read a single message, and claiming chat.read on it
            // turns a consent decision into a 403 met halfway through a conversation.
            new() { Name = Capabilities.ChatRead,
                    Granted = granted.Contains("Chat.Read") || granted.Contains("Chat.ReadWrite"),
                    Enabled = true },

            new() { Name = Capabilities.ChatSend,
                    Granted = granted.Contains("ChatMessage.Send") || granted.Contains("Chat.ReadWrite"),
                    Enabled = true },
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<Capability> DeclaredCapabilities =>
        CapabilitiesFor(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <inheritdoc />
    public Uri BuildAdminConsentUrl(string tenant, string state)
    {
        // The v2.0 endpoint with an explicit `scope`, NOT the bare /adminconsent that the
        // ticket asked for. The difference matters for this app specifically: the v1-style
        // endpoint consents to whatever is listed under the app registration's required
        // permissions, and OutPost2 asks for everything dynamically at authorize time, so
        // that list is not where the truth lives. An admin who approved it would approve
        // a set that has nothing to do with what the user was refused.
        //
        // Asking for ConsentScopes — required plus optional — rather than just the
        // required set, because the whole point of a trip to the administrator is to come
        // back with as much as the tenant will give. Being sent back for a second approval
        // because the calendar was left off the first one is the worst outcome here.
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _o.ClientId,
            ["scope"] = string.Join(' ', _o.ConsentScopes),
            ["redirect_uri"] = _o.EffectiveAdminConsentRedirectUri,
            ["state"] = state,
        };

        var builder = new UriBuilder($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/v2.0/adminconsent")
        {
            Query = string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")),
        };

        return builder.Uri;
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
        // is only reachable from the callback. GetAccountAsync by identifier rather than
        // the obsolete GetAccountsAsync() (OP2-147): it fires the same cache callbacks,
        // and we already know exactly which account we just redeemed for.
        byte[]? blob = null;
        app.UserTokenCache.SetAfterAccess(args => blob = args.TokenCache.SerializeMsalV3());
        await app.GetAccountAsync(account.HomeAccountId?.Identifier);

        var cacheBlob = Convert.ToBase64String(
            blob ?? throw new InvalidOperationException("MSAL token cache was empty after code redemption."));

        // What Entra actually granted may be narrower than what we asked for, so record the
        // response rather than our request. Claiming a capability we were refused turns a
        // clear consent problem into a confusing runtime failure later.
        var granted = (result.Scopes ?? []).Select(s => s.Split('/').Last()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var capabilities = CapabilitiesFor(granted);

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

        // By identifier, which is both the supported call and the cheap one (OP2-147):
        // GetAccountsAsync() deserialises and walks the whole cache, and this server has
        // one cache per connection and many connections.
        var account = string.IsNullOrWhiteSpace(connection.HomeAccountId)
            ? null
            : await app.GetAccountAsync(connection.HomeAccountId);

        if (account is null)
        {
            // Fallback for a connection stored before HomeAccountId was recorded, and for
            // one whose stored id no longer matches what Entra issues. Each connection has
            // its own cache blob holding exactly one account, so "the only one in here" is
            // well defined — and there is no supported call that asks for it, which is why
            // the obsolete one is suppressed rather than replaced.
#pragma warning disable CS0618
            account = (await app.GetAccountsAsync()).FirstOrDefault();
#pragma warning restore CS0618
        }

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
            catch (MsalUiRequiredException ex)
            {
                // Expected wherever the optional scopes are not consented. Remembered, so
                // every mail read does not pay for the same refusal — and remembered with
                // an expiry, so consent granted later is picked up on its own.
                _wideRefusedUntil[connection.Id] = DateTimeOffset.UtcNow.Add(RetryWideAfter);

                // S6667 wants the exception object itself. Deliberately not, here and at
                // the catch below (OP2-145): this is expected control flow on every token
                // refresh for an unconsented tenant, and attaching a stack trace that is
                // always the same frames inside MSAL would bury the line it is trying to
                // make readable. What actually distinguishes one refusal from another is
                // MSAL's own classification, so that is logged instead — and logged, which
                // the previous version of this clause did not do at all.
#pragma warning disable S6667
                log.LogInformation(
                    "Connection {Label} has no consent for the optional scopes ({Code}/{Classification}); using the required set.",
                    connection.Label, ex.ErrorCode, ex.Classification);
#pragma warning restore S6667
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
            //
            // See the clause above for why the exception object is not attached. The code
            // and classification are what separate "revoked" from "Conditional Access" from
            // "consent withdrawn", and they are the only part of it worth reading.
#pragma warning disable S6667
            log.LogInformation(
                "Connection {Label} needs interactive re-auth ({Code}/{Classification}): {Reason}.",
                connection.Label, ex.ErrorCode, ex.Classification, ex.Message);
#pragma warning restore S6667
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
