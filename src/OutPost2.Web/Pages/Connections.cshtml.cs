using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

[Authorize]
public class ConnectionsModel(IConnectionStore store) : PageModel
{
    /// <summary>
    /// One provider's worth of the page. Grouping happens here rather than in the view so
    /// the ordering and the "no default" reasoning are testable without rendering HTML.
    /// </summary>
    public sealed record Group(
        ProviderKind Provider,
        string Heading,
        IReadOnlyList<Connection> Connections,
        Connection? Default)
    {
        public string? NoDefaultReason =>
            Default is null ? CapabilityCatalog.NoDefaultReason(Provider) : null;
    }

    public IReadOnlyList<Group> Groups { get; private set; } = [];
    public string? Error { get; private set; }
    public string? Added { get; private set; }
    public string? Replaced { get; private set; }

    public async Task OnGetAsync(string? error, string? added, string? replaced)
    {
        Error = error;
        Added = added;
        Replaced = replaced;

        var all = await store.ListAsync(User.UserId());

        // Every provider is shown, including ones with nothing connected: an empty group
        // is how a user discovers that GitHub is even an option.
        Groups =
        [
            .. Enum.GetValues<ProviderKind>()
                .Select(p =>
                {
                    var mine = all.Where(c => c.Provider == p)
                        .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();

                    return new Group(p, CapabilityCatalog.ProviderHeading(p), mine,
                        mine.FirstOrDefault(c => c.IsDefaultForProvider));
                })
        ];
    }

    // ── presentation helpers, kept here so the view stays declarative ──────

    public static string StatusWord(ConnectionState s) => s switch
    {
        ConnectionState.Active or ConnectionState.Consented => "On air",
        ConnectionState.Expired => "Expired",
        ConnectionState.Revoked => "Revoked",
        ConnectionState.NeedsAdminApproval => "Waiting on admin",
        _ => "Not connected",
    };

    public static string StatusClass(ConnectionState s) => s switch
    {
        ConnectionState.Active or ConnectionState.Consented => "active",
        ConnectionState.Expired => "expired",
        ConnectionState.Revoked => "revoked",
        _ => "pending",
    };

    /// <summary>
    /// The sentence shown when a connection cannot serve. Every one of them names the next
    /// step, because "expired" on its own tells the user nothing they can act on.
    /// </summary>
    public static (string Text, string Action)? Alert(Connection c) => c.State switch
    {
        ConnectionState.Expired => (
            c.CredentialExpiresAt is { } when
                ? $"The token expired on {when:d MMM yyyy}. Claude can't reach {c.Label} until you paste a new one. Nothing else changes."
                : $"The credential has expired. Claude can't reach {c.Label} until you replace it. Nothing else changes.",
            c.AuthKind == AuthKind.OAuth2 ? "Reconnect" : "Replace token"),

        ConnectionState.Revoked => (
            $"{c.Provider} rejected this credential. It has to be set up again.",
            c.AuthKind == AuthKind.OAuth2 ? "Reconnect" : "Replace token"),

        ConnectionState.NeedsAdminApproval => (
            "Waiting on the tenant admin to approve Outpost. Nothing you can do from here until they act.",
            "Reconnect"),

        _ => null,
    };

    /// <summary>
    /// The account line. Deliberately different per provider: a tenant GUID means nothing
    /// to a person, so it is abbreviated and labelled, while a self-hosted tracker's
    /// hostname is the thing that tells two of them apart.
    /// </summary>
    public static string AccountLine(Connection c)
    {
        var who = c.AccountIdentifier ?? "unknown account";

        return c.Provider switch
        {
            ProviderKind.Microsoft => $"{who} · work or school account",
            _ => $"{who} on {Host(c.Instance)}",
        };
    }

    public static string? TenantShort(Connection c) =>
        c.Provider == ProviderKind.Microsoft && c.Instance.Length > 12
            ? c.Instance[..8] + "…" + c.Instance[^4..]
            : null;

    private static string Host(string instance) =>
        Uri.TryCreate(instance, UriKind.Absolute, out var u) ? u.Host : instance;

    /// <summary>
    /// How this credential's lifetime works, or nothing at all. Only stated where it is
    /// known to be true: GitHub reports an expiry, Microsoft genuinely self-renews, and a
    /// YouTrack token's lifetime is not something we read — so for that one we say
    /// nothing rather than assert "never expires".
    /// </summary>
    public static string? Lifetime(Connection c)
    {
        if (c.CredentialExpiresAt is { } when)
            return $"Token expires {when:d MMM yyyy}";

        return c.Provider == ProviderKind.Microsoft ? "Sign-in renews automatically" : null;
    }

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d < TimeSpan.FromMinutes(2)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} minutes ago";
        if (d < TimeSpan.FromHours(48)) return $"{(int)d.TotalHours} hours ago";
        return $"{(int)d.TotalDays} days ago";
    }

    /// <summary>
    /// The spoken sentence with the connection's name marked up inside it. The name is
    /// HTML-encoded before the template wraps it, so a label containing markup cannot
    /// escape into the page.
    /// </summary>
    public static string SpokenExampleHtml(Connection c) =>
        CapabilityCatalog.SpokenExample(
            c.Provider,
            $"<b>{System.Net.WebUtility.HtmlEncode(c.Label)}</b>");

    /// <summary>
    /// What to say before switching a capability ON, or null where no confirmation is
    /// warranted. Switching OFF never asks: making the safe direction cost a click only
    /// teaches people to dismiss the dialog without reading it.
    /// </summary>
    public static string? EnableConfirm(Connection c, Capability cap)
    {
        if (cap.Enabled) return null;

        var risk = CapabilityCatalog.Risk(cap.Name);
        if (risk == CapabilityRisk.None) return null;

        var what = risk switch
        {
            CapabilityRisk.ActsAsYou =>
                $"This lets Claude act as you on {c.Label}. Other people will see your name on it.",
            CapabilityRisk.High =>
                $"This lets Claude create, change and delete things on {c.Label}.",
            _ => $"This lets Claude change things on {c.Label}.",
        };

        var enforcement = cap.IsSelfImposedOnly
            ? "The token already allows it; this switch is only Outpost's own restraint."
            : "The provider enforces this, so switching it on grants it for real.";

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            $"{CapabilityCatalog.Verb(cap.Name)} on {c.Label}.",
            what,
            enforcement);
    }

    /// <summary>Capabilities split by who enforces them, so each band can tell the truth.</summary>
    public static IEnumerable<IGrouping<bool, Capability>> CapabilityBands(Connection c) =>
        c.Capabilities
            .OrderBy(x => CapabilityCatalog.Risk(x.Name))
            .GroupBy(x => x.IsSelfImposedOnly)
            .OrderBy(g => g.Key);
}
