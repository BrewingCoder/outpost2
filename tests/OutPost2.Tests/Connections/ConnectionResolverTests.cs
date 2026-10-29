using OutPost2.Core.Connections;

namespace OutPost2.Tests.Connections;

public class ConnectionResolverTests
{
    private const string Read = Capabilities.MailRead;
    private const string Send = Capabilities.MailSend;

    private static Connection Conn(
        string label,
        ConnectionState state = ConnectionState.Active,
        bool isDefault = false,
        ProviderKind provider = ProviderKind.Microsoft,
        params (string Name, bool Granted, bool Enabled)[] caps)
    {
        caps = caps.Length == 0 ? [(Read, true, true), (Send, true, true)] : caps;
        return new Connection
        {
            UserId = Guid.Empty,
            Label = label,
            Provider = provider,
            Instance = $"{label}.example",
            AuthKind = AuthKind.OAuth2,
            SealedCredential = $"ref/{label}",
            State = state,
            IsDefaultForProvider = isDefault,
            Capabilities = caps.Select(c => new Capability
            {
                Name = c.Name, Granted = c.Granted, Enabled = c.Enabled,
            }).ToList(),
        };
    }

    // ── the happy paths ──────────────────────────────────────────────────────

    [Fact]
    public void Explicit_label_wins()
    {
        var r = ConnectionResolver.Resolve([Conn("work"), Conn("home", isDefault: true)], Read, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Single, r.Kind);
        Assert.Equal("work", r.Connection!.Label);
    }

    [Fact]
    public void Label_match_is_case_insensitive_and_trimmed()
    {
        var r = ConnectionResolver.Resolve([Conn("work")], Read, "  WORK ", isWrite: false);
        Assert.Equal("work", r.Connection!.Label);
    }

    [Fact]
    public void Sole_capable_connection_is_still_not_chosen_unless_it_is_the_default()
    {
        // Being the only candidate is not consent. If it were, adding a second connection
        // would silently change what an unlabelled request already does — the user would
        // have configured the change without ever being shown it.
        var r = ConnectionResolver.Resolve([Conn("work")], Read, null, isWrite: false);

        Assert.Equal(ResolutionKind.Ambiguous, r.Kind);
        Assert.False(r.IsActionable);
        Assert.Contains("work", r.SpokenSummary);

        // ...and it must not ask "which one?" of a list with one entry.
        Assert.DoesNotContain("Which one", r.SpokenSummary);
    }

    [Fact]
    public void Sole_capable_connection_resolves_once_it_is_made_the_default()
    {
        var r = ConnectionResolver.Resolve([Conn("work", isDefault: true)], Read, null, isWrite: false);

        Assert.Equal(ResolutionKind.Single, r.Kind);
        Assert.Equal("work", r.Connection!.Label);
    }

    [Fact]
    public void A_provider_with_no_default_asks_rather_than_guessing()
    {
        var r = ConnectionResolver.Resolve([Conn("lab"), Conn("indelible")], Read, null, isWrite: false);

        Assert.Equal(ResolutionKind.Ambiguous, r.Kind);
        Assert.Contains("lab", r.SpokenSummary);
        Assert.Contains("indelible", r.SpokenSummary);
    }

    [Fact]
    public void Unlabelled_read_falls_back_to_the_provider_default()
    {
        var r = ConnectionResolver.Resolve([Conn("work"), Conn("home", isDefault: true)], Read, null, isWrite: false);

        Assert.Equal(ResolutionKind.Single, r.Kind);
        Assert.Equal("home", r.Connection!.Label);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("everywhere")]
    [InlineData("both")]
    public void Fan_out_tokens_select_every_capable_connection(string token)
    {
        var r = ConnectionResolver.Resolve([Conn("work"), Conn("home")], Read, token, isWrite: false);

        Assert.Equal(ResolutionKind.FanOut, r.Kind);
        Assert.Equal(2, r.Connections.Count);
    }

    // ── the safety properties — these are the point of the class ─────────────

    [Fact]
    public void Ambiguous_write_asks_rather_than_using_the_default()
    {
        // Sending from the wrong tenant is unrecoverable: you cannot unsend, and the
        // recipient sees the wrong identity. So a write must never ride on a default.
        var r = ConnectionResolver.Resolve([Conn("work", isDefault: true), Conn("home")], Send, null, isWrite: true);

        Assert.Equal(ResolutionKind.Ambiguous, r.Kind);
        Assert.Contains("work", r.SpokenSummary);
        Assert.Contains("home", r.SpokenSummary);
        Assert.False(r.IsActionable);
    }

    [Fact]
    public void Ambiguous_read_with_no_default_also_asks()
    {
        var r = ConnectionResolver.Resolve([Conn("work"), Conn("home")], Read, null, isWrite: false);
        Assert.Equal(ResolutionKind.Ambiguous, r.Kind);
    }

    [Fact]
    public void Expired_named_connection_reports_itself_and_does_not_silently_substitute()
    {
        // The right answer to "check work email" when work is expired is to say so —
        // not to quietly answer from the home mailbox.
        var r = ConnectionResolver.Resolve(
            [Conn("work", ConnectionState.Expired), Conn("home")], Read, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
        Assert.Equal("work", r.Connection!.Label);
        Assert.DoesNotContain("home", r.SpokenSummary);
        Assert.Contains("re-authorising", r.SpokenSummary);
    }

    [Fact]
    public void Connection_awaiting_admin_approval_says_so_specifically()
    {
        var r = ConnectionResolver.Resolve(
            [Conn("work", ConnectionState.NeedsAdminApproval)], Read, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
        Assert.Contains("admin", r.SpokenSummary);
    }

    [Fact]
    public void A_connection_waiting_on_an_admin_hands_over_the_link_to_forward()
    {
        // The whole point of OP2-100: a user who cannot make their admin act must come
        // away from the refusal holding something they can send. Saying "ask your admin"
        // and stopping is the behaviour this replaces.
        var c = Conn("work", ConnectionState.NeedsAdminApproval);
        c.AdminConsentUrl = "https://login.microsoftonline.com/contoso.com/v2.0/adminconsent?client_id=x";

        var r = ConnectionResolver.Resolve([c], Read, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
        Assert.Contains(c.AdminConsentUrl, r.SpokenSummary);

        // On its own line, so a voice client can speak the sentence and skip the URL.
        Assert.Contains('\n', r.SpokenSummary);
    }

    [Fact]
    public void Once_the_admin_has_approved_the_user_is_not_told_to_keep_waiting()
    {
        // The failure this guards against is a connection parked forever: the admin acted
        // days ago, and we are still telling the user the ball is in the admin's court.
        var c = Conn("work", ConnectionState.NeedsAdminApproval);
        c.AdminConsentUrl = "https://login.microsoftonline.com/contoso.com/v2.0/adminconsent?client_id=x";
        c.AdminConsentGrantedAt = DateTimeOffset.UtcNow.AddDays(-2);

        var r = ConnectionResolver.Resolve([c], Read, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
        Assert.Contains("approved", r.SpokenSummary);
        Assert.Contains("sign in", r.SpokenSummary);

        // And the link is spent — offering it again sends the admin round a second time.
        Assert.DoesNotContain("adminconsent", r.SpokenSummary);
    }

    [Fact]
    public void A_parked_connection_is_still_found_by_name()
    {
        // A parked connection declares every capability with none granted. If it declared
        // none at all it would fall out of BOTH the capable and the unusable lists, and
        // "check work email" would answer "no connection called work" for a connection
        // the user can see on the settings page.
        var parked = Conn("work", ConnectionState.NeedsAdminApproval,
            caps: [(Read, false, true), (Send, false, true)]);

        var r = ConnectionResolver.Resolve([parked], Read, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
        Assert.NotEqual(ResolutionKind.UnknownLabel, r.Kind);
    }

    [Fact]
    public void Granted_but_disabled_capability_is_not_usable()
    {
        // "Off" must mean off. Granted-and-disabled is a legitimate state and the
        // resolver must honour the toggle rather than the grant.
        var r = ConnectionResolver.Resolve(
            [Conn("work", caps: [(Read, true, false)])], Read, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
    }

    [Fact]
    public void Enabled_but_ungranted_capability_is_not_usable()
    {
        var r = ConnectionResolver.Resolve(
            [Conn("work", caps: [(Read, false, true)])], Read, "work", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
    }

    [Fact]
    public void Fan_out_excludes_broken_connections_but_still_reports_them()
    {
        // Silent omission reads as "you have no home mail", which is a lie.
        var r = ConnectionResolver.Resolve(
            [Conn("work"), Conn("home", ConnectionState.Expired)], Read, "all", isWrite: false);

        Assert.Equal(ResolutionKind.FanOut, r.Kind);
        Assert.Single(r.Connections);
        Assert.Single(r.Degraded);
        Assert.Contains("home", r.SpokenSummary);
    }

    [Fact]
    public void Unknown_label_lists_what_does_exist()
    {
        var r = ConnectionResolver.Resolve([Conn("work"), Conn("home")], Read, "personal", isWrite: false);

        Assert.Equal(ResolutionKind.UnknownLabel, r.Kind);
        Assert.Contains("work", r.SpokenSummary);
        Assert.Contains("home", r.SpokenSummary);
    }

    [Fact]
    public void No_connections_at_all_is_actionable_advice_not_an_error()
    {
        var r = ConnectionResolver.Resolve([], Read, null, isWrite: false);

        Assert.Equal(ResolutionKind.None, r.Kind);
        Assert.False(string.IsNullOrWhiteSpace(r.SpokenSummary));
    }

    [Fact]
    public void A_connection_for_another_capability_does_not_satisfy_this_one()
    {
        var r = ConnectionResolver.Resolve(
            [Conn("work", caps: [(Read, true, true)])], Send, null, isWrite: true);

        Assert.Equal(ResolutionKind.None, r.Kind);
    }

    [Fact]
    public void Every_outcome_carries_a_spoken_summary()
    {
        // The primary surface is a voice conversation. "Could not resolve connection" is
        // not something anyone can act on while driving, so no path may return silence.
        ConnectionResolution[] all =
        [
            ConnectionResolver.Resolve([Conn("work")], Read, null, false),
            ConnectionResolver.Resolve([Conn("work"), Conn("home")], Read, "all", false),
            ConnectionResolver.Resolve([Conn("work"), Conn("home")], Send, null, true),
            ConnectionResolver.Resolve([], Read, null, false),
            ConnectionResolver.Resolve([Conn("work", ConnectionState.Revoked)], Read, "work", false),
            ConnectionResolver.Resolve([Conn("work")], Read, "nope", false),
        ];

        Assert.All(all, r => Assert.False(string.IsNullOrWhiteSpace(r.SpokenSummary)));
    }
}
