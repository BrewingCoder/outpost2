using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Issues;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// YouTrack tools. Same resolver, same capability model as mail — which is the point:
/// a second provider plugs in without re-solving tenancy, naming or consent (D4).
/// </summary>
[McpServerToolType]
public sealed class IssueTools(
    IHttpContextAccessor http,
    IConnectionStore connections,
    IYouTrackService youtrack)
{
    private Guid UserId => http.HttpContext?.User.UserId()
        ?? throw new InvalidOperationException("No authenticated user on this MCP request.");

    private async Task<(Connection? Conn, string? Message)> ResolveAsync(
        string? connection, string capability, bool isWrite, CancellationToken ct)
    {
        // Filtered to this provider before resolving. Capability names are meant to be
        // provider-agnostic, so without this a connection for a different provider could
        // win a label match and then be called with an API it does not speak.
        var candidates = (await connections.ListAsync(UserId, ct))
            .Where(c => c.Provider == ProviderKind.YouTrack)
            .ToList();

        var r = ConnectionResolver.Resolve(candidates, capability, connection, isWrite);

        // FanOut is meaningful for mail but not for a write to one tracker, so anything
        // other than a single resolved connection is reported rather than guessed at.
        if (r.Kind == ResolutionKind.Single) return (r.Connection, null);

        // SpokenSummary is written to be said out loud, so it asks the question and stops.
        // The caller here is a model, which also needs telling what to do about it —
        // otherwise "Which one?" reads as a dead end rather than a retriable call.
        var message = r.Kind == ResolutionKind.Ambiguous
            ? r.SpokenSummary + " (Ask the user, then call again with the connection name.)"
            : r.SpokenSummary;

        return (null, message);
    }

    [McpServerTool(Name = "list_projects")]
    [Description("List projects on a YouTrack connection.")]
    public async Task<string> ListProjects(
        [Description("Connection name, e.g. 'lab'. Required unless a default tracker has been set.")] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.IssuesRead, false, ct);
        if (c is null) return msg!;

        var projects = await youtrack.ListProjectsAsync(c, ct);
        if (projects.Count == 0) return $"No projects on {c.Label}.";

        return $"Projects on {c.Label}:\n" +
               string.Join("\n", projects.Select(p => $"- {p.ShortName} — {p.Name}"));
    }

    [McpServerTool(Name = "search_issues")]
    [Description("Search issues using YouTrack query syntax, e.g. 'project: OP2 State: Open' or 'for: me'.")]
    public async Task<string> SearchIssues(
        [Description("YouTrack query, e.g. 'project: OP2 #Unresolved'.")] string query,
        [Description("Connection name, e.g. 'lab'. Required unless a default tracker has been set.")] string? connection = null,
        [Description("How many to return.")] int count = 20,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.IssuesRead, false, ct);
        if (c is null) return msg!;

        var issues = await youtrack.SearchAsync(c, query, count, ct);
        if (issues.Count == 0) return $"No issues match \"{query}\" on {c.Label}.";

        var sb = new StringBuilder($"{issues.Count} issue{(issues.Count == 1 ? "" : "s")} on {c.Label}:\n");
        foreach (var i in issues)
        {
            sb.Append($"- {i.Key}: {i.Summary}");
            if (!string.IsNullOrWhiteSpace(i.State)) sb.Append($" [{i.State}]");
            if (!string.IsNullOrWhiteSpace(i.Assignee)) sb.Append($" — {i.Assignee}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_issue")]
    [Description("Read one issue in full: reporter, dates, every field, links, comments and the full description. Issue keys like OP2-14 are speakable, so they are used directly. A very long description is returned in parts; if the reply says it continues, call again with descriptionFrom before answering any question about its later sections.")]
    public async Task<string> GetIssue(
        [Description("Issue key, e.g. OP2-14.")] string issueKey,
        [Description("Connection name, e.g. 'lab'. Required unless a default tracker has been set.")] string? connection = null,
        [Description("Character offset to resume the description from. Only needed when a previous call said the description continues; pass the offset it gave you.")] int descriptionFrom = 0,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.IssuesRead, false, ct);
        if (c is null) return msg!;

        var i = await youtrack.GetIssueAsync(c, issueKey, ct);
        if (i is null) return $"No issue {issueKey} on {c.Label}.";

        return Describe(i, descriptionFrom);
    }

    /// <summary>
    /// Renders an issue for the conversation. Static and separate from the tool because
    /// this is where the bug was: the service fetched the reporter and this step dropped
    /// it, which is not reachable by any test that stops at the service.
    /// </summary>
    // Large enough that almost nothing is cut at all. The old 4,000 was small enough to
    // land inside an ordinary acceptance-criteria list, which is exactly the content
    // someone asks about out loud and exactly the content that must not arrive partial.
    private const int DescriptionWindow = 12_000;
    private const int CommentWindow = 2_000;

    /// <summary>
    /// Takes at most <paramref name="max"/> characters from <paramref name="from"/>, ending
    /// on a boundary, and reports how many characters are left after it.
    /// <para>
    /// Cutting at a fixed offset lands mid-word and mid-sentence, and a half-sentence reads
    /// as a whole one. Backing up to a paragraph, line or sentence end costs a few hundred
    /// characters and makes the break visible as a break.
    /// </para>
    /// </summary>
    private static (string Text, int Remaining) Window(string s, int from, int max)
    {
        from = Math.Clamp(from, 0, s.Length);
        var rest = s[from..];
        if (rest.Length <= max) return (rest, 0);

        var slice = rest[..max];

        // Only accept a boundary in the last quarter of the window; further back than that
        // and honouring it would throw away more text than the tidier break is worth.
        var floor = max * 3 / 4;
        var cut = -1;
        foreach (var sep in (string[])["\n\n", "\n", ". ", "? ", "! ", " "])
        {
            var at = slice.LastIndexOf(sep, StringComparison.Ordinal);
            if (at <= floor) continue;
            cut = at + (sep == " " ? 0 : sep.Length);
            break;
        }

        var text = (cut > 0 ? slice[..cut] : slice).TrimEnd();
        return (text, rest.Length - (cut > 0 ? cut : max));
    }

    public static string Describe(IssueDetail i, int descriptionFrom = 0)
    {
        // Everything the tracker holds, not a chosen subset. The service was already
        // fetching the reporter while this method printed only state and assignee, so
        // "who filed this?" came back as a limitation of the tracker rather than as the
        // answer sitting in the response. Anything omitted here is unanswerable out loud
        // and gives no sign that it was omitted.
        var sb = new StringBuilder();
        sb.AppendLine($"{i.Key}: {i.Summary}");

        sb.AppendLine($"Project {i.Project}" +
                      (i.State is null ? "" : $", state {i.State}") +
                      (i.Assignee is null ? "" : $", assigned to {i.Assignee}"));

        if (i.Reporter is not null)
        {
            sb.AppendLine($"Reported by {i.Reporter}" +
                          (i.Created is { } cr ? $" on {When(cr)}" : ""));
        }
        else if (i.Created is { } created)
        {
            sb.AppendLine($"Created {When(created)}");
        }

        if (i.Updated is { } up) sb.AppendLine($"Last updated {When(up)}");
        if (i.Resolved is { } res) sb.AppendLine($"Resolved {When(res)}");

        // State and Assignee are already on the line above; repeating them wastes the
        // listener's attention on a re-read of what was just said.
        var rest = i.Fields
            .Where(f => !f.Key.Equals("State", StringComparison.OrdinalIgnoreCase)
                     && !f.Key.Equals("Assignee", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rest.Count > 0)
        {
            sb.AppendLine();
            foreach (var f in rest) sb.AppendLine($"{f.Key}: {f.Value}");
        }

        if (i.Tags.Count > 0) sb.AppendLine($"Tags: {string.Join(", ", i.Tags)}");

        if (i.Links.Count > 0)
        {
            sb.AppendLine();
            foreach (var l in i.Links) sb.AppendLine($"{l.Key}: {l.Value}");
        }

        if (!string.IsNullOrWhiteSpace(i.Description))
        {
            sb.AppendLine();

            var (text, remaining) = Window(i.Description, descriptionFrom, DescriptionWindow);
            sb.AppendLine(text);

            if (remaining > 0)
            {
                // A bare ellipsis says something was cut and nothing about what, how much,
                // or how to get it. An acceptance-criteria list ending in "…" reads as the
                // end of the list, and the criterion that was dropped is invisible.
                sb.AppendLine();
                sb.AppendLine($"[{remaining:N0} more characters of this description. " +
                              $"Call get_issue again with descriptionFrom: {descriptionFrom + text.Length} " +
                              "to read on — do that before summarising acceptance criteria.]");
            }
        }

        if (i.CommentCount > 0)
        {
            sb.AppendLine();

            // Say how many there are before showing a subset, so a truncated view is never
            // mistaken for the whole conversation.
            var shown = i.Comments.TakeLast(10).ToList();
            sb.AppendLine(shown.Count < i.CommentCount
                ? $"{i.CommentCount} comments, most recent {shown.Count}:"
                : $"{i.CommentCount} comment{(i.CommentCount == 1 ? "" : "s")}:");

            foreach (var cm in shown)
            {
                var (text, remaining) = Window(cm.Text, 0, CommentWindow);
                var more = remaining > 0 ? $" [+{remaining:N0} more characters]" : "";
                sb.AppendLine($"- {cm.Author}{(cm.At is { } at ? $" ({When(at)})" : "")}: {text}{more}");
            }
        }

        if (i.Url is not null)
        {
            sb.AppendLine();
            sb.AppendLine(i.Url);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Absolute, and in the reader's own zone. "3 days ago" is friendlier to say and
    /// useless to act on — a date that has to be converted before it can be quoted in a
    /// standup or compared against a release is worse than one that reads a little stiffly.
    /// </summary>
    private static string When(DateTimeOffset t) => t.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    [McpServerTool(Name = "create_issue")]
    [Description("Create an issue. Always name the connection and project.")]
    public async Task<string> CreateIssue(
        [Description("Project short name, e.g. OP2.")] string project,
        [Description("Issue summary.")] string summary,
        [Description("Longer description. Optional.")] string? description = null,
        [Description("Connection name, e.g. 'lab'. Required unless a default tracker has been set.")] string? connection = null,
        CancellationToken ct = default)
    {
        // isWrite: creating in the wrong tracker is visible to a whole team.
        var (c, msg) = await ResolveAsync(connection, Capabilities.IssuesWrite, true, ct);
        if (c is null) return msg!;

        try
        {
            var key = await youtrack.CreateIssueAsync(c, project, summary, description, ct);
            return $"Created {key} on {c.Label}.";
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "comment_on_issue")]
    [Description("Add a comment to an issue.")]
    public async Task<string> CommentOnIssue(
        [Description("Issue key, e.g. OP2-14.")] string issueKey,
        [Description("Comment text.")] string text,
        [Description("Connection name, e.g. 'lab'. Required unless a default tracker has been set.")] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.IssuesWrite, true, ct);
        if (c is null) return msg!;

        await youtrack.CommentAsync(c, issueKey, text, ct);
        return $"Commented on {issueKey}.";
    }

    [McpServerTool(Name = "update_issue")]
    [Description("Apply a YouTrack command to an issue, e.g. 'State Fixed', 'Priority Critical', 'for me'.")]
    public async Task<string> UpdateIssue(
        [Description("Issue key, e.g. OP2-14.")] string issueKey,
        [Description("YouTrack command, e.g. 'State Fixed'.")] string command,
        [Description("Connection name, e.g. 'lab'. Required unless a default tracker has been set.")] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.IssuesWrite, true, ct);
        if (c is null) return msg!;

        await youtrack.ApplyCommandAsync(c, issueKey, command, ct);
        return $"Applied \"{command}\" to {issueKey}.";
    }

    [McpServerTool(Name = "create_project")]
    [Description("Create a new YouTrack project. Requires the admin capability, which is switched off by default.")]
    public async Task<string> CreateProject(
        [Description("Project name, e.g. 'OutPost2'.")] string name,
        [Description("Short key, e.g. 'OP2'.")] string shortName,
        [Description("Connection name, e.g. 'lab'. Required unless a default tracker has been set.")] string? connection = null,
        CancellationToken ct = default)
    {
        // A separate capability from write, and off unless deliberately enabled — creating
        // a project is visible to everyone on the server and awkward to undo, so it must
        // not ride on the same switch as "read my tickets" when a voice turn can trigger
        // either by accident.
        var (c, msg) = await ResolveAsync(connection, Capabilities.IssuesAdmin, true, ct);
        if (c is null) return msg!;

        try
        {
            var p = await youtrack.CreateProjectAsync(c, name, shortName, ct);
            return $"Created project {p.ShortName} ({p.Name}) on {c.Label}.";
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }
}
