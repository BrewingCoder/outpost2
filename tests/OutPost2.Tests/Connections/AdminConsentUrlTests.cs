using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Tests.Connections;

/// <summary>
/// The link a blocked user forwards to their administrator (OP2-100).
/// <para>
/// Worth its own suite because nothing downstream can catch a mistake in it. A wrong
/// admin-consent URL fails in a stranger's browser, in another organisation, with no
/// telemetry reaching us — the user just never hears back.
/// </para>
/// </summary>
public class AdminConsentUrlTests
{
    private static MicrosoftConnectionService Service(EntraOptions? o = null) =>
        new(Options.Create(o ?? new EntraOptions
            {
                ClientId = "11111111-2222-3333-4444-555555555555",
                ClientSecret = "secret",
                RedirectUri = "https://outpost.example/signin-microsoft",
            }),

            // Never touched: building the URL reads configuration and nothing else, which
            // is the property that makes it safe to call before the connection exists.
            connections: null!,
            NullLogger<MicrosoftConnectionService>.Instance);

    private static Dictionary<string, string> Query(Uri u) =>
        u.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public void It_is_the_v2_endpoint_and_names_the_scopes()
    {
        var url = Service().BuildAdminConsentUrl("contoso.com", "state-1");

        // v2.0, not the bare /adminconsent the ticket asked for. The v1-style endpoint
        // consents to the app registration's statically listed permissions, and this app
        // asks for everything dynamically — so that list is not where the truth lives and
        // an admin approving it would approve a set unrelated to what the user was refused.
        Assert.Equal("/contoso.com/v2.0/adminconsent", url.AbsolutePath);
        Assert.Equal("login.microsoftonline.com", url.Host);

        var q = Query(url);
        Assert.Contains("https://graph.microsoft.com/Mail.ReadWrite", q["scope"]);

        // The optional scopes too. A second trip to the administrator because the
        // calendar was left off the first request is the worst outcome available here.
        Assert.Contains("https://graph.microsoft.com/Calendars.ReadWrite", q["scope"]);
    }

    [Fact]
    public void It_carries_the_state_that_finds_the_connection_again()
    {
        var url = Service().BuildAdminConsentUrl("contoso.com", "the-connection-id");

        Assert.Equal("the-connection-id", Query(url)["state"]);
    }

    [Fact]
    public void It_never_asks_common()
    {
        // Microsoft documents this one explicitly: a personal account cannot give admin
        // consent outside a tenant context. `organizations` is the placeholder to use
        // when the user never told us which directory they are in.
        var url = Service().BuildAdminConsentUrl(Connection.UnknownTenant, "s");

        Assert.Equal("/organizations/v2.0/adminconsent", url.AbsolutePath);
        Assert.DoesNotContain("/common/", url.AbsolutePath);
    }

    [Fact]
    public void The_redirect_falls_back_to_the_sign_in_callback()
    {
        // Blank by default on purpose: Entra string-matches redirect URIs, so defaulting
        // to a separate path would mean admin consent failed with AADSTS50011 on every
        // deployment whose app registration had not been edited by hand first.
        var url = Service().BuildAdminConsentUrl("contoso.com", "s");

        Assert.Equal("https://outpost.example/signin-microsoft", Query(url)["redirect_uri"]);
    }

    [Fact]
    public void A_configured_redirect_is_used_instead()
    {
        var url = Service(new EntraOptions
        {
            ClientId = "c",
            RedirectUri = "https://outpost.example/signin-microsoft",
            AdminConsentRedirectUri = "https://outpost.example/admin-approved",
        }).BuildAdminConsentUrl("contoso.com", "s");

        Assert.Equal("https://outpost.example/admin-approved", Query(url)["redirect_uri"]);
    }

    [Fact]
    public void A_tenant_cannot_smuggle_extra_query_parameters_into_the_path()
    {
        // The tenant reaches here from a user-typed hint. Unescaped it would let that
        // hint append its own parameters to a URL the user then forwards to an
        // administrator as if it came from us.
        var url = Service().BuildAdminConsentUrl("contoso.com/x?client_id=evil", "s");

        // One client_id, and it is ours.
        Assert.Equal("11111111-2222-3333-4444-555555555555", Query(url)["client_id"]);
        Assert.Equal("login.microsoftonline.com", url.Host);
        Assert.EndsWith("/v2.0/adminconsent", url.AbsolutePath);
    }

    [Fact]
    public void Declared_capabilities_are_the_full_set_with_nothing_granted()
    {
        // What a parked connection claims. All declared so the resolver can explain
        // itself; none granted, because nothing was.
        var caps = Service().DeclaredCapabilities;

        Assert.Contains(caps, c => c.Name == Capabilities.MailRead);
        Assert.Contains(caps, c => c.Name == Capabilities.CalendarRead);
        Assert.Contains(caps, c => c.Name == Capabilities.ChatSend);
        Assert.All(caps, c => Assert.False(c.Granted));
    }
}
