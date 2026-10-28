using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using OutPost2.Infrastructure.Feedback;
using OutPost2.Infrastructure.Users;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// Lets the model report friction with OutPost2 from inside the conversation that hit it
/// (OP2-74).
/// <para>
/// The alternative is what happens today: the model works around a rough edge, the user
/// notices it while driving, and it is forgotten by the time anyone is at a keyboard. The
/// moment the friction is felt is the only moment the detail exists.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class FeedbackTools(
    IHttpContextAccessor http,
    IUserStore users,
    IFrictionService friction)
{
    private Guid UserId => http.HttpContext?.User.UserId()
        ?? throw new InvalidOperationException("No authenticated user on this MCP request.");

    [McpServerTool(Name = "report_friction")]
    [Description("""
        Report a problem or rough edge with Outpost itself — a tool that failed, something
        you could not do, or a design that got in your way. Files it into Outpost's issue
        tracker, or comments on the matching open issue if one already exists.

        Use it when you notice the friction, without being asked, and then carry on with
        what you were doing. It is for Outpost's own behaviour, NOT for anything in the
        user's mail, tickets or code.

        Describe the shape of the problem only. Never include message contents, subjects,
        names, email addresses, ticket text or anything else from the user's data — say
        "a message with a long HTML body", not the message.
        """)]
    public async Task<string> ReportFriction(
        [Description("What you were trying to do, in one or two sentences.")] string doing,
        [Description("What went wrong, or what would work better. Be specific about the behaviour, not the data.")] string problem,
        [Description("The tool involved, e.g. 'read_mail'. Omit if it isn't about one tool.")] string? tool = null,
        [Description("One of: bug, missing, awkward.")] string kind = "awkward",
        CancellationToken ct = default)
    {
        if (!friction.IsConfigured)
            return "Friction reporting isn't wired up on this server, so I can't file that. "
                 + "Worth mentioning to Scott directly.";

        // Named rather than numbered. One line of the report is "who hit this", and several
        // people hitting the same thing is the whole signal the tracker is there to carry.
        var user = await users.FindAsync(UserId, ct);
        var reporter = user?.GitHubLogin ?? UserId.ToString();

        var outcome = await friction.ReportAsync(new FrictionReport
        {
            Tool = tool,
            Doing = doing,
            Problem = problem,
            Kind = Kind(kind),
        }, reporter, ct);

        return outcome.Spoken;
    }

    /// <summary>
    /// Forgiving on the way in. A model asked for "bug, missing, awkward" will sooner or
    /// later say "missing feature" or "defect", and refusing the report over the label
    /// would throw away the thing we actually wanted.
    /// </summary>
    private static FrictionKind Kind(string? said) => (said ?? "").Trim().ToLowerInvariant() switch
    {
        "bug" or "broken" or "defect" or "error" or "failure" => FrictionKind.Bug,
        var s when s.StartsWith("missing") || s.Contains("feature") => FrictionKind.MissingFeature,
        _ => FrictionKind.AwkwardDesign,
    };
}
