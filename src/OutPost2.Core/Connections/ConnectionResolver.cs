namespace OutPost2.Core.Connections;

/// <summary>
/// Decides WHICH connection a request acts on (OP2-2).
/// <para>
/// This is the heart of the substrate. Every tool call must resolve to an explicit
/// connection — there is no ambient "current account", because a process-global current
/// account is exactly what stops a single-user CLI becoming a multi-user service.
/// </para>
/// </summary>
public static class ConnectionResolver
{
    /// <summary>Tokens meaning "every connection that can do this", triggering fan-out.</summary>
    private static readonly HashSet<string> AllTokens =
        new(StringComparer.OrdinalIgnoreCase) { "all", "any", "every", "everywhere", "both" };

    /// <param name="candidates">The user's connections. Must already be scoped to one user.</param>
    /// <param name="capability">The capability the action needs, e.g. <c>mail.send</c>.</param>
    /// <param name="label">What the user said, if anything: "work", "all", or null.</param>
    /// <param name="isWrite">
    /// True for anything that sends, deletes or mutates. Raises an ambiguous resolution from
    /// "guess the default" to "ask", because sending from the wrong tenant is unrecoverable —
    /// you cannot unsend mail, and the recipient sees the wrong identity.
    /// </param>
    public static ConnectionResolution Resolve(
        IReadOnlyList<Connection> candidates,
        string capability,
        string? label,
        bool isWrite)
    {
        // Only usable connections are ever candidates: the right answer to "check work email"
        // when work needs re-auth is to say so, not to silently answer from home.
        var capable = candidates
            .Where(c => c.State is ConnectionState.Active or ConnectionState.Consented)
            .Where(c => c.Capabilities.Any(x => x.Name == capability && x.Usable))
            .ToList();

        // Everything that DECLARES the capability but cannot currently serve it — whether
        // because the connection is in a bad state OR because the capability is toggled
        // off or was never granted. Both must be represented: a connection that is Active
        // with mail switched off is still a connection the user knows they have, and
        // reporting "no connection called work" for it is a lie that sends them hunting.
        var unusable = candidates
            .Where(c => c.Capabilities.Any(x => x.Name == capability))
            .Except(capable)
            .ToList();

        if (!string.IsNullOrWhiteSpace(label))
        {
            if (AllTokens.Contains(label.Trim()))
            {
                return capable.Count == 0
                    ? ConnectionResolution.None(capability, unusable)
                    : ConnectionResolution.FanOut(capable, unusable);
            }

            var named = capable.FirstOrDefault(
                c => string.Equals(c.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));

            if (named is not null)
                return ConnectionResolution.Single(named, unusable);

            // Named something real that cannot serve this request: say which and why,
            // rather than falling back to a different account behind the user's back.
            var blocked = unusable.FirstOrDefault(
                c => string.Equals(c.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));

            return blocked is not null
                ? ConnectionResolution.Unavailable(blocked, capability)
                : ConnectionResolution.UnknownLabel(label.Trim(), candidates);
        }

        if (capable.Count == 0) return ConnectionResolution.None(capability, unusable);

        // Nothing is chosen on the user's behalf unless they nominated it. Being the only
        // candidate is not consent: a provider with no default means "always name one",
        // and that has to hold at one connection as well as at five — otherwise adding a
        // second tracker silently changes what the first unlabelled request does.
        var preferred = capable.FirstOrDefault(c => c.IsDefaultForProvider);
        if (preferred is null) return ConnectionResolution.Ambiguous(capable, capability);

        // A default is a convenience for reading. It is not standing permission to write
        // from an identity the user did not name in the moment, and other people see which
        // identity acted, so once there is a choice to get wrong, ask.
        if (isWrite && capable.Count > 1) return ConnectionResolution.Ambiguous(capable, capability);

        return ConnectionResolution.Single(preferred, unusable);
    }
}

public enum ResolutionKind
{
    /// <summary>Exactly one connection: proceed.</summary>
    Single,

    /// <summary>Run against several and merge, labelling results per connection.</summary>
    FanOut,

    /// <summary>Several possible and we must not guess: ask the user which.</summary>
    Ambiguous,

    /// <summary>No connection can do this at all.</summary>
    None,

    /// <summary>The named connection exists but is expired/revoked/awaiting admin.</summary>
    Unavailable,

    /// <summary>No connection by that name.</summary>
    UnknownLabel,
}

/// <summary>
/// Outcome of resolution. Every case carries a <see cref="SpokenSummary"/>, because the
/// primary surface is a voice conversation and "could not resolve connection" is not an
/// answer anyone can act on while driving.
/// </summary>
public sealed record ConnectionResolution
{
    public required ResolutionKind Kind { get; init; }
    public Connection? Connection { get; init; }
    public IReadOnlyList<Connection> Connections { get; init; } = [];

    /// <summary>
    /// Connections that hold the capability but cannot currently serve it. Surfaced even on
    /// success: "here's your work mail — note home needs re-authorising" beats silence,
    /// because silent omission reads as "you have no home mail".
    /// </summary>
    public IReadOnlyList<Connection> Degraded { get; init; } = [];

    public required string SpokenSummary { get; init; }
    public bool IsActionable => Kind is ResolutionKind.Single or ResolutionKind.FanOut;

    private static string Names(IEnumerable<Connection> cs) => string.Join(", ", cs.Select(c => c.Label));

    private static string DegradedSuffix(IReadOnlyList<Connection> degraded) =>
        degraded.Count == 0 ? "" : $" Note: {Names(degraded)} needs attention first.";

    public static ConnectionResolution Single(Connection c, IReadOnlyList<Connection>? degraded = null) => new()
    {
        Kind = ResolutionKind.Single,
        Connection = c,
        Connections = [c],
        Degraded = degraded ?? [],
        SpokenSummary = $"Using {c.Label}." + DegradedSuffix(degraded ?? []),
    };

    public static ConnectionResolution FanOut(IReadOnlyList<Connection> cs, IReadOnlyList<Connection>? degraded = null) => new()
    {
        Kind = ResolutionKind.FanOut,
        Connections = cs,
        Degraded = degraded ?? [],
        SpokenSummary = $"Checking {Names(cs)}." + DegradedSuffix(degraded ?? []),
    };

    public static ConnectionResolution Ambiguous(IReadOnlyList<Connection> cs, string capability) => new()
    {
        Kind = ResolutionKind.Ambiguous,
        Connections = cs,

        // With one candidate "Which one — lab?" is a question with a single answer, which
        // sounds broken. The reason it is being asked at all is that nothing was nominated,
        // so say that instead.
        SpokenSummary = cs.Count == 1
            ? $"Tell me which connection to use — you have {Names(cs)}."
            : $"Which one — {Names(cs)}?",
    };

    public static ConnectionResolution None(string capability, IReadOnlyList<Connection>? degraded = null) => new()
    {
        Kind = ResolutionKind.None,
        Degraded = degraded ?? [],
        SpokenSummary = (degraded ?? []).Count > 0
            ? $"{Names(degraded!)} could do that, but needs reconnecting first."
            : $"No connection set up for that yet.",
    };

    public static ConnectionResolution Unavailable(Connection c, string capability) => new()
    {
        Kind = ResolutionKind.Unavailable,
        Connection = c,
        SpokenSummary = Explain(c, capability),
    };

    /// <summary>
    /// Why this connection cannot serve the request. State problems and capability
    /// problems need different words because they need different actions from the user:
    /// one is "go and re-authorise", the other is "go and flip a switch".
    /// </summary>
    private static string Explain(Connection c, string capability)
    {
        if (c.State is not (ConnectionState.Active or ConnectionState.Consented))
            return c.State switch
            {
                ConnectionState.Expired => $"{c.Label} needs re-authorising — I'll send you a link.",
                ConnectionState.NeedsAdminApproval => $"{c.Label} is still waiting on your admin to approve it.",
                ConnectionState.Revoked => $"{c.Label} has been revoked and needs setting up again.",
                _ => $"{c.Label} isn't connected yet.",
            };

        var cap = c.Capabilities.FirstOrDefault(x => x.Name == capability);
        if (cap is null) return $"{c.Label} can't do that.";
        if (!cap.Granted) return $"{c.Label} hasn't been given permission for that yet.";
        if (!cap.Enabled) return $"That's switched off for {c.Label}. You can turn it on in settings.";
        return $"{c.Label} can't do that right now.";
    }

    public static ConnectionResolution UnknownLabel(string label, IReadOnlyList<Connection> all) => new()
    {
        Kind = ResolutionKind.UnknownLabel,
        Connections = all,
        SpokenSummary = all.Count == 0
            ? $"You don't have a connection called {label}, and none set up yet."
            : $"No connection called {label}. You have {Names(all)}.",
    };
}
