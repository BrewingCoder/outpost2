namespace OutPost2.Web;

/// <summary>
/// Where this deployment actually lives, for the links the tools read out loud.
/// <para>
/// These URLs are spoken to a user mid-conversation — "add one at &lt;...&gt;/connections" — so
/// they were originally written as literals. That made every instance of the product tell
/// its users to go and configure themselves on somebody else's host, and it would have
/// been a confusing failure rather than an obvious one: the page loads, it is simply the
/// wrong person's account.
/// </para>
/// <para>
/// There is deliberately no default. A wrong base URL is worse than a missing one, because
/// a missing one fails at startup where someone is watching.
/// </para>
/// </summary>
public sealed class PublicUrls
{
    public PublicUrls(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                "OutPost2:PublicBaseUrl is not configured. It is the address users reach this " +
                "deployment on, and it appears in OAuth metadata and in links the tools speak.");

        Base = baseUrl.TrimEnd('/');
    }

    public string Base { get; }

    public string Connections => $"{Base}/connections";
    public string Files => $"{Base}/files";
    public string Jobs => $"{Base}/jobs";
}
