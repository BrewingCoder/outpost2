using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OutPost2.Infrastructure.Feedback;

public enum FrictionKind
{
    Bug,
    MissingFeature,
    AwkwardDesign,
}

/// <summary>What the model hit, in the model's own words.</summary>
public sealed record FrictionReport
{
    /// <summary>The tool involved, where there is one. Null for "the whole thing is awkward".</summary>
    public string? Tool { get; init; }

    /// <summary>What it was trying to do.</summary>
    public required string Doing { get; init; }

    /// <summary>What went wrong, or what would work better.</summary>
    public required string Problem { get; init; }

    public FrictionKind Kind { get; init; } = FrictionKind.AwkwardDesign;
}

/// <summary>Where the report ended up, phrased so it can be read back out loud.</summary>
public sealed record FrictionOutcome(bool Filed, string? Key, string Spoken);

public interface IFrictionService
{
    bool IsConfigured { get; }

    Task<FrictionOutcome> ReportAsync(FrictionReport report, string reporter, CancellationToken ct = default);
}

/// <summary>
/// Files friction reports into OutPost2's own tracker (OP2-74).
/// <para>
/// Talks to YouTrack directly rather than through <c>YouTrackService</c>, which is built
/// around a user's <c>Connection</c> — decrypting that user's credential, touching their
/// row. None of that applies here: the credential is the server's, the destination is
/// fixed, and there is no connection to touch.
/// </para>
/// </summary>
public sealed partial class FrictionService(
    IHttpClientFactory httpFactory,
    IOptions<FeedbackOptions> options,
    ILogger<FrictionService> log) : IFrictionService
{
    private readonly FeedbackOptions _o = options.Value;

    public bool IsConfigured => _o.IsConfigured;

    private HttpClient Client()
    {
        var http = httpFactory.CreateClient("youtrack");
        http.BaseAddress = new Uri(_o.Url.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _o.Token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Cloudflare 1010-blocks unfamiliar agents; the default .NET one is refused.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2/1.0");
        return http;
    }

    public async Task<FrictionOutcome> ReportAsync(
        FrictionReport report, string reporter, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new(false, null, "Friction reporting isn't configured on this server yet.");

        var doing = Scrub(Clamp(report.Doing, 1200));
        var problem = Scrub(Clamp(report.Problem, 2400));

        if (problem.Length == 0)
            return new(false, null, "I need to know what actually went wrong before I can file it.");

        var tool = Sanitise(report.Tool);
        var summary = Summarise(tool, problem);

        using var http = Client();

        // Dedup before creating. Several people hitting the same rough edge is the signal
        // worth having; six issues saying the same thing is noise that buries it.
        var existing = await OpenReportsAsync(http, ct);
        var twin = existing.FirstOrDefault(i => Similarity(i.Summary, summary) >= 0.6);

        var body = Describe(report.Kind, tool, doing, problem, reporter);

        if (twin is not null)
        {
            using var resp = await http.PostAsJsonAsync(
                $"api/issues/{Uri.EscapeDataString(twin.Key)}/comments",
                new { text = $"Hit again.\n\n{body}" }, ct);

            if (!resp.IsSuccessStatusCode)
                return new(false, null, await FailureAsync(resp, "comment on " + twin.Key, ct));

            log.LogInformation("Friction from {Reporter} added to existing {Key}.", reporter, twin.Key);
            return new(true, twin.Key, $"Added to {twin.Key}, which already covers this: \"{twin.Summary}\".");
        }

        var projectId = await ProjectIdAsync(http, ct);
        if (projectId is null)
            return new(false, null, $"The feedback tracker has no project called {_o.Project}.");

        using var created = await http.PostAsJsonAsync("api/issues?fields=idReadable", new
        {
            project = new { id = projectId },
            summary,
            description = body,
        }, ct);

        if (!created.IsSuccessStatusCode)
            return new(false, null, await FailureAsync(created, "file the report", ct));

        var key = (await created.Content.ReadFromJsonAsync<YtIssue>(ct))?.IdReadable;
        if (key is null) return new(false, null, "The tracker accepted it but did not say which issue it created.");

        // Best effort, and deliberately not fatal: an untagged issue is still a filed
        // issue, and failing the whole report because a tag did not stick would throw away
        // feedback that is already in the tracker.
        await TagAsync(http, key, ct);

        log.LogInformation("Friction from {Reporter} filed as {Key}.", reporter, key);
        return new(true, key, $"Filed as {key}: \"{summary}\".");
    }

    private async Task TagAsync(HttpClient http, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.Tag)) return;

        try
        {
            using var resp = await http.PostAsJsonAsync("api/commands", new
            {
                query = $"add tag {_o.Tag}",
                issues = new[] { new { idReadable = key } },
            }, ct);

            if (!resp.IsSuccessStatusCode)
                log.LogWarning("Could not tag {Key} with {Tag}: HTTP {Status}.",
                    key, _o.Tag, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not tag {Key} with {Tag}.", key, _o.Tag);
        }
    }

    /// <summary>An open report already in the tracker, as a dedup candidate.</summary>
    private sealed record OpenReport(string Key, string Summary);

    private async Task<IReadOnlyList<OpenReport>> OpenReportsAsync(
        HttpClient http, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(_o.Tag)
            ? $"project: {_o.Project} #Unresolved"
            : $"project: {_o.Project} tag: {_o.Tag} #Unresolved";

        try
        {
            var found = await http.GetFromJsonAsync<List<YtIssue>>(
                $"api/issues?query={Uri.EscapeDataString(query)}&$top=100&fields=idReadable,summary", ct);

            return (found ?? [])
                .Where(i => i.IdReadable is not null && i.Summary is not null)
                .Select(i => new OpenReport(i.IdReadable!, i.Summary!))
                .ToList();
        }
        catch (Exception ex)
        {
            // A dedup lookup that fails must not lose the report. Filing a possible
            // duplicate is a far smaller cost than dropping feedback on the floor.
            log.LogWarning(ex, "Could not search for existing friction reports; filing without dedup.");
            return [];
        }
    }

    private async Task<string?> ProjectIdAsync(HttpClient http, CancellationToken ct)
    {
        var projects = await http.GetFromJsonAsync<List<YtProject>>(
            "api/admin/projects?fields=id,shortName&$top=200", ct);

        return projects?.FirstOrDefault(p =>
            string.Equals(p.ShortName, _o.Project, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static async Task<string> FailureAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        return $"Couldn't {what}: HTTP {(int)resp.StatusCode}. {Clamp(body, 200)}";
    }

    // ── shaping ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A one-line summary built from the problem, because the model writes a paragraph and
    /// a tracker needs a title. Prefixed with the tool so the list groups by where the
    /// friction is.
    /// </summary>
    public static string Summarise(string? tool, string problem)
    {
        var line = problem.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? problem;
        line = Whitespace().Replace(line, " ").Trim();

        const int Room = 110;
        if (line.Length > Room)
        {
            var cut = line.LastIndexOf(' ', Math.Min(Room, line.Length - 1));
            line = (cut > Room / 2 ? line[..cut] : line[..Room]).TrimEnd() + "…";
        }

        return string.IsNullOrWhiteSpace(tool) ? line : $"{tool}: {line}";
    }

    public static string Describe(
        FrictionKind kind, string? tool, string doing, string problem, string reporter)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Type:** {Wording(kind)}  ");
        sb.AppendLine($"**Tool:** {(string.IsNullOrWhiteSpace(tool) ? "_not tool-specific_" : $"`{tool}`")}  ");

        // Who hit it, always. Several people reporting the same thing is the difference
        // between a quirk and a priority, and that cannot be recovered later.
        sb.AppendLine($"**Reported by:** {reporter}  ");
        sb.AppendLine($"**When:** {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine("**What I was trying to do**");
        sb.AppendLine();
        sb.AppendLine(doing.Length == 0 ? "_not stated_" : doing);
        sb.AppendLine();
        sb.AppendLine("**What went wrong, or what would work better**");
        sb.AppendLine();
        sb.AppendLine(problem);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("_Filed automatically by Outpost's `report_friction` tool. " +
                      "Addresses and tokens are redacted on the way in._");
        return sb.ToString();
    }

    private static string Wording(FrictionKind kind) => kind switch
    {
        FrictionKind.Bug => "bug",
        FrictionKind.MissingFeature => "missing feature",
        _ => "awkward design",
    };

    /// <summary>
    /// Removes the things a report should never carry out of someone's mailbox.
    /// <para>
    /// The tool's description tells the model not to paste message contents, and mostly it
    /// will not. But "mostly" is not a privacy control: these reports leave the reporter's
    /// tenant and land in a tracker other people read, so the addresses and credentials
    /// that leak most easily are taken out here rather than trusted away.
    /// </para>
    /// </summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        text = Addresses().Replace(text, "[address]");
        text = Secrets().Replace(text, "[redacted]");
        return text.Trim();
    }

    /// <summary>A tool name is an identifier; anything else is someone talking.</summary>
    private static string? Sanitise(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return null;
        var clean = new string(tool.Trim().Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.').ToArray());
        return clean.Length is 0 or > 60 ? null : clean;
    }

    private static string Clamp(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    /// <summary>
    /// Dice coefficient over meaningful words. Chosen over substring matching because two
    /// reports of the same problem rarely share a phrase — "can't tell which mailbox a
    /// result came from" and "results don't say what mailbox they're in" overlap on words
    /// and on nothing else.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        var x = Words(a);
        var y = Words(b);
        if (x.Count == 0 || y.Count == 0) return 0;

        var shared = x.Intersect(y).Count();

        // Two three-word summaries sharing "mail" and "the" are not the same report. A
        // floor on the absolute overlap stops short titles colliding with everything.
        if (shared < 3) return 0;

        return 2.0 * shared / (x.Count + y.Count);
    }

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "to", "of", "in", "on", "for", "with", "is",
        "it", "its", "this", "that", "when", "there", "was", "were", "be", "been", "as",
        "at", "by", "from", "has", "have", "had", "not", "no", "so", "if", "than", "then",
        "i", "we", "you", "my", "me", "would", "could", "can", "cant", "doesnt", "didnt",
    };

    private static HashSet<string> Words(string s) =>
        NonWord().Split(s.ToLowerInvariant())
            .Where(w => w.Length > 2 && !Noise.Contains(w))
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Addresses();

    // Bearer tokens, PATs and base64 blobs. Long unbroken runs of token characters are
    // never prose, and a message id pasted in by mistake is no loss either.
    //
    // No trailing \b: a YouTrack permanent token and a base64 blob both end in '=', which
    // is a non-word character, so a word boundary after it never matches and the token
    // sails through unredacted. The leading boundary is the one that earns its place.
    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{16,}"
        + @"|perm[:-][A-Za-z0-9+/=._:-]{16,}"
        + @"|eyJ[\w-]{10,}\.[\w-]{10,}\.[\w-]{10,}"
        + @"|[A-Za-z0-9+/=_-]{40,})")]
    private static partial Regex Secrets();

    // ── wire types ───────────────────────────────────────────────────────────

    private sealed record YtIssue(
        [property: JsonPropertyName("idReadable")] string? IdReadable,
        [property: JsonPropertyName("summary")] string? Summary);

    private sealed record YtProject(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("shortName")] string? ShortName);
}
