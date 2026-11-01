using System.Net.Http.Headers;
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
/// What paging Graph will actually allow on a <c>$search</c> over messages (OP2-208).
/// <para>
/// OP2-208 asks for "a page token (or offset/before-date)". Which of those is buildable is
/// not a design preference, it is whatever Graph permits — and the combinations that are
/// refused are refused with a 400 that reads like a product bug. This probe asks the live
/// service all four questions and prints the answers, so the decision is recorded against
/// evidence rather than recollection.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveSearchPagingProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private const string Select =
        "$select=id,subject,receivedDateTime,parentFolderId";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Says_which_of_offset_filter_and_next_link_graph_accepts()
    {
        HttpClient http;
        OutPostDbContext db;
        MailService mail;
        Connection connection;

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

            await LiveEnvironment.RequireSchemaAsync(db);

            var found = await db.Connections
                .FirstOrDefaultAsync(c => c.Provider == ProviderKind.Microsoft);
            Skip.If(found is null, "no Microsoft connection stored");
            connection = found!;

            var token = await ms.GetAccessTokenAsync(connection);
            Skip.If(token is null, "no access token for the stored connection");

            var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            _disposables.Add(sp);

            var factory = sp.GetRequiredService<IHttpClientFactory>();

            http = factory.CreateClient();
            http.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _disposables.Add(http);

            mail = new MailService(ms, factory, NullLogger<MailService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        // 1. The baseline: does a plain search hand back a nextLink at all?
        var (status, count, next, error) = await Ask(http, $"me/messages?$search=%22the%22&$top=25&{Select}");
        output.WriteLine($"1. $search + $top=25            -> {status}  items={count}  nextLink={(next is null ? "NO" : "yes")}");
        if (error is not null) output.WriteLine($"   error: {error}");

        // 2. Offset paging, which is what OP2-208 asks for first.
        var skip = await Ask(http, $"me/messages?$search=%22the%22&$top=25&$skip=25&{Select}");
        output.WriteLine($"2. $search + $skip=25           -> {skip.Status}  items={skip.Count}");
        if (skip.Error is not null) output.WriteLine($"   error: {skip.Error}");

        // 3. A date cut-off, the ticket's fallback suggestion.
        var filter = await Ask(http,
            "me/messages?$search=%22the%22&$top=25&$filter=receivedDateTime%20lt%202026-06-01T00:00:00Z&" + Select);
        output.WriteLine($"3. $search + $filter(date)      -> {filter.Status}  items={filter.Count}");
        if (filter.Error is not null) output.WriteLine($"   error: {filter.Error}");

        // 4. Following the nextLink, which is the mechanism the service does offer.
        if (next is not null)
        {
            var page2 = await Ask(http, next);
            output.WriteLine($"4. follow @odata.nextLink       -> {page2.Status}  items={page2.Count}  nextLink={(page2.Next is null ? "NO" : "yes")}");
            if (page2.Error is not null) output.WriteLine($"   error: {page2.Error}");
            output.WriteLine($"   token length: {next.Length} chars");
        }
        else
        {
            output.WriteLine("4. follow @odata.nextLink       -> no link to follow");
        }

        // 5. And whether a scoped search pages the same way, since that is the shape a
        //    clean-up actually uses.
        var scoped = await Ask(http, $"me/mailFolders/inbox/messages?$search=%22the%22&$top=25&{Select}");
        output.WriteLine($"5. inbox-scoped $search         -> {scoped.Status}  items={scoped.Count}  nextLink={(scoped.Next is null ? "NO" : "yes")}");
        if (scoped.Error is not null) output.WriteLine($"   error: {scoped.Error}");

        // The one thing this probe asserts rather than reports: paging has to be possible
        // somehow, or OP2-208 cannot be built at all and has to be answered instead.
        Assert.True(next is not null || skip.Status == 200,
            "Graph offered neither a nextLink nor $skip on a search — OP2-208 would need a different answer entirely");

        // And the end of it: our own SearchAsync, resumed from its own continuation, must
        // return DIFFERENT mail. Paging that quietly repeats page one is the failure
        // OP2-208 reported about re-running a search, with a token on top to make it look
        // like progress.
        Skip.If(next is null, "no second page to carry on to");

        var first = await mail.SearchAsync(connection, "the", top: 10);
        Skip.If(first.Continuation is null, "the first page was the whole answer");

        var second = await mail.SearchAsync(
            connection, "the", top: 10, continueFrom: first.Continuation);

        output.WriteLine("");
        output.WriteLine($"6. SearchAsync page 1 -> {first.Items.Count}, page 2 -> {second.Items.Count}");

        Assert.NotEmpty(second.Items);

        var overlap = first.Items.Select(m => m.Id).Intersect(second.Items.Select(m => m.Id)).Count();
        output.WriteLine($"   overlap between the two pages: {overlap} of {first.Items.Count}");

        Assert.Equal(0, overlap);
    }

    private static async Task<(int Status, int Count, string? Next, string? Error)> Ask(
        HttpClient http, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("ConsistencyLevel", "eventual");

        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            return ((int)resp.StatusCode, 0, null, Message(body));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return ((int)resp.StatusCode,
            root.TryGetProperty("value", out var v) ? v.GetArrayLength() : 0,
            root.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null,
            null);
    }

    private static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e)
                   && e.TryGetProperty("message", out var m)
                ? m.GetString() ?? body
                : body;
        }
        catch { return body.Length > 300 ? body[..300] : body; }
    }
}
