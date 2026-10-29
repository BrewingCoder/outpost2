using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Tests.Connections;

/// <summary>
/// What a connection says when it is missing a CONSENT rather than a credential
/// (OP2-166, OP2-167).
/// <para>
/// This suite exists because of a live failure that looked like something else entirely.
/// Two Microsoft connections made before the calendar and Teams shipped carried only the
/// three mail capabilities — not ungranted calendar entries, none at all — and the
/// resolver builds its "exists but cannot serve this" list from connections that DECLARE
/// the capability. So both fell out of every list and the answer to "what's on my
/// calendar for work" was <i>"No connection called work. You have work, home."</i> while
/// listing the very name it claimed not to know.
/// </para>
/// <para>
/// An agent read that, believed it, and filed two bug reports saying the connection
/// lookup was broken. So the test for the fix is not only that the capability is declared
/// — it is that the refusal states plainly enough to be acted on by a model that the
/// thing missing is consent.
/// </para>
/// </summary>
public class ConsentRefusalTests
{
    /// <summary>
    /// A Microsoft connection exactly as the live database held one: mail only, because
    /// that was everything OutPost2 could do on the day it was connected.
    /// </summary>
    private static Connection MailOnly(string label = "work") => new()
    {
        UserId = Guid.Empty,
        Label = label,
        Provider = ProviderKind.Microsoft,
        Instance = "7b444cd6-6fa0-4c40-8fea-89438daf5de5",
        AuthKind = AuthKind.OAuth2,
        SealedCredential = "sealed",
        State = ConnectionState.Active,
        Capabilities =
        [
            new() { Name = Capabilities.MailRead, Granted = true, Enabled = true },
            new() { Name = Capabilities.MailWrite, Granted = true, Enabled = true },
            new() { Name = Capabilities.MailSend, Granted = true, Enabled = true },
        ],
    };

    private static Connection Declared(string label = "work")
    {
        var c = MailOnly(label);
        CapabilityCatalog.DeclareMissing(c);
        return c;
    }

    // ── declaring what the build now offers ──────────────────────────────────

    [Fact]
    public void A_capability_the_connection_predates_is_declared_rather_than_absent()
    {
        var c = MailOnly();
        Assert.DoesNotContain(c.Capabilities, x => x.Name == Capabilities.CalendarRead);

        Assert.True(CapabilityCatalog.DeclareMissing(c));

        var cal = Assert.Single(c.Capabilities, x => x.Name == Capabilities.CalendarRead);
        Assert.False(cal.Granted);
        Assert.True(cal.NeverRequested);

        // Enabled, not off. The switch is the user's statement about a permission they
        // have; this one has never been offered, so leaving it off would mean the
        // reconnect they were told to do granted the permission and still did nothing.
        Assert.True(cal.Enabled);
    }

    [Fact]
    public void Declaring_leaves_every_existing_grant_and_switch_alone()
    {
        var c = MailOnly();
        c.Capabilities.Single(x => x.Name == Capabilities.MailSend).Enabled = false;

        CapabilityCatalog.DeclareMissing(c);

        var send = c.Capabilities.Single(x => x.Name == Capabilities.MailSend);
        Assert.True(send.Granted);
        Assert.False(send.Enabled);
        Assert.False(send.NeverRequested);
    }

    [Fact]
    public void Declaring_twice_adds_nothing_the_second_time()
    {
        var c = Declared();
        var before = c.Capabilities.Count;

        Assert.False(CapabilityCatalog.DeclareMissing(c));
        Assert.Equal(before, c.Capabilities.Count);
    }

    [Fact]
    public void The_catalog_and_the_Microsoft_service_declare_the_same_set()
    {
        // Two lists of the same thing in two assemblies. If they drift, a newly added
        // capability is declared onto old connections and never granted on new ones, or
        // the reverse — and either way the symptom is the one this suite exists for.
        var service = new MicrosoftConnectionService(
            Options.Create(new EntraOptions
            {
                ClientId = "id",
                ClientSecret = "secret",
                RedirectUri = "https://outpost.example/signin-microsoft",
            }),
            connections: null!,
            NullLogger<MicrosoftConnectionService>.Instance);

        Assert.Equal(
            CapabilityCatalog.DeclaredFor(ProviderKind.Microsoft).OrderBy(x => x),
            service.DeclaredCapabilities.Select(x => x.Name).OrderBy(x => x));
    }

    // ── the refusal the agent actually reads ─────────────────────────────────

    [Fact]
    public void A_calendar_request_on_a_mail_only_connection_names_the_missing_consent()
    {
        var r = ConnectionResolver.Resolve(
            [Declared("work"), Declared("home")], Capabilities.CalendarRead, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
        Assert.False(r.IsActionable);

        // The whole point: a model reading this must not be able to conclude anything
        // other than "this connection has not consented to the calendar".
        Assert.Contains("work", r.SpokenSummary);
        Assert.Contains("not consented", r.SpokenSummary);
        Assert.Contains("read your calendar", r.SpokenSummary);
        Assert.Contains(Capabilities.CalendarRead, r.SpokenSummary);
        Assert.Contains("Reconnect", r.SpokenSummary);
    }

    [Fact]
    public void The_same_holds_for_Teams_chats()
    {
        // OP2-167 was filed separately from OP2-166 because it looked like a second bug.
        // It is the same one, and this is here so it cannot regress on its own.
        var r = ConnectionResolver.Resolve(
            [Declared("work")], Capabilities.ChatRead, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
        Assert.Contains("not consented", r.SpokenSummary);
        Assert.Contains(Capabilities.ChatRead, r.SpokenSummary);
    }

    [Fact]
    public void It_no_longer_claims_the_connection_does_not_exist()
    {
        var r = ConnectionResolver.Resolve(
            [Declared("work"), Declared("home")], Capabilities.CalendarRead, "work", isWrite: false);

        // The exact sentence from the field report, which named "work" as unavailable in
        // the same breath as listing "work" among the connections available.
        Assert.DoesNotContain("No connection called", r.SpokenSummary);
        Assert.NotEqual(ResolutionKind.UnknownLabel, r.Kind);
    }

    [Fact]
    public void A_name_that_really_is_unknown_is_still_reported_as_unknown()
    {
        // The fix must not go the other way and turn every typo into a consent problem.
        var r = ConnectionResolver.Resolve(
            [Declared("work")], Capabilities.CalendarRead, "holiday", isWrite: false);

        Assert.Equal(ResolutionKind.UnknownLabel, r.Kind);
        Assert.Contains("No connection called holiday", r.SpokenSummary);
    }

    [Fact]
    public void Never_asked_and_asked_but_refused_are_different_sentences()
    {
        var never = Declared();

        var refused = Declared("other");
        var cap = refused.Capabilities.Single(x => x.Name == Capabilities.CalendarRead);
        cap.NeverRequested = false;

        var a = ConnectionResolver.Resolve([never], Capabilities.CalendarRead, "work", isWrite: false);
        var b = ConnectionResolver.Resolve([refused], Capabilities.CalendarRead, "other", isWrite: false);

        // Both say consent...
        Assert.Contains("not consented", a.SpokenSummary);
        Assert.Contains("not consented", b.SpokenSummary);

        // ...but only one of them can honestly promise that a reconnect is enough. Where
        // the tenant already refused, sending the user round the same loop is the single
        // piece of advice guaranteed not to work twice.
        Assert.Contains("never asked for", a.SpokenSummary);
        Assert.DoesNotContain("administrator", a.SpokenSummary);
        Assert.Contains("administrator", b.SpokenSummary);
    }

    [Fact]
    public void A_capability_switched_off_by_the_user_is_not_called_a_consent_problem()
    {
        var c = Declared();
        var cal = c.Capabilities.Single(x => x.Name == Capabilities.CalendarRead);
        cal.Granted = true;
        cal.NeverRequested = false;
        cal.Enabled = false;

        var r = ConnectionResolver.Resolve([c], Capabilities.CalendarRead, "work", isWrite: false);

        Assert.Contains("switched off", r.SpokenSummary);
        Assert.DoesNotContain("not consented", r.SpokenSummary);
    }

    [Fact]
    public void Asking_every_connection_still_explains_the_only_one_there_is()
    {
        var r = ConnectionResolver.Resolve([Declared("work")], Capabilities.CalendarRead, "all", isWrite: false);

        Assert.Equal(ResolutionKind.None, r.Kind);
        Assert.Contains("not consented", r.SpokenSummary);
        Assert.Contains(Capabilities.CalendarRead, r.SpokenSummary);
    }

    [Fact]
    public void Asking_every_connection_with_several_blocked_points_at_naming_one()
    {
        var r = ConnectionResolver.Resolve(
            [Declared("work"), Declared("home")], Capabilities.CalendarRead, "all", isWrite: false);

        Assert.Equal(ResolutionKind.None, r.Kind);
        Assert.Contains("work", r.SpokenSummary);
        Assert.Contains("home", r.SpokenSummary);
        Assert.Contains("permission", r.SpokenSummary);
    }

    [Fact]
    public void An_unlabelled_request_reaches_the_explanation_too()
    {
        // No label, no default, one connection — the resolver would normally ask which to
        // use. It must not: there is nothing to choose between, and "which one?" hides the
        // fact that the only candidate cannot serve the request at all.
        var r = ConnectionResolver.Resolve([Declared("work")], Capabilities.CalendarRead, null, isWrite: false);

        Assert.Equal(ResolutionKind.None, r.Kind);
        Assert.Contains("not consented", r.SpokenSummary);
    }
}
