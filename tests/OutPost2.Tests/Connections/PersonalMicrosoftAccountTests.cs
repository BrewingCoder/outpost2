using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Personal Microsoft accounts — outlook.com, hotmail.com, live.com (OP2-81).
/// <para>
/// The property most worth pinning is the one that is easiest to lose: work sign-ins stay
/// on <c>/organizations</c>. Widening them to <c>/common</c> would "support" personal
/// accounts in one line and bring back the picker in which a work account and a personal
/// account with the same address read identically.
/// </para>
/// </summary>
public class PersonalMicrosoftAccountTests
{
    private static MicrosoftConnectionService Service(bool personalAccounts = true) =>
        new(Options.Create(new EntraOptions
            {
                ClientId = "11111111-2222-3333-4444-555555555555",
                ClientSecret = "secret",
                RedirectUri = "https://outpost.example/signin-microsoft",
                PersonalAccounts = personalAccounts,
            }),

            // Never touched: building an authorize URL reads configuration only.
            connections: null!,
            NullLogger<MicrosoftConnectionService>.Instance);

    private static PendingConnection Pending(string? hint, string? expectedHomeAccountId = null) => new()
    {
        State = "state-1",
        UserId = Guid.NewGuid(),
        Provider = ProviderKind.Microsoft,
        Label = "home",
        CodeVerifier = "unused",
        TenantHint = hint,
        ExpectedHomeAccountId = expectedHomeAccountId,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
    };

    private static Dictionary<string, string> Query(Uri u) =>
        u.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    // ── which sign-in each kind goes to ──────────────────────────────────────

    [Fact]
    public async Task A_personal_sign_in_goes_to_consumers()
    {
        var url = await Service().BuildAuthorizeUrlAsync(Pending(PersonalMicrosoftAccount.TenantHint));

        Assert.StartsWith("/consumers/", url.AbsolutePath);
    }

    [Fact]
    public async Task A_work_sign_in_still_goes_to_organizations_with_personal_accounts_on()
    {
        var url = await Service(personalAccounts: true).BuildAuthorizeUrlAsync(Pending(null));

        Assert.StartsWith("/organizations/", url.AbsolutePath);
        Assert.DoesNotContain("/common/", url.AbsolutePath);
    }

    [Fact]
    public async Task A_personal_reconnect_carrying_the_consumer_tenant_id_also_goes_to_consumers()
    {
        // A reconnect hints the connection's own tenant, which for a personal account is
        // Microsoft's consumer tenant id rather than the word "consumers".
        var url = await Service().BuildAuthorizeUrlAsync(Pending(Connection.ConsumerTenantId, "home-account"));

        Assert.StartsWith("/consumers/", url.AbsolutePath);
    }

    // ── what a personal account is asked for ─────────────────────────────────

    [Fact]
    public async Task A_personal_sign_in_does_not_ask_for_work_only_scopes()
    {
        var scope = Query(await Service().BuildAuthorizeUrlAsync(Pending(PersonalMicrosoftAccount.TenantHint)))["scope"];

        Assert.Contains("Mail.ReadWrite", scope);
        Assert.Contains("Calendars.ReadWrite", scope);
        Assert.Contains("Contacts.ReadWrite", scope);

        Assert.DoesNotContain("Chat.Read", scope);
        Assert.DoesNotContain("ChatMessage.Send", scope);
        Assert.DoesNotContain("User.ReadBasic.All", scope);
        Assert.DoesNotContain("Calendars.Read.Shared", scope);
    }

    [Fact]
    public async Task A_work_sign_in_still_asks_for_everything()
    {
        var scope = Query(await Service().BuildAuthorizeUrlAsync(Pending(null)))["scope"];

        Assert.Contains("Chat.Read", scope);
        Assert.Contains("User.ReadBasic.All", scope);
    }

    [Fact]
    public async Task A_personal_sign_in_sends_no_domain_hint()
    {
        var q = Query(await Service().BuildAuthorizeUrlAsync(Pending(PersonalMicrosoftAccount.TenantHint)));

        Assert.False(q.ContainsKey("domain_hint"));
    }

    // ── the switch ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_personal_connection_is_refused_when_the_server_does_not_offer_them()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(personalAccounts: false).BuildAuthorizeUrlAsync(Pending(PersonalMicrosoftAccount.TenantHint)));
    }

    [Fact]
    public async Task An_existing_personal_connection_can_still_be_reconnected_after_the_switch_goes_off()
    {
        var url = await Service(personalAccounts: false)
            .BuildAuthorizeUrlAsync(Pending(Connection.ConsumerTenantId, "home-account"));

        Assert.StartsWith("/consumers/", url.AbsolutePath);
    }

    // ── detection ────────────────────────────────────────────────────────────

    private static Connection Conn(
        string instance, bool isPersonal = false, ProviderKind provider = ProviderKind.Microsoft,
        params (string Name, bool Granted)[] caps) => new()
    {
        UserId = Guid.Empty,
        Label = "home",
        Provider = provider,
        Instance = instance,
        AuthKind = AuthKind.OAuth2,
        SealedCredential = "ref/home",
        State = ConnectionState.Active,
        IsPersonalAccount = isPersonal,
        Capabilities = caps.Select(c => new Capability { Name = c.Name, Granted = c.Granted, Enabled = true }).ToList(),
    };

    [Fact]
    public void The_consumer_tenant_marks_a_connection_personal_even_without_the_flag()
    {
        Assert.True(PersonalMicrosoftAccount.Is(Conn(Connection.ConsumerTenantId)));
        Assert.False(PersonalMicrosoftAccount.Is(Conn("72f988bf-86f1-41af-91ab-2d7cd011db47")));
    }

    [Fact]
    public void A_personal_Gmail_account_is_not_a_personal_Microsoft_account()
    {
        // Google sets IsPersonalAccount too, for a consumer Gmail address.
        Assert.False(PersonalMicrosoftAccount.Is(Conn("gmail.com", isPersonal: true, ProviderKind.Google)));
    }

    // ── what the user is told ────────────────────────────────────────────────

    [Fact]
    public void Teams_on_a_personal_account_explains_why_rather_than_asking_for_a_reconnect()
    {
        var c = Conn(Connection.ConsumerTenantId, isPersonal: true,
            caps: [(Capabilities.MailRead, true), (Capabilities.ChatRead, false)]);

        var r = ConnectionResolver.Resolve([c], Capabilities.ChatRead, "home", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
        Assert.Contains("personal Microsoft account", r.SpokenSummary);
        Assert.DoesNotContain("Reconnect", r.SpokenSummary);
    }

    [Fact]
    public void A_work_account_missing_Teams_consent_is_still_told_to_reconnect()
    {
        var c = Conn("72f988bf-86f1-41af-91ab-2d7cd011db47",
            caps: [(Capabilities.MailRead, true), (Capabilities.ChatRead, false)]);

        var r = ConnectionResolver.Resolve([c], Capabilities.ChatRead, "home", isWrite: false);

        Assert.Contains("Reconnect", r.SpokenSummary);
    }
}
