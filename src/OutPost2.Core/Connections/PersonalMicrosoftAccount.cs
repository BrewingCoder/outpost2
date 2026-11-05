namespace OutPost2.Core.Connections;

/// <summary>
/// Personal Microsoft accounts — outlook.com, hotmail.com, live.com (OP2-81).
/// <para>
/// Connected through a deliberately SEPARATE path rather than by widening the sign-in to
/// <c>/common</c>. A personal account can be registered under a work email address, and
/// <c>/common</c> then shows two picker entries that read identically — which is how the
/// first real connection ended up on the wrong mailbox. So the user says up front which
/// kind they mean, work sign-ins stay on <c>/organizations</c>, and personal ones go to
/// <c>/consumers</c>, where the work account is never offered either.
/// </para>
/// </summary>
public static class PersonalMicrosoftAccount
{
    /// <summary>
    /// The tenant hint that marks a pending sign-in as personal. Microsoft's own name for
    /// the consumer authority segment, so it also works unchanged as an MSAL tenant.
    /// </summary>
    public const string TenantHint = "consumers";

    /// <summary>
    /// Whether a tenant hint asks for a personal account: the <c>consumers</c> segment a
    /// new connection starts with, or the consumer tenant id a reconnect carries.
    /// </summary>
    public static bool IsHint(string? tenantHint) =>
        string.Equals(tenantHint?.Trim(), TenantHint, StringComparison.OrdinalIgnoreCase)
        || string.Equals(tenantHint?.Trim(), Connection.ConsumerTenantId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this connection is a personal Microsoft account.</summary>
    public static bool Is(Connection c) =>
        c.Provider == ProviderKind.Microsoft
        && (c.IsPersonalAccount
            || string.Equals(c.Instance, Connection.ConsumerTenantId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Capabilities Microsoft Graph does not offer to personal accounts at all. Teams chat
    /// is work-or-school only. Declared on the connection anyway, ungranted, so a request
    /// for one is answered with WHY rather than "no connection called home".
    /// </summary>
    public static readonly IReadOnlySet<string> UnsupportedCapabilities =
        new HashSet<string>(StringComparer.Ordinal) { Capabilities.ChatRead, Capabilities.ChatSend };

    /// <summary>Whether <paramref name="capability"/> can never work on this connection.</summary>
    public static bool CannotEver(Connection c, string capability) =>
        Is(c) && UnsupportedCapabilities.Contains(capability);

    /// <summary>The sentence for a feature Microsoft keeps to work or school accounts.</summary>
    public static string NotOffered(Connection c, string what) =>
        $"{c.Label} is a personal Microsoft account, and Microsoft only offers {what} to work " +
        "or school accounts. Use a work connection for that.";
}
