using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Infrastructure.Issues;

/// <summary>
/// A Jira failure, in a sentence. An <see cref="InvalidOperationException"/> because that
/// is what the issue tools already catch and say back, scrubbed, as YouTrack's are.
/// </summary>
public sealed class JiraException(string message) : InvalidOperationException(message);

/// <summary>
/// Jira Cloud behind the issue tools (OP2-82), over REST v3 at the site's own address.
/// <para>
/// Not YouTrack, and the differences are the whole of this class:
/// </para>
/// <list type="bullet">
/// <item>The query is JQL, sent to <c>POST /rest/api/3/search/jql</c>. The older
/// <c>/rest/api/3/search</c> is deprecated and being removed (CHANGE-2046); its successor
/// pages by token, returns no total, and refuses an unbounded query.</item>
/// <item>Descriptions and comments are Atlassian Document Format trees, read and written
/// through <see cref="AtlassianDocument"/>.</item>
/// <item>A status changes through a workflow transition, not by setting a field, and
/// which transitions exist depends on where the issue is now. <c>update_issue</c> takes a
/// small command language mapped onto transitions, the assignee, priority and labels —
/// see <see cref="ApplyCommandAsync"/>.</item>
/// <item>Comments come from their own endpoint, paged by the server (100 at a time), and
/// are fetched oldest first across every page so that the tools' numbering — #1 is the
/// oldest — means the same thing it does on YouTrack.</item>
/// <item>Every issue needs an issue type, which YouTrack has no notion of at creation.</item>
/// <item>There is no project creation; see <see cref="NoProjectCreation"/>.</item>
/// </list>
/// <para>
/// Written against Atlassian's published v3 OpenAPI description, not a live site.
/// </para>
/// </summary>
public sealed partial class JiraService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<JiraService> log) : IIssueProvider
{
    public ProviderKind Provider => ProviderKind.Jira;

    /// <summary>The comment endpoint's documented default page; the server may send fewer.</summary>
    private const int CommentPage = 100;

    /// <summary>
    /// Fifty pages of comments. Far beyond any real thread, and a bound on what one
    /// <c>get_issue</c> can cost. Past it the count still comes from the server.
    /// </summary>
    private const int MaxCommentPages = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string NoProjectCreation(string label) =>
        $"{label} is a Jira connection, and Outpost can't create Jira projects: a Jira Cloud project needs a "
        + "template, a project type and a lead chosen by a site administrator. Create it in Jira; list_projects "
        + "will show it as soon as it exists.";

    private async Task<HttpClient> ClientAsync(Connection c, CancellationToken ct)
    {
        var credential = await connections.GetCredentialAsync(c, ct)
            ?? throw new JiraException($"{c.Label} has no stored credential. Reconnect it.");

        var http = JiraConnectionService.Configure(httpFactory.CreateClient("jira"), c.Instance, credential);
        await connections.TouchAsync(c.Id, ct);
        return http;
    }

    private static string Host(Connection c) =>
        Uri.TryCreate(c.Instance, UriKind.Absolute, out var u) ? u.Host : c.Label;

    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

    /// <summary>
    /// Sends, and turns a failure into a sentence. Jira's own error text is passed on —
    /// "Field 'State' does not exist", "Issue type is required" — because it is usually the
    /// whole answer; the tools fence it as provider text.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Connection c, HttpRequestMessage request, string what, CancellationToken ct,
        bool notFoundIsNull = false)
    {
        var resp = await http.SendAsync(request, ct);
        if (resp.IsSuccessStatusCode) return resp;
        if (notFoundIsNull && resp.StatusCode == HttpStatusCode.NotFound) return resp;

        throw await FailureAsync(resp, c, what, ct);
    }

    private static async Task<JiraException> FailureAsync(
        HttpResponseMessage resp, Connection c, string what, CancellationToken ct)
    {
        var status = (int)resp.StatusCode;
        var detail = await ErrorTextAsync(resp, ct);
        resp.Dispose();

        return new JiraException(resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"{Host(c)} rejected the email and API token for {c.Label}. Replace the token on the connections page.",
            HttpStatusCode.Forbidden =>
                $"{Host(c)} refused {what}: the account behind {c.Label} doesn't have permission for it." + Tail(detail),
            HttpStatusCode.NotFound =>
                $"{Host(c)} found nothing for {what} — it doesn't exist, or {c.Label} can't see it." + Tail(detail),
            HttpStatusCode.TooManyRequests =>
                $"{Host(c)} is rate-limiting {c.Label}. Try again in a minute.",
            _ => $"{Host(c)} returned HTTP {status} for {what}." + Tail(detail),
        });
    }

    private static string Tail(string? detail) => string.IsNullOrWhiteSpace(detail) ? "" : " Jira says: " + detail;

    /// <summary><c>{"errorMessages":[…],"errors":{"field":"…"}}</c>, flattened to one line.</summary>
    private static async Task<string?> ErrorTextAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;

            using var doc = JsonDocument.Parse(body);
            var parts = new List<string>();
            if (doc.RootElement.TryGetProperty("errorMessages", out var m) && m.ValueKind == JsonValueKind.Array)
                parts.AddRange(m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            if (doc.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                parts.AddRange(e.EnumerateObject().Select(p => $"{p.Name}: {p.Value}"));

            var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
            return text.Length == 0 ? null : text.Length > 500 ? text[..500] + "…" : text;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct) where T : class, new()
    {
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(body, Json, ct) ?? new T();
    }

    private static string Key(string issueKey) => Uri.EscapeDataString(issueKey.Trim());

    // ── projects ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Connection c, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        var all = new List<ProjectSummary>();
        for (var page = 0; page < 10; page++)
        {
            using var resp = await SendAsync(http, c,
                new HttpRequestMessage(HttpMethod.Get, $"rest/api/3/project/search?startAt={all.Count}&maxResults=100&orderBy=key"),
                "the project list", ct);
            var p = await ReadAsync<ProjectPage>(resp, ct);

            all.AddRange(p.Values.Select(x => new ProjectSummary(x.Id ?? "", x.Key ?? "", x.Name ?? "")));
            if (p.IsLast != false || p.Values.Count == 0) break;
        }

        return all;
    }

    /// <summary>A Jira site has no notion of a project created by a token's owner on a whim.</summary>
    public Task<ProjectSummary> CreateProjectAsync(Connection c, string name, string shortName, CancellationToken ct = default) =>
        throw new JiraException(NoProjectCreation(c.Label));

    // ── search ──────────────────────────────────────────────────────────────

    private static readonly string[] SummaryFields = ["summary", "status", "assignee", "project"];

    public async Task<IReadOnlyList<IssueSummary>> SearchAsync(
        Connection c, string query, int top = 20, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var wanted = Math.Clamp(top, 1, 100);

        var found = new List<IssueSummary>();
        string? token = null;

        // The server may return fewer than asked for "where a large number of fields are
        // requested", so a short page is not the end; isLast and the token are.
        for (var page = 0; page < 5 && found.Count < wanted; page++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "rest/api/3/search/jql")
            {
                Content = Body(new SearchRequest(query, wanted - found.Count, SummaryFields, token)),
            };

            HttpResponseMessage resp;
            try { resp = await SendAsync(http, c, request, "that search", ct); }
            catch (JiraException ex) when (LooksLikeYouTrack(query))
            {
                throw new JiraException(ex.Message + $" That query looks like YouTrack syntax, and {c.Label} is a Jira "
                    + "connection, which reads JQL — e.g. " + IssueQueryLanguage.Example(ProviderKind.Jira) + ".");
            }

            using (resp)
            {
                var r = await ReadAsync<SearchResponse>(resp, ct);
                found.AddRange(r.Issues.Select(MapSummary));
                if (r.IsLast != false || string.IsNullOrEmpty(r.NextPageToken) || r.Issues.Count == 0) break;
                token = r.NextPageToken;
            }
        }

        return found.Take(wanted).ToList();
    }

    /// <summary>
    /// YouTrack's tells: <c>name: value</c>, <c>#Tag</c>, <c>for: me</c>. None is valid JQL,
    /// which uses <c>=</c>, <c>~</c> and functions. Only consulted once Jira has already
    /// refused the query, to say why — never to rewrite it.
    /// </summary>
    public static bool LooksLikeYouTrack(string query) => YouTrackTell().IsMatch(query ?? "");

    [GeneratedRegex(@"(^|\s)#\w|\b[A-Za-z][\w ]*:\s*\S|\bfor:\s*me\b", RegexOptions.IgnoreCase)]
    private static partial Regex YouTrackTell();

    private static IssueSummary MapSummary(JiraIssue i)
    {
        var f = i.Fields;
        return new IssueSummary(
            i.Id ?? i.Key ?? "", i.Key ?? i.Id ?? "", Str(f, "summary") ?? "(no summary)",
            Name(f, "status"), Person(f, "assignee"), ProjectKey(f) ?? "");
    }

    // ── one issue ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fields Jira returns that are either mapped to a named slot in <see cref="IssueDetail"/>,
    /// fetched separately (comments), or bookkeeping nobody asks about out loud.
    /// </summary>
    private static readonly HashSet<string> NotAField = new(StringComparer.OrdinalIgnoreCase)
    {
        "summary", "description", "status", "assignee", "reporter", "project", "created", "updated",
        "resolutiondate", "labels", "issuelinks", "subtasks", "parent", "comment", "attachment",
        "worklog", "watches", "votes", "lastViewed", "statuscategorychangedate", "statusCategory",
        "aggregateprogress", "progress", "aggregatetimespent", "aggregatetimeestimate",
        "aggregatetimeoriginalestimate", "workratio", "issuerestriction", "thumbnail", "creator",
        "timetracking", "security",
    };

    /// <summary>Shown first, in this order; everything else follows by name.</summary>
    private static readonly string[] Leading = ["issuetype", "priority", "resolution", "duedate"];

    public async Task<IssueDetail?> GetIssueAsync(Connection c, string issueKey, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        // Every field but the comments, which page separately, plus each field's display
        // name — custom fields are otherwise only "customfield_10037".
        using var resp = await SendAsync(http, c,
            new HttpRequestMessage(HttpMethod.Get, $"rest/api/3/issue/{Key(issueKey)}?fields=*all,-comment&expand=names"),
            $"issue {issueKey}", ct, notFoundIsNull: true);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;

        var i = await ReadAsync<JiraIssue>(resp, ct);
        var f = i.Fields;
        var key = i.Key ?? issueKey;

        var (comments, total) = await CommentsAsync(http, c, key, ct);

        return new IssueDetail(
            i.Id ?? key, key, Str(f, "summary") ?? "(no summary)",
            Prop(f, "description") is { } d ? AtlassianDocument.ToText(d) : null,
            Name(f, "status"), Person(f, "assignee"), ProjectKey(f) ?? "", Person(f, "reporter"),
            Moment(Str(f, "created")), Moment(Str(f, "updated")), Moment(Str(f, "resolutiondate")),
            Prop(f, "labels") is { ValueKind: JsonValueKind.Array } l
                ? l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [],
            Fields(f, i.Names),
            Links(f),
            total,
            comments,
            $"{c.Instance.TrimEnd('/')}/browse/{key}");
    }

    /// <summary>
    /// Every comment, oldest first, across the server's pages.
    /// <para>
    /// The tools number comments from the oldest and promise that #3 is the same comment on
    /// every call (OP2-263). Jira pages comments server-side, so the first page alone would
    /// make the newest comments unreachable and the overview — which shows the most recent
    /// ten — would show the oldest instead. So the pages are walked to the end, advancing by
    /// what each page actually held rather than by what was asked for.
    /// </para>
    /// </summary>
    private async Task<(List<IssueComment> Comments, int Total)> CommentsAsync(
        HttpClient http, Connection c, string key, CancellationToken ct)
    {
        var all = new List<IssueComment>();
        var total = 0;

        for (var page = 0; page < MaxCommentPages; page++)
        {
            using var resp = await SendAsync(http, c,
                new HttpRequestMessage(HttpMethod.Get,
                    $"rest/api/3/issue/{Key(key)}/comment?startAt={all.Count}&maxResults={CommentPage}&orderBy=created"),
                $"the comments on {key}", ct);
            var p = await ReadAsync<CommentPageResponse>(resp, ct);

            total = Math.Max(p.Total, all.Count + p.Comments.Count);
            all.AddRange(p.Comments.Select(x => new IssueComment(
                x.Author?.DisplayName ?? "?",
                x.Body is { } b ? AtlassianDocument.ToText(b) ?? "" : "",
                Moment(x.Created))));

            if (p.Comments.Count == 0 || all.Count >= p.Total) break;
        }

        if (total > all.Count)
            log.LogWarning("{Key} on {Label} has {Total} comments; read the first {Read}.", key, c.Label, total, all.Count);

        return (all, total);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Fields(JsonElement f, Dictionary<string, string>? names)
    {
        if (f.ValueKind != JsonValueKind.Object) return [];

        var list = new List<(int Order, string Name, string Value)>();
        foreach (var p in f.EnumerateObject())
        {
            if (NotAField.Contains(p.Name)) continue;

            var label = names is not null && names.TryGetValue(p.Name, out var n) && !string.IsNullOrWhiteSpace(n) ? n : p.Name;

            // Jira's own bookkeeping custom fields: a lexical rank and a development-panel
            // summary that is literally the string "{}" when empty.
            if (label is "Rank" or "Development") continue;

            var value = p.Name switch
            {
                "timeoriginalestimate" or "timeestimate" or "timespent" => Duration(p.Value),
                _ => Describe(p.Value),
            };
            if (string.IsNullOrWhiteSpace(value) || value == "{}") continue;

            var order = Array.IndexOf(Leading, p.Name);
            list.Add((order < 0 ? Leading.Length : order, label, value));
        }

        return list.OrderBy(x => x.Order).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new KeyValuePair<string, string>(x.Name, x.Value)).ToList();
    }

    /// <summary>
    /// A field value of whatever shape: an option (<c>value</c>), a user (<c>displayName</c>),
    /// a version or component (<c>name</c>), a rich-text document, a list of any of them,
    /// or a scalar. The same defensive reading YouTrack's custom fields needed.
    /// </summary>
    private static string? Describe(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.ToString(),
        JsonValueKind.True or JsonValueKind.False => e.GetBoolean() ? "yes" : "no",
        JsonValueKind.Array => e.EnumerateArray().Select(Describe).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() is
            { Count: > 0 } parts ? string.Join(", ", parts) : null,
        JsonValueKind.Object when AtlassianDocument.IsDocument(e) => AtlassianDocument.ToText(e),
        JsonValueKind.Object => Pick(e),
        _ => null,
    };

    private static string? Pick(JsonElement o)
    {
        foreach (var key in (string[])["displayName", "name", "value", "key"])
            if (o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            {
                // A cascading select: "Hardware → Laptop".
                var s = v.GetString()!;
                return o.TryGetProperty("child", out var child) && Pick(child) is { } sub ? $"{s} → {sub}" : s;
            }
        return null;
    }

    /// <summary>Time tracking arrives in seconds.</summary>
    private static string? Duration(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out var s) || s <= 0) return null;
        var mins = s / 60;
        return mins >= 60 ? $"{mins / 60}h {mins % 60}m" : $"{mins}m";
    }

    /// <summary>
    /// Issue links, the parent and the subtasks, worded from this issue's side: an outward
    /// link reads "blocks", the same link seen from the other end "is blocked by".
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> Links(JsonElement f)
    {
        var by = new List<(string Label, List<string> Keys)>();
        void Add(string? label, string? key)
        {
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(key)) return;
            var slot = by.FirstOrDefault(x => x.Label == label);
            if (slot.Keys is null) by.Add((label!, [key!]));
            else slot.Keys.Add(key!);
        }

        if (f.ValueKind != JsonValueKind.Object) return [];

        if (Prop(f, "parent") is { } parent) Add("parent", Str(parent, "key"));

        if (Prop(f, "subtasks") is { ValueKind: JsonValueKind.Array } subs)
            foreach (var s in subs.EnumerateArray()) Add("subtasks", Str(s, "key"));

        if (Prop(f, "issuelinks") is { ValueKind: JsonValueKind.Array } links)
            foreach (var l in links.EnumerateArray())
            {
                var type = Prop(l, "type") ?? default;
                if (Prop(l, "outwardIssue") is { ValueKind: JsonValueKind.Object } o)
                    Add(Str(type, "outward") ?? Str(type, "name"), Str(o, "key"));
                else if (Prop(l, "inwardIssue") is { ValueKind: JsonValueKind.Object } i)
                    Add(Str(type, "inward") ?? Str(type, "name"), Str(i, "key"));
            }

        return by.Select(x => new KeyValuePair<string, string>(x.Label, string.Join(", ", x.Keys))).ToList();
    }

    /// <summary>A property of an object, or null — including when the element is not an object at all.</summary>
    private static JsonElement? Prop(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v : null;

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? Name(JsonElement f, string field) => Prop(f, field) is { } v ? Str(v, "name") : null;

    private static string? Person(JsonElement f, string field) => Prop(f, field) is { } v ? Str(v, "displayName") : null;

    private static string? ProjectKey(JsonElement f) => Prop(f, "project") is { } v ? Str(v, "key") : null;

    /// <summary>
    /// Jira writes offsets without a colon — <c>2026-11-01T10:00:00.000+0000</c> — which
    /// the round-trip parser does not take, so the colon is put back first.
    /// </summary>
    public static DateTimeOffset? Moment(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var fixedUp = Offset().Replace(s.Trim(), "$1:$2");
        return DateTimeOffset.TryParse(fixedUp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t : null;
    }

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex Offset();

    // ── writes ──────────────────────────────────────────────────────────────

    public async Task<string> CreateIssueAsync(
        Connection c, string projectShortName, string summary, string? description,
        string? issueType = null, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var project = projectShortName.Trim();

        // The issue type is required, and which ones exist is per project. Asking also
        // proves the project exists, which turns Jira's 400 about a missing project into
        // the list of ones that do.
        IssueTypesResponse types;
        using (var resp = await http.GetAsync(
                   $"rest/api/3/issue/createmeta/{Uri.EscapeDataString(project)}/issuetypes?maxResults=200", ct))
        {
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                var projects = await ListProjectsAsync(c, ct);
                throw new JiraException(
                    $"No project '{project}' on {c.Label} that you can create issues in. Available: "
                    + string.Join(", ", projects.Select(p => p.ShortName)));
            }

            if (!resp.IsSuccessStatusCode) throw await FailureAsync(resp, c, $"the issue types of {project}", ct);

            types = await ReadAsync<IssueTypesResponse>(resp, ct);
        }

        var standard = types.IssueTypes.Where(t => t.Subtask != true).ToList();
        var type = issueType is { Length: > 0 }
            ? types.IssueTypes.FirstOrDefault(t => string.Equals(t.Name, issueType.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new JiraException(
                  $"{project} has no issue type '{issueType.Trim()}'. It has: {string.Join(", ", standard.Select(t => t.Name))}.")
            : standard.FirstOrDefault(t => string.Equals(t.Name, "Task", StringComparison.OrdinalIgnoreCase))
              ?? standard.FirstOrDefault()
              ?? throw new JiraException($"{project} offers no issue type you can create.");

        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = project },
            ["summary"] = summary,
            ["issuetype"] = new JsonObject { ["id"] = type.Id },
        };
        if (!string.IsNullOrWhiteSpace(description)) fields["description"] = AtlassianDocument.FromText(description);

        using var created = await SendAsync(http, c,
            new HttpRequestMessage(HttpMethod.Post, "rest/api/3/issue")
            {
                Content = Body(new JsonObject { ["fields"] = fields }),
            }, $"a new {type.Name} in {project}", ct);

        var r = await ReadAsync<CreatedIssue>(created, ct);
        log.LogInformation("Created {Key} ({Type}) in {Project} via {Label}.", r.Key, type.Name, project, c.Label);
        return r.Key ?? "(unknown)";
    }

    public async Task CommentAsync(Connection c, string issueKey, string text, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        using var _ = await SendAsync(http, c,
            new HttpRequestMessage(HttpMethod.Post, $"rest/api/3/issue/{Key(issueKey)}/comment")
            {
                Content = Body(new JsonObject { ["body"] = AtlassianDocument.FromText(text) }),
            }, $"a comment on {issueKey}", ct);
    }

    /// <summary>What <c>update_issue</c> takes on a Jira connection, said whenever a command isn't understood.</summary>
    public const string CommandHelp =
        "On Jira, update_issue takes one of: a transition or status name ('In Progress', 'Done', or 'status Done'); "
        + "'for me' or 'assignee <name>' or 'unassign'; 'priority <name>'; 'label <name>' or 'remove label <name>'.";

    /// <summary>
    /// One change per call, in a small language shaped after YouTrack's commands so that
    /// the same tool reads naturally on both: "Done", "status In Progress", "for me",
    /// "assignee Dana", "priority High", "label customer", "remove label customer".
    /// <para>
    /// Jira has no command parser of its own to lean on, as YouTrack does. A status is a
    /// workflow transition, offered only from where the issue is now, so the transitions
    /// are fetched and matched by their name or the status they lead to — and when none
    /// matches, the ones that exist are said back rather than guessed between.
    /// </para>
    /// </summary>
    public async Task ApplyCommandAsync(Connection c, string issueKey, string command, CancellationToken ct = default)
    {
        var cmd = Regex.Replace((command ?? "").Trim(), @"\s+", " ");
        if (cmd.Length == 0) throw new JiraException("Say what to change. " + CommandHelp);

        using var http = await ClientAsync(c, ct);

        string? Rest(params string[] prefixes)
        {
            foreach (var p in prefixes)
                if (cmd.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                    return cmd[(p.Length + 1)..].Trim();
            return null;
        }

        if (Is(cmd, "for me", "assign to me", "assignee me", "assign me", "take it"))
        {
            await AssignAsync(http, c, issueKey, c.HomeAccountId ?? await MyAccountIdAsync(http, c, ct), ct);
            return;
        }

        if (Is(cmd, "unassign", "unassigned", "assignee unassigned", "assignee none", "assignee nobody"))
        {
            await AssignAsync(http, c, issueKey, null, ct);
            return;
        }

        if (Rest("assignee", "assign to", "for") is { Length: > 0 } who)
        {
            await AssignAsync(http, c, issueKey, await AssignableAsync(http, c, issueKey, who, ct), ct);
            return;
        }

        if (Rest("priority") is { Length: > 0 } priority)
        {
            await EditAsync(http, c, issueKey,
                new JsonObject { ["fields"] = new JsonObject { ["priority"] = new JsonObject { ["name"] = priority } } },
                $"the priority of {issueKey}", ct);
            return;
        }

        if (Rest("remove label", "untag", "remove tag") is { Length: > 0 } drop)
        {
            await EditAsync(http, c, issueKey, Labels("remove", drop), $"the labels on {issueKey}", ct);
            return;
        }

        if (Rest("label", "add label", "tag") is { Length: > 0 } add)
        {
            await EditAsync(http, c, issueKey, Labels("add", add), $"the labels on {issueKey}", ct);
            return;
        }

        await TransitionAsync(http, c, issueKey, Rest("status", "state", "transition", "move to") ?? cmd, ct);
    }

    private static bool Is(string cmd, params string[] forms) =>
        forms.Any(f => string.Equals(cmd, f, StringComparison.OrdinalIgnoreCase));

    private static JsonObject Labels(string op, string label)
    {
        // Jira labels cannot contain spaces; it would refuse the whole edit. Said here,
        // where the reason is known, rather than as Jira's generic 400.
        if (label.Contains(' '))
            throw new JiraException($"Jira labels can't contain spaces, so '{label}' can't be one. Try '{label.Replace(' ', '-')}'.");
        return new JsonObject
        {
            ["update"] = new JsonObject
            {
                ["labels"] = new JsonArray(new JsonObject { [op] = label }),
            },
        };
    }

    private async Task EditAsync(HttpClient http, Connection c, string issueKey, JsonObject body, string what, CancellationToken ct)
    {
        using var _ = await SendAsync(http, c,
            new HttpRequestMessage(HttpMethod.Put, $"rest/api/3/issue/{Key(issueKey)}") { Content = Body(body) }, what, ct);
    }

    private async Task AssignAsync(HttpClient http, Connection c, string issueKey, string? accountId, CancellationToken ct)
    {
        // accountId null is Jira's "unassigned"; it has to be sent, not omitted.
        var body = new JsonObject { ["accountId"] = accountId is null ? null : JsonValue.Create(accountId) };
        using var _ = await SendAsync(http, c,
            new HttpRequestMessage(HttpMethod.Put, $"rest/api/3/issue/{Key(issueKey)}/assignee")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            }, $"the assignee of {issueKey}", ct);
    }

    private async Task<string> MyAccountIdAsync(HttpClient http, Connection c, CancellationToken ct)
    {
        using var resp = await SendAsync(http, c, new HttpRequestMessage(HttpMethod.Get, "rest/api/3/myself"), "your account", ct);
        return (await ReadAsync<JiraUser>(resp, ct)).AccountId
               ?? throw new JiraException($"{Host(c)} didn't say which account {c.Label} is.");
    }

    /// <summary>
    /// A person named out loud, among those who may be assigned this issue. One match is
    /// taken; several are said back by name, because assigning the wrong Dana is seen by both.
    /// </summary>
    private async Task<string> AssignableAsync(HttpClient http, Connection c, string issueKey, string who, CancellationToken ct)
    {
        using var resp = await SendAsync(http, c,
            new HttpRequestMessage(HttpMethod.Get,
                $"rest/api/3/user/assignable/search?issueKey={Key(issueKey)}&query={Uri.EscapeDataString(who)}&maxResults=10"),
            $"people who can take {issueKey}", ct);
        var people = (await ReadAsync<List<JiraUser>>(resp, ct)).Where(p => !string.IsNullOrEmpty(p.AccountId)).ToList();

        var exact = people.Where(p => string.Equals(p.DisplayName, who, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0].AccountId!;

        return people.Count switch
        {
            1 => people[0].AccountId!,
            0 => throw new JiraException($"Nobody called '{who}' can be assigned {issueKey}."),
            _ => throw new JiraException(
                $"Several people match '{who}': {string.Join(", ", people.Select(p => p.DisplayName))}. Say which one."),
        };
    }

    private async Task TransitionAsync(HttpClient http, Connection c, string issueKey, string target, CancellationToken ct)
    {
        TransitionsResponse t;
        using (var resp = await SendAsync(http, c,
                   new HttpRequestMessage(HttpMethod.Get, $"rest/api/3/issue/{Key(issueKey)}/transitions"),
                   $"the transitions of {issueKey}", ct))
            t = await ReadAsync<TransitionsResponse>(resp, ct);

        // The transition's own name first ("Start progress"), then the status it leads to
        // ("In Progress"), which is what people usually say.
        var pick = t.Transitions.FirstOrDefault(x => string.Equals(x.Name, target, StringComparison.OrdinalIgnoreCase))
                   ?? t.Transitions.FirstOrDefault(x => string.Equals(x.To?.Name, target, StringComparison.OrdinalIgnoreCase));

        if (pick?.Id is null)
        {
            var offered = t.Transitions
                .Select(x => x.To?.Name is { } to && !string.Equals(to, x.Name, StringComparison.OrdinalIgnoreCase)
                    ? $"{x.Name} (to {to})" : x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

            throw new JiraException(offered.Count == 0
                ? $"Jira offers no transitions for {issueKey} from where it is now, so it can't move to '{target}'."
                : $"Jira can't move {issueKey} to '{target}' from where it is now. It can do: {string.Join(", ", offered)}. "
                  + CommandHelp);
        }

        using var _ = await SendAsync(http, c,
            new HttpRequestMessage(HttpMethod.Post, $"rest/api/3/issue/{Key(issueKey)}/transitions")
            {
                Content = Body(new JsonObject { ["transition"] = new JsonObject { ["id"] = pick.Id } }),
            }, $"moving {issueKey} to {pick.To?.Name ?? pick.Name}", ct);

        log.LogInformation("Moved {Key} via '{Transition}' on {Label}.", issueKey, pick.Name, c.Label);
    }

    // ── wire types ──────────────────────────────────────────────────────────

    private sealed record SearchRequest(
        [property: JsonPropertyName("jql")] string Jql,
        [property: JsonPropertyName("maxResults")] int MaxResults,
        [property: JsonPropertyName("fields")] string[] Fields,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

    private sealed class SearchResponse
    {
        [JsonPropertyName("issues")] public List<JiraIssue> Issues { get; init; } = [];
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; init; }
        [JsonPropertyName("isLast")] public bool? IsLast { get; init; }
    }

    private sealed class JiraIssue
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("key")] public string? Key { get; init; }

        /// <summary>Raw: a field's shape depends on its type, and custom fields are anything.</summary>
        [JsonPropertyName("fields")] public JsonElement Fields { get; init; }

        [JsonPropertyName("names")] public Dictionary<string, string>? Names { get; init; }
    }

    private sealed class ProjectPage
    {
        [JsonPropertyName("values")] public List<JiraProject> Values { get; init; } = [];
        [JsonPropertyName("isLast")] public bool? IsLast { get; init; }
    }

    private sealed record JiraProject(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("name")] string? Name);

    private sealed class CommentPageResponse
    {
        [JsonPropertyName("total")] public int Total { get; init; }
        [JsonPropertyName("comments")] public List<JiraComment> Comments { get; init; } = [];
    }

    private sealed record JiraComment(
        [property: JsonPropertyName("author")] JiraUser? Author,
        [property: JsonPropertyName("body")] JsonElement? Body,
        [property: JsonPropertyName("created")] string? Created);

    private sealed class JiraUser
    {
        [JsonPropertyName("accountId")] public string? AccountId { get; init; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
    }

    private sealed class IssueTypesResponse
    {
        [JsonPropertyName("issueTypes")] public List<IssueType> IssueTypes { get; init; } = [];
    }

    private sealed record IssueType(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("subtask")] bool? Subtask);

    private sealed class CreatedIssue
    {
        [JsonPropertyName("key")] public string? Key { get; init; }
    }

    private sealed class TransitionsResponse
    {
        [JsonPropertyName("transitions")] public List<Transition> Transitions { get; init; } = [];
    }

    private sealed record Transition(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("to")] Status? To);

    private sealed record Status([property: JsonPropertyName("name")] string? Name);
}
