using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Code;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Tests.Code;

/// <summary>
/// Reading a repository's files (OP2-168).
/// <para>
/// Driven against a stubbed transport rather than GitHub, because what is worth proving
/// here is the handling of the awkward shapes — a directory and a file are told apart
/// only by whether the JSON is an array, a file over a megabyte arrives with its content
/// field empty, and a binary blob decodes to plausible-looking nonsense. Each of those
/// produces a wrong answer that reads like a right one.
/// </para>
/// </summary>
public class GitHubContentsTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            // AbsoluteUri, not ToString(): ToString() un-escapes the path, so a test
            // reading it cannot tell a correctly escaped space from a raw one.
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Stores nothing and seals nothing: these tests are about HTTP, not crypto.</summary>
    private sealed class PlainProtector : ICredentialProtector
    {
        public Task<ProtectedCredential> ProtectAsync(string p, CancellationToken ct = default) =>
            Task.FromResult(new ProtectedCredential
            {
                WrappedKey = "test", Nonce = "", Tag = "",
                Ciphertext = Convert.ToBase64String(Encoding.UTF8.GetBytes(p)),
            });

        public Task<string> UnprotectAsync(ProtectedCredential c, CancellationToken ct = default) =>
            Task.FromResult(Encoding.UTF8.GetString(Convert.FromBase64String(c.Ciphertext)));

        public Task<ProtectedCredential> RewrapAsync(ProtectedCredential c, CancellationToken ct = default) =>
            Task.FromResult(c);
    }

    /// <summary>
    /// A store that holds one connection in memory. The real one needs Postgres, and
    /// nothing here is about storage — only <c>GetCredentialAsync</c> and
    /// <c>TouchAsync</c> are ever called on this path.
    /// </summary>
    private sealed class InMemoryStore : IConnectionStore
    {
        public Task<string?> GetCredentialAsync(Connection c, CancellationToken ct = default) =>
            Task.FromResult<string?>("token");

        public Task TouchAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Connection>> ListAsync(Guid u, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Connection?> GetAsync(Guid u, Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Connection?> FindByLabelAsync(Guid u, string l, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Connection> UpsertAsync(Guid u, ProviderKind p, string i, string? h, string l,
            AuthKind a, string cr, IEnumerable<Capability> caps, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetCredentialAsync(Connection c, string cr, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetDefaultAsync(Guid u, Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ClearDefaultAsync(Guid u, ProviderKind p, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetStateAsync(Guid u, Guid id, ConnectionState s, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetCapabilityEnabledAsync(Guid u, Guid id, string cap, bool on, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RenameAsync(Guid u, Guid id, string l, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(Guid u, Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetAccountIdentityAsync(Connection c, string? a, bool p, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetCredentialExpiryAsync(Connection c, DateTimeOffset? e, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Connection> ParkForAdminApprovalAsync(Guid u, ProviderKind p, string i, string l,
            IEnumerable<Capability> caps, Func<Guid, string> url, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Connection?> MarkAdminConsentGrantedAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static Connection Conn() => new()
    {
        UserId = Guid.Empty,
        Label = "github",
        Provider = ProviderKind.GitHub,
        Instance = "https://api.github.com",
        AuthKind = AuthKind.PersonalAccessToken,
        SealedCredential = "sealed",
        State = ConnectionState.Active,
    };

    private static (GitHubService Service, Stub Handler) Service(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Stub(respond);
        return (new GitHubService(new OneClient(handler), new InMemoryStore(),
            NullLogger<GitHubService>.Instance), handler);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string FileJson(string path, string content, long? size = null) =>
        $$"""
          {
            "type": "file",
            "name": "{{path.Split('/')[^1]}}",
            "path": "{{path}}",
            "size": {{size ?? Encoding.UTF8.GetByteCount(content)}},
            "html_url": "https://github.com/o/r/blob/main/{{path}}",
            "content": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes(content))}}",
            "encoding": "base64"
          }
          """;

    // ── listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_directory_lists_its_entries_with_folders_first()
    {
        var (svc, _) = Service(_ => Json("""
            [
              {"type":"file","name":"README.md","path":"README.md","size":1024},
              {"type":"dir","name":"src","path":"src","size":0},
              {"type":"file","name":"LICENSE","path":"LICENSE","size":1070},
              {"type":"dir","name":"docs","path":"docs","size":0}
            ]
            """));

        var entries = await svc.ListPathAsync(Conn(), "o/r", null, null);

        Assert.NotNull(entries);
        Assert.Equal(["docs", "src", "LICENSE", "README.md"], entries.Select(e => e.Name));
        Assert.True(entries[0].IsDirectory);
        Assert.False(entries[^1].IsDirectory);
        Assert.Equal(1070, entries.Single(e => e.Name == "LICENSE").Size);
    }

    [Fact]
    public async Task Asking_to_list_a_file_returns_null_rather_than_an_empty_folder()
    {
        // The contents endpoint answers for both, and the ONLY difference is array
        // versus object. Treating an object as an empty array would say "docs is empty"
        // about a file that exists — a wrong answer wearing the shape of a right one.
        var (svc, _) = Service(_ => Json(FileJson("README.md", "# hi")));

        Assert.Null(await svc.ListPathAsync(Conn(), "o/r", "README.md", null));
    }

    [Fact]
    public async Task Asking_to_read_a_directory_returns_null_rather_than_nonsense()
    {
        var (svc, _) = Service(_ => Json("""[{"type":"file","name":"a","path":"docs/a","size":1}]"""));

        Assert.Null(await svc.ReadFileAsync(Conn(), "o/r", "docs", null, 1000));
    }

    // ── reading ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_file_comes_back_decoded()
    {
        var (svc, handler) = Service(_ => Json(FileJson("docs/setup.md", "line one\nline two\n")));

        var file = await svc.ReadFileAsync(Conn(), "o/r", "docs/setup.md", null, 1000);

        Assert.NotNull(file);
        Assert.Equal("line one\nline two\n", file.Text);
        Assert.False(file.Truncated);
        Assert.Contains("repos/o/r/contents/docs/setup.md", handler.Urls[0]);
    }

    [Fact]
    public async Task A_branch_is_passed_through_as_the_ref()
    {
        var (svc, handler) = Service(_ => Json(FileJson("a.txt", "x")));

        await svc.ReadFileAsync(Conn(), "o/r", "a.txt", "release/2.0", 1000);

        Assert.Contains("ref=release%2F2.0", handler.Urls[0]);
    }

    [Fact]
    public async Task A_path_keeps_its_separators_and_escapes_everything_else()
    {
        // Escaping the whole path turns every slash into %2F and asks for one file whose
        // name contains slashes; escaping nothing lets a space or a hash end the URL.
        var (svc, handler) = Service(_ => Json(FileJson("a/b c/d#e.txt", "x")));

        await svc.ReadFileAsync(Conn(), "o/r", "docs/release notes/v1#final.md", null, 1000);

        Assert.Equal(
            "https://api.github.com/repos/o/r/contents/docs/release%20notes/v1%23final.md",
            handler.Urls[0]);
    }

    [Fact]
    public async Task A_long_file_is_truncated_and_says_so()
    {
        var (svc, _) = Service(_ => Json(FileJson("big.txt", new string('x', 500))));

        var file = await svc.ReadFileAsync(Conn(), "o/r", "big.txt", null, 100);

        Assert.NotNull(file);
        Assert.Equal(100, file.Text.Length);
        Assert.True(file.Truncated);

        // The size reported is the FILE's, not the excerpt's. A caller told "100 bytes"
        // about a 500-byte file has been told the excerpt is the whole thing.
        Assert.Equal(500, file.Size);
    }

    [Fact]
    public async Task A_file_too_big_to_inline_is_fetched_raw()
    {
        // Over 1 MB GitHub returns the metadata with content empty and expects a second
        // call with the raw media type. Without this a large file reads as empty, which
        // is indistinguishable from a file that genuinely is.
        var (svc, handler) = Service(req =>
            req.Headers.Accept.Any(a => a.MediaType == "application/vnd.github.raw")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("the real bytes") }
                : Json("""
                    {"type":"file","name":"big.bin","path":"big.bin","size":2000000,"content":"","encoding":"none"}
                    """));

        var file = await svc.ReadFileAsync(Conn(), "o/r", "big.bin", null, 1000);

        Assert.NotNull(file);
        Assert.Equal("the real bytes", file.Text);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task A_binary_file_is_refused_rather_than_mangled()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x1A, 0x0A };
        var (svc, _) = Service(_ => Json($$"""
            {"type":"file","name":"logo.png","path":"logo.png","size":7,
             "content":"{{Convert.ToBase64String(bytes)}}","encoding":"base64"}
            """));

        var ex = await Assert.ThrowsAsync<GitHubException>(
            () => svc.ReadFileAsync(Conn(), "o/r", "logo.png", null, 1000));

        Assert.Contains("binary", ex.Message);
        Assert.Contains("logo.png", ex.Message);
    }

    [Fact]
    public async Task A_submodule_says_what_it_is()
    {
        var (svc, _) = Service(_ => Json("""
            {"type":"submodule","name":"vendor","path":"vendor","size":0}
            """));

        var ex = await Assert.ThrowsAsync<GitHubException>(
            () => svc.ReadFileAsync(Conn(), "o/r", "vendor", null, 1000));

        Assert.Contains("submodule", ex.Message);
    }

    // ── the failures a caller has to act on ──────────────────────────────────

    [Fact]
    public async Task A_404_names_all_three_things_it_could_mean()
    {
        // Path wrong, branch wrong, or the token cannot see the repository at all. The
        // API cannot tell them apart and neither can we, so saying only the likeliest
        // sends the caller to check the one thing that was already right.
        var (svc, _) = Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<GitHubException>(
            () => svc.ListPathAsync(Conn(), "o/r", "docs", "main"));

        Assert.Contains("docs", ex.Message);
        Assert.Contains("main", ex.Message);
        Assert.Contains("branch", ex.Message);
        Assert.Contains("cannot see", ex.Message);
    }

    [Fact]
    public async Task Sizes_are_said_the_way_a_person_would()
    {
        Assert.Equal("512 bytes", ByteSize.Describe(512));
        Assert.Equal("1 KB", ByteSize.Describe(1024));
        Assert.Equal("1.5 KB", ByteSize.Describe(1536));
        Assert.Equal("2 MB", ByteSize.Describe(2 * 1024 * 1024));
    }
}
