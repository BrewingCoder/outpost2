using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Infrastructure.Issues;

public sealed record IssueSummary(string Id, string Key, string Summary, string? State, string? Assignee, string Project);
public sealed record IssueDetail(
    string Id, string Key, string Summary, string? Description, string? State,
    string? Assignee, string Project, string? Reporter, IReadOnlyList<string> Comments);
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

        var url = $"api/issues/{Uri.EscapeDataString(issueKey)}" +
                  "?fields=id,idReadable,summary,description,project(shortName),reporter(login,fullName)," +
                  "customFields(name,value(name,login,fullName)),comments(text,author(login,fullName))";

        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var i = await resp.Content.ReadFromJsonAsync<YtIssue>(ct);
        if (i is null) return null;

        var s = Map(i);
        return new IssueDetail(s.Id, s.Key, s.Summary, i.Description, s.State, s.Assignee, s.Project,
            i.Reporter?.Describe(),
            (i.Comments ?? []).Select(x => $"{x.Author?.Describe() ?? "?"}: {x.Text}").ToList());
    }

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
        [property: JsonPropertyName("customFields")] List<YtField>? CustomFields,
        [property: JsonPropertyName("comments")] List<YtComment>? Comments);

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
        [property: JsonPropertyName("author")] YtUser? Author);
}
