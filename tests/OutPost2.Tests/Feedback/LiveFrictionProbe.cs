using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Infrastructure.Feedback;
using Xunit.Abstractions;

namespace OutPost2.Tests.Feedback;

/// <summary>
/// Files a real friction report into the real tracker, then cleans up after itself
/// (OP2-74).
/// <para>
/// The parts that can only break live are the ones worth proving: resolving the project
/// short name to the internal id YouTrack actually wants, the tag command, and the dedup
/// round trip. A fake HTTP handler would agree with whatever this code believes.
/// </para>
/// <para>
/// Both issues are deleted in a finally. A probe that leaves debris in a shared project is
/// a probe nobody runs twice.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveFrictionProbe(ITestOutputHelper output) : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task A_report_is_filed_tagged_and_then_deduplicated()
    {
        var (url, token) = Credentials();
        Skip.If(url is null, "no lab YouTrack credentials on this machine");

        var options = new FeedbackOptions
        {
            Url = url!,
            Token = token!,
            Project = "OP2",
            Tag = "friction",
        };

        var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        _disposables.Add(sp);
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        var service = new FrictionService(
            factory, Options.Create(options), NullLogger<FrictionService>.Instance);

        Assert.True(service.IsConfigured);

        // Unique enough that it cannot collide with a real report, and recognisable enough
        // that anything left behind by a crashed run is obviously debris.
        var nonce = Guid.NewGuid().ToString("n")[..8];
        var problem =
            $"probe {nonce}: the widget frobnicator refuses every spangle request and " +
            "reports no reason at all, so there is nothing to act on";

        string? key = null;
        string? second = null;

        try
        {
            var filed = await service.ReportAsync(new FrictionReport
            {
                Tool = "probe_tool",
                Doing = "checking that friction reports reach the tracker",
                Problem = problem,
                Kind = FrictionKind.Bug,
            }, "live-probe");

            output.WriteLine($"filed: {filed.Spoken}");
            Assert.True(filed.Filed, filed.Spoken);
            key = filed.Key;
            Assert.NotNull(key);

            // What landed, read back from the server rather than from what we sent.
            var issue = await GetAsync(options, key!, "idReadable,summary,description,tags(name)");
            var root = issue.RootElement;

            Assert.StartsWith("probe_tool:", root.GetProperty("summary").GetString());
            var description = root.GetProperty("description").GetString() ?? "";
            Assert.Contains("live-probe", description);
            Assert.Contains("bug", description);
            Assert.Contains("widget frobnicator", description);

            var tags = root.GetProperty("tags").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()).ToList();
            output.WriteLine($"  tags: {string.Join(", ", tags)}");
            Assert.Contains("friction", tags);

            // The same complaint, said differently. It must comment on the first issue
            // rather than filing a second — this is the behaviour that keeps the tracker
            // usable once more than one person is reporting.
            var again = await service.ReportAsync(new FrictionReport
            {
                Tool = "probe_tool",
                Doing = "checking deduplication",
                Problem =
                    $"probe {nonce}: every spangle request the widget frobnicator refuses " +
                    "reports no reason, leaving nothing to act on",
                Kind = FrictionKind.Bug,
            }, "live-probe-two");

            output.WriteLine($"again: {again.Spoken}");
            Assert.True(again.Filed, again.Spoken);
            second = again.Key;

            Assert.Equal(key, second);
            second = null;

            var commented = await GetAsync(options, key!, "comments(text)");
            var comments = commented.RootElement.GetProperty("comments").EnumerateArray()
                .Select(c => c.GetProperty("text").GetString() ?? "").ToList();

            Assert.Contains(comments, c => c.Contains("live-probe-two"));
            Assert.Contains(comments, c => c.StartsWith("Hit again."));
        }
        finally
        {
            foreach (var doomed in new[] { key, second }.Where(x => x is not null))
                await DeleteAsync(options, doomed!, output);
        }
    }

    private static (string? Url, string? Token) Credentials()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "credentials.json");

        if (!File.Exists(path)) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return (doc.RootElement.GetProperty("lab_youtrack_url").GetString(),
                    doc.RootElement.GetProperty("lab_youtrack_token").GetString());
        }
        catch { return (null, null); }
    }

    private static HttpClient Client(FeedbackOptions o)
    {
        var http = new HttpClient { BaseAddress = new Uri(o.Url.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.Token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2-tests/1.0");
        return http;
    }

    private static async Task<JsonDocument> GetAsync(FeedbackOptions o, string key, string fields)
    {
        using var http = Client(o);
        var body = await http.GetStringAsync(
            $"api/issues/{Uri.EscapeDataString(key)}?fields={Uri.EscapeDataString(fields)}");
        return JsonDocument.Parse(body);
    }

    private static async Task DeleteAsync(FeedbackOptions o, string key, ITestOutputHelper output)
    {
        try
        {
            using var http = Client(o);
            using var resp = await http.DeleteAsync($"api/issues/{Uri.EscapeDataString(key)}");
            output.WriteLine($"cleanup {key}: {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            output.WriteLine($"cleanup {key} FAILED — delete it by hand: {ex.Message}");
        }
    }
}
