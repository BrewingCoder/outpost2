using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Infrastructure.Issues;

public sealed record IssueSummary(string Id, string Key, string Summary, string? State, string? Assignee, string Project);

public sealed record IssueComment(string Author, string Text, DateTimeOffset? At);

/// <summary>
/// Everything the tracker holds about one issue, not a chosen subset.
/// <para>
/// State and Assignee stay named because they are what gets asked for out loud, but they
/// are two custom fields among however many a project defines — Priority, Type, Due date,
/// Fix version, Subsystem, whatever an admin added last week. Picking a fixed list here
/// means every project-specific field is invisible to the conversation, and nothing about
/// it being missing is visible either: the answer just quietly lacks it.
/// </para>
/// </summary>
public sealed record IssueDetail(
    string Id, string Key, string Summary, string? Description, string? State,
    string? Assignee, string Project, string? Reporter,
    DateTimeOffset? Created, DateTimeOffset? Updated, DateTimeOffset? Resolved,
    IReadOnlyList<string> Tags,
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    IReadOnlyList<KeyValuePair<string, string>> Links,
    int CommentCount,
    IReadOnlyList<IssueComment> Comments,
    string? Url);
public sealed record ProjectSummary(string Id, string ShortName, string Name);

public interface IYouTrackService
{
    Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Connection c, CancellationToken ct = default);
    Task<IReadOnlyList<IssueSummary>> SearchAsync(Connection c, string query, int top = 20, CancellationToken ct = default);
    Task<IssueDetail?> GetIssueAsync(Connection c, string issueKey, CancellationToken ct = default);
    Task<string> CreateIssueAsync(Connection c, string projectShortName, string summary, string? description, CancellationToken ct = default);
    Task CommentAsync(Connection c, string issueKey, string text, CancellationToken ct = default);
    Task ApplyCommandAsync(Connection c, string issueKey, string command, CancellationToken ct = default);
    Task<ProjectSummary> CreateProjectAsync(Connection c, string name, string shortName, CancellationToken ct = default);
}

public sealed class YouTrackService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<YouTrackService> log) : IYouTrackService
{
    private async Task<HttpClient> ClientAsync(Connection c, CancellationToken ct)
    {
        var token = await connections.GetCredentialAsync(c, ct)
            ?? throw new InvalidOperationException($"{c.Label} has no stored token.");

        var http = httpFactory.CreateClient("youtrack");
        http.BaseAddress = new Uri(c.Instance.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Cloudflare 1010-blocks unfamiliar agents; the default .NET one is refused.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2/1.0");

        // Recorded here rather than per-operation: everything that talks to the service
        // goes through this method, so one call covers them all and none can be missed.
        // Without it "Last used" stays blank forever and the UI cannot tell a live
        // connection from one nobody has touched since the day it was added.
        await connections.TouchAsync(c.Id, ct);

        return http;
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Connection c, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var r = await http.GetFromJsonAsync<List<YtProject>>(
            "api/admin/projects?fields=id,name,shortName&$top=200", ct);

        return (r ?? []).Select(p => new ProjectSummary(p.Id, p.ShortName ?? "", p.Name ?? "")).ToList();
    }

    public async Task<IReadOnlyList<IssueSummary>> SearchAsync(
        Connection c, string query, int top = 20, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        var url = $"api/issues?query={Uri.EscapeDataString(query)}&$top={Math.Clamp(top, 1, 100)}" +
                  "&fields=id,idReadable,summary,project(shortName),customFields(name,value(name,login,fullName))";

        var r = await http.GetFromJsonAsync<List<YtIssue>>(url, ct);
        return (r ?? []).Select(Map).ToList();
    }

    public async Task<IssueDetail?> GetIssueAsync(Connection c, string issueKey, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        // Asking for the fields by name is not optional — YouTrack returns only id and
        // $type otherwise. Anything absent from this list cannot be reported, and that
        // absence is indistinguishable downstream from the issue not having the value.
        var url = $"api/issues/{Uri.EscapeDataString(issueKey)}" +
                  "?fields=id,idReadable,summary,description,project(shortName),reporter(login,fullName)," +
                  "created,updated,resolved,tags(name)," +
                  "customFields(name,value(name,login,fullName,presentation,text,minutes))," +
                  "links(direction,linkType(name,sourceToTarget,targetToSource),issues(idReadable))," +
                  "comments(text,created,author(login,fullName))";

        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var i = await resp.Content.ReadFromJsonAsync<YtIssue>(ct);
        if (i is null) return null;

        var s = Map(i);

        // Every field with a value, State and Assignee included — they are named separately
        // above for the common question, not removed from the record.
        var fields = (i.CustomFields ?? [])
            .Select(f => new { f.Name, Value = f.Describe() })
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new KeyValuePair<string, string>(f.Name!, f.Value!))
            .ToList();

        var comments = (i.Comments ?? [])
            .Select(x => new IssueComment(x.Author?.Describe() ?? "?", x.Text ?? "", Moment(x.Created)))
            .ToList();

        return new IssueDetail(
            s.Id, s.Key, s.Summary, i.Description, s.State, s.Assignee, s.Project,
            i.Reporter?.Describe(),
            Moment(i.Created), Moment(i.Updated), Moment(i.Resolved),
            (i.Tags ?? []).Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList(),
            fields,
            (i.Links ?? []).SelectMany(l => l.Describe()).ToList(),
            comments.Count,
            comments,
            $"{c.Instance.TrimEnd('/')}/issue/{s.Key}");
    }

    /// <summary>
    /// YouTrack timestamps are epoch milliseconds, and 0 means unset rather than 1970 —
    /// an unresolved issue reporting a resolution date is worse than reporting none.
    /// </summary>
    private static DateTimeOffset? Moment(long? ms) =>
        ms is null or <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);

    public async Task<string> CreateIssueAsync(
        Connection c, string projectShortName, string summary, string? description, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        // YouTrack wants the internal project id, not the short name a human says out loud.
        var projects = await ListProjectsAsync(c, ct);
        var project = projects.FirstOrDefault(p =>
            string.Equals(p.ShortName, projectShortName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No project '{projectShortName}'. Available: {string.Join(", ", projects.Select(p => p.ShortName))}");

        using var resp = await http.PostAsJsonAsync("api/issues?fields=idReadable", new
        {
            project = new { id = project.Id },
            summary,
            description,
        }, ct);

        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<YtIssue>(ct);

        log.LogInformation("Created {Key} in {Project} via {Label}.", created?.IdReadable, project.ShortName, c.Label);
        return created?.IdReadable ?? "(unknown)";
    }

    public async Task CommentAsync(Connection c, string issueKey, string text, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        using var resp = await http.PostAsJsonAsync(
            $"api/issues/{Uri.EscapeDataString(issueKey)}/comments", new { text }, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Applies a YouTrack command ("State Fixed", "for me", "Priority Critical").
    /// Commands rather than field writes because YouTrack's custom-field schemes vary per
    /// project; the command parser resolves names against whatever that project actually has.
    /// </summary>
    public async Task ApplyCommandAsync(Connection c, string issueKey, string command, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        using var resp = await http.PostAsJsonAsync("api/commands", new
        {
            query = command,
            issues = new[] { new { idReadable = issueKey } },
        }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ProjectSummary> CreateProjectAsync(
        Connection c, string name, string shortName, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        // A project needs a leader, and the only identity we can be sure of is the token's own.
        var me = await http.GetFromJsonAsync<YtUser>("api/users/me?fields=id,login,fullName", ct)
                 ?? throw new InvalidOperationException("Could not identify the token's user.");

        using var resp = await http.PostAsJsonAsync("api/admin/projects?fields=id,name,shortName", new
        {
            name,
            shortName,
            leader = new { id = me.Id },
        }, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Could not create project: {body}");
        }

        var p = await resp.Content.ReadFromJsonAsync<YtProject>(ct);
        log.LogInformation("Created project {Short} ({Name}) via {Label}.", p?.ShortName, p?.Name, c.Label);
        return new ProjectSummary(p!.Id, p.ShortName ?? shortName, p.Name ?? name);
    }

    private static IssueSummary Map(YtIssue i)
    {
        string? Field(string name) => i.CustomFields?
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?
            .Describe();

        return new IssueSummary(
            i.Id, i.IdReadable ?? i.Id, i.Summary ?? "(no summary)",
            Field("State"), Field("Assignee"), i.Project?.ShortName ?? "");
    }

    // ── wire types ───────────────────────────────────────────────────────────

    private sealed record YtProject(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("shortName")] string? ShortName);

    private sealed record YtIssue(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("idReadable")] string? IdReadable,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("project")] YtProject? Project,
        [property: JsonPropertyName("reporter")] YtUser? Reporter,
        [property: JsonPropertyName("created")] long? Created,
        [property: JsonPropertyName("updated")] long? Updated,
        [property: JsonPropertyName("resolved")] long? Resolved,
        [property: JsonPropertyName("tags")] List<YtTag>? Tags,
        [property: JsonPropertyName("customFields")] List<YtField>? CustomFields,
        [property: JsonPropertyName("links")] List<YtLink>? Links,
        [property: JsonPropertyName("comments")] List<YtComment>? Comments);

    private sealed record YtTag([property: JsonPropertyName("name")] string? Name);

    /// <summary>
    /// A link's wording depends on which end you are standing at: the same relationship is
    /// "parent for" from one side and "subtask of" from the other. YouTrack gives both
    /// phrasings plus a direction, so the right one can be chosen rather than guessed.
    /// </summary>
    private sealed record YtLink(
        [property: JsonPropertyName("direction")] string? Direction,
        [property: JsonPropertyName("linkType")] YtLinkType? LinkType,
        [property: JsonPropertyName("issues")] List<YtIssueRef>? Issues)
    {
        public IEnumerable<KeyValuePair<string, string>> Describe()
        {
            var keys = (Issues ?? []).Select(x => x.IdReadable).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (keys.Count == 0) yield break;

            // BOTH means the relation is symmetric (duplicates, relates), so either
            // phrasing is correct; OUTWARD reads source-to-target, INWARD the reverse.
            var label = Direction?.ToUpperInvariant() switch
            {
                "INWARD" => LinkType?.TargetToSource,
                _ => LinkType?.SourceToTarget,
            } ?? LinkType?.Name;

            if (string.IsNullOrWhiteSpace(label)) yield break;
            yield return new KeyValuePair<string, string>(label!, string.Join(", ", keys!));
        }
    }

    private sealed record YtLinkType(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("sourceToTarget")] string? SourceToTarget,
        [property: JsonPropertyName("targetToSource")] string? TargetToSource);

    private sealed record YtIssueRef([property: JsonPropertyName("idReadable")] string? IdReadable);

    /// <summary>
    /// A custom field's value is NOT consistently an object. YouTrack returns an object for
    /// enum/state/user fields, an ARRAY for multi-value fields (Fix versions, Sprints), and
    /// a bare string or number for simple text/integer fields — plus null when unset.
    /// Deserialising into a fixed shape throws on the first issue that uses any of them, so
    /// the value is kept raw and interpreted defensively.
    /// </summary>
    private sealed record YtField(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("value")] JsonElement Value)
    {
        public string? Describe() => Describe(Value);

        private static string? Describe(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => Pick(e),
            JsonValueKind.Array => Join(e),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.ToString(),
            JsonValueKind.True or JsonValueKind.False => e.GetBoolean().ToString(),
            _ => null,
        };

        private static string? Pick(JsonElement o)
        {
            foreach (var key in (string[])["fullName", "name", "login", "presentation", "text"])
                if (o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();

            // A period field (Estimation, Spent time) carries no string at all, only a
            // minute count. Without this it describes as null and reads as unset.
            if (o.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number)
            {
                var mins = m.GetInt32();
                return mins >= 60 ? $"{mins / 60}h {mins % 60}m" : $"{mins}m";
            }

            return null;
        }

        private static string? Join(JsonElement arr)
        {
            var parts = arr.EnumerateArray().Select(Describe).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    private sealed record YtUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("fullName")] string? FullName)
    {
        public string Describe() => FullName ?? Login ?? Id;
    }

    private sealed record YtComment(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("created")] long? Created,
        [property: JsonPropertyName("author")] YtUser? Author);
}
