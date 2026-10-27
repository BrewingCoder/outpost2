using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Core.Users;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Issues;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Tests.Issues;

/// <summary>
/// Drives the YouTrack operations against the real lab server and the real OP2 project.
/// Read-only apart from one scratch issue that is closed immediately, so a test run never
/// disturbs the actual backlog.
/// </summary>
[Trait("Category", "Integration")]
public sealed class YouTrackServiceTests : IAsyncLifetime
{
    private const string Admin = "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";
    private readonly string _dbName = "op2_yts_" + Guid.NewGuid().ToString("n")[..12];

    private OutPostDbContext _db = null!;
    private YouTrackService _svc = null!;
    private VaultTransitClient _vault = null!;
    private Connection _conn = null!;
    private bool _ready;
    private string _why = "";

    public async Task InitializeAsync()
    {
        try
        {
            var credsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "credentials.json");
            using var creds = JsonDocument.Parse(await File.ReadAllTextAsync(credsPath));
            var url = creds.RootElement.GetProperty("lab_youtrack_url").GetString()!;
            var token = creds.RootElement.GetProperty("lab_youtrack_token").GetString()!;

            await using (var a = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options))
                await a.Database.ExecuteSqlRawAsync($"CREATE DATABASE {_dbName}");

            _db = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>()
                .UseNpgsql(Admin.Replace("Database=postgres", $"Database={_dbName}")).Options);
            await _db.Database.MigrateAsync();

            _vault = new VaultTransitClient(Microsoft.Extensions.Options.Options.Create(new VaultOptions
            {
                Address = "http://localhost:8200", AuthMethod = VaultAuthMethod.Token,
                Token = "op2-dev-root", TransitMount = "op2-transit", KeyName = "op2-kek",
            }), NullLogger<VaultTransitClient>.Instance);

            if (!await _vault.HealthyAsync()) { _why = "dev Vault unreachable"; return; }

            var store = new ConnectionStore(_db, new CredentialProtector(_vault));
            var u = new User { GitHubUserId = 7, GitHubLogin = "tester" };
            _db.Users.Add(u);
            await _db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddHttpClient("youtrack");
            var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

            var ytConn = new YouTrackConnectionService(factory, store, NullLogger<YouTrackConnectionService>.Instance);
            _conn = await ytConn.ConnectAsync(u.Id, url, token, "lab");

            _svc = new YouTrackService(factory, store, NullLogger<YouTrackService>.Instance);
            _ready = true;
        }
        catch (Exception ex) { _why = ex.Message; }
    }

    public async Task DisposeAsync()
    {
        _vault?.Dispose();
        if (_db is null) return;
        await _db.DisposeAsync();
        await using var a = new OutPostDbContext(
            new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
        await a.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
    }

    [SkippableFact]
    public async Task Lists_projects_including_OP2()
    {
        Skip.IfNot(_ready, _why);

        var projects = await _svc.ListProjectsAsync(_conn);

        Assert.Contains(projects, p => p.ShortName == "OP2");
    }

    [SkippableFact]
    public async Task Searches_issues_in_OP2()
    {
        Skip.IfNot(_ready, _why);

        var issues = await _svc.SearchAsync(_conn, "project: OP2", 50);

        Assert.NotEmpty(issues);
        Assert.All(issues, i => Assert.Equal("OP2", i.Project));
        Assert.Contains(issues, i => i.Key == "OP2-1");
    }

    [SkippableFact]
    public async Task Reads_an_issue_with_its_state()
    {
        Skip.IfNot(_ready, _why);

        var issue = await _svc.GetIssueAsync(_conn, "OP2-15");

        Assert.NotNull(issue);
        Assert.Equal("OP2-15", issue!.Key);
        Assert.False(string.IsNullOrWhiteSpace(issue.Summary));
        Assert.False(string.IsNullOrWhiteSpace(issue.State));
    }

    [SkippableFact]
    public async Task Unknown_issue_returns_null_rather_than_throwing()
    {
        Skip.IfNot(_ready, _why);

        Assert.Null(await _svc.GetIssueAsync(_conn, "OP2-999999"));
    }

    [SkippableFact]
    public async Task Create_issue_names_the_available_projects_when_the_short_name_is_wrong()
    {
        Skip.IfNot(_ready, _why);

        // A wrong project name is the likeliest mistake by voice, so the error must tell
        // the user what they could have said instead.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.CreateIssueAsync(_conn, "NOPE", "should not be created", null));

        Assert.Contains("No project 'NOPE'", ex.Message);
        Assert.Contains("OP2", ex.Message);
    }

    [SkippableFact]
    public async Task Creates_an_issue_comments_on_it_and_closes_it()
    {
        Skip.IfNot(_ready, _why);

        var key = await _svc.CreateIssueAsync(
            _conn, "OP2", "[automated test] scratch issue — safe to delete",
            "Created by the OutPost2 integration suite to exercise create/comment/command. "
            + "Closed immediately.");

        Assert.StartsWith("OP2-", key);

        await _svc.CommentAsync(_conn, key, "Automated test comment.");
        await _svc.ApplyCommandAsync(_conn, key, "State Obsolete");

        var reloaded = await _svc.GetIssueAsync(_conn, key);
        Assert.NotNull(reloaded);
        Assert.Equal("Obsolete", reloaded!.State);
        Assert.Contains(reloaded.Comments, c => c.Contains("Automated test comment"));
    }
}
