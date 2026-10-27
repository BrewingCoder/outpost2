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
        var r = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), capability, connection, isWrite);

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
    [Description("Read one issue in full, including its comments. Issue keys like OP2-14 are speakable, so they are used directly.")]
    public async Task<string> GetIssue(
        [Description("Issue key, e.g. OP2-14.")] string issueKey,
        [Description("Connection name, e.g. 'lab'. Required unless a default tracker has been set.")] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.IssuesRead, false, ct);
        if (c is null) return msg!;

        var i = await youtrack.GetIssueAsync(c, issueKey, ct);
        if (i is null) return $"No issue {issueKey} on {c.Label}.";

        var sb = new StringBuilder();
        sb.AppendLine($"{i.Key}: {i.Summary}");
        sb.AppendLine($"Project {i.Project}" +
                      (i.State is null ? "" : $", state {i.State}") +
                      (i.Assignee is null ? "" : $", assigned to {i.Assignee}"));
        if (!string.IsNullOrWhiteSpace(i.Description))
        {
            sb.AppendLine();
            sb.AppendLine(i.Description.Length > 4000 ? i.Description[..4000] + "…" : i.Description);
        }
        if (i.Comments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{i.Comments.Count} comment{(i.Comments.Count == 1 ? "" : "s")}:");
            foreach (var cm in i.Comments.TakeLast(5)) sb.AppendLine($"- {cm}");
        }
        return sb.ToString().TrimEnd();
    }

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
