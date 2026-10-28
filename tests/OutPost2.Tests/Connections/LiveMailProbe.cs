using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Mail;
using OutPost2.Infrastructure.Security;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Exercises the real mail path against the real mailbox: list, read, attachments.
/// <para>
/// Strictly read-only. The unit tests prove HtmlToText and AttachmentExtractor handle the
/// shapes we fed them; this proves they handle the shapes Microsoft actually sends, which
/// is a different claim and the one that matters — HTML mail is precisely what the
/// predecessor got wrong.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveMailProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Reads_real_messages_including_html_and_attachments()
    {
        OutPostDbContext db;
        MailService mail;

        try
        {
            db = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
            _disposables.Add(db);

            var vault = new VaultTransitClient(Options.Create(new VaultOptions
            {
                Address = "http://localhost:8200", AuthMethod = VaultAuthMethod.Token,
                Token = "op2-dev-root", TransitMount = "op2-transit", KeyName = "op2-kek",
            }), NullLogger<VaultTransitClient>.Instance);
            _disposables.Add(vault);

            Skip.IfNot(await vault.HealthyAsync(), "dev Vault unreachable");

            var creds = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "credentials.json")));

            var store = new ConnectionStore(db, new CredentialProtector(vault));
            var ms = new MicrosoftConnectionService(Options.Create(new EntraOptions
            {
                ClientId = creds.RootElement.GetProperty("outpost2_entra_client_id").GetString()!,
                ClientSecret = creds.RootElement.GetProperty("outpost2_entra_client_secret").GetString()!,
            }), store, NullLogger<MicrosoftConnectionService>.Instance);

            var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            _disposables.Add(sp);

            mail = new MailService(ms, sp.GetRequiredService<IHttpClientFactory>(),
                NullLogger<MailService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        // Reachable-but-unmigrated Postgres is a skip, not a failure (see LiveEnvironment).
        await LiveEnvironment.RequireSchemaAsync(db);

        var connection = await db.Connections
            .FirstOrDefaultAsync(c => c.Provider == ProviderKind.Microsoft);
        Skip.If(connection is null, "no Microsoft connection stored");

        var list = await mail.ListAsync(connection!, top: 10);
        output.WriteLine($"inbox: {list.Count} message(s) from '{connection!.Label}'");
        Assert.NotEmpty(list);

        foreach (var m in list)
            output.WriteLine($"  [{(m.IsRead ? ' ' : '*')}]{(m.HasAttachments ? " @" : "  ")} " +
                             $"{m.ReceivedAt:yyyy-MM-dd HH:mm}  {m.From,-34}  {Trim(m.Subject, 54)}");

        // Every summary must be able to say which mailbox it came from. A fan-out result
        // without that is worse than no fan-out.
        Assert.All(list, m => Assert.Equal(connection.Label, m.ConnectionLabel));

        // --- read the newest message in full -------------------------------------------
        var full = await mail.ReadAsync(connection, list[0].Id);
        Assert.NotNull(full);
        output.WriteLine("");
        output.WriteLine($"=== read '{Trim(full!.Subject, 60)}' ===");
        output.WriteLine($"  from {full.From}  to [{string.Join(", ", full.To)}]");
        output.WriteLine($"  quoted history stripped: {full.QuotedHistoryStripped}");
        output.WriteLine($"  body {full.Body.Length} chars:");
        output.WriteLine(Indent(Trim(full.Body, 1200)));

        // The predecessor's failure mode was HTML arriving as markup or as nothing at all.
        Assert.False(string.IsNullOrWhiteSpace(full.Body), "body came back empty");
        Assert.DoesNotContain("<div", full.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<table", full.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("&nbsp;", full.Body, StringComparison.OrdinalIgnoreCase);

        // --- search ----------------------------------------------------------------------
        // $search is a different Graph code path from $filter, with its own escaping and
        // its own refusal to combine with $orderby. Worth proving against the live service.
        var hits = await mail.SearchAsync(connection, "invoice", top: 5);
        output.WriteLine("");
        output.WriteLine($"=== search \"invoice\" -> {hits.Count} hit(s) ===");
        foreach (var h in hits)
            output.WriteLine($"  {h.ReceivedAt:yyyy-MM-dd}  {h.From,-34}  {Trim(h.Subject, 54)}");
        Assert.All(hits, h => Assert.Equal(connection.Label, h.ConnectionLabel));

        // --- attachments ----------------------------------------------------------------
        // Scan deeper than the display list: attachment handling is a headline feature and
        // whether the ten newest messages happen to carry one is pure chance.
        var deep = await mail.ListAsync(connection, top: 100);
        var withAttachments = deep.FirstOrDefault(m => m.HasAttachments);
        if (withAttachments is null)
        {
            output.WriteLine("");
            output.WriteLine($"no message in the newest {deep.Count} has attachments — path not exercised");
            return;
        }

        var attachments = await mail.ListAttachmentsAsync(connection, withAttachments.Id);
        output.WriteLine("");
        output.WriteLine($"=== attachments on '{Trim(withAttachments.Subject, 50)}' ===");
        foreach (var a in attachments)
            output.WriteLine($"  {a.Name,-44} {a.HumanSize,9}  {a.ContentType}" +
                             $"{(a.IsLink ? "  [link]" : "")}{(a.IsEmbeddedItem ? "  [embedded]" : "")}");

        var downloadable = attachments.FirstOrDefault(a => !a.IsLink && !a.IsEmbeddedItem);
        if (downloadable is null)
        {
            output.WriteLine("  (all are links or embedded items — nothing with bytes to fetch)");
            return;
        }

        await using var stream = await mail.OpenAttachmentAsync(
            connection, withAttachments.Id, downloadable.Id);
        Assert.NotNull(stream);

        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer);
        output.WriteLine($"  downloaded '{downloadable.Name}': {buffer.Length} bytes " +
                         $"(Graph reported {downloadable.SizeBytes})");

        // Graph's reported size includes base64/MIME overhead, so it is an upper bound on
        // the decoded bytes rather than a match. Zero bytes, though, is a real failure.
        Assert.True(buffer.Length > 0, "attachment downloaded as zero bytes");
    }

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    private static string Indent(string s) =>
        string.Join("\n", s.Replace("\r", "").Split('\n').Select(l => "    " + l));
}
