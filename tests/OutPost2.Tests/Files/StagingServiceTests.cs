using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Users;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Files;

namespace OutPost2.Tests.Files;

/// <summary>
/// Runs against real Garage and real Postgres. The ranged-read behaviour here is what the
/// entire large-attachment pipeline rests on (OP2-10), so proving it against a fake would
/// prove nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StagingServiceTests : IAsyncLifetime
{
    private const string Admin = "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";
    private readonly string _dbName = "op2_stage_" + Guid.NewGuid().ToString("n")[..12];

    private OutPostDbContext _db = null!;
    private S3ObjectStore _store = null!;
    private StagingService _staging = null!;
    private Guid _alice, _bob;
    private bool _ready;
    private string _why = "";

    private static StorageOptions Options() => new()
    {
        ServiceUrl = "http://localhost:3900",
        AccessKey = "GK" + new string('0', 22) + "d2",
        SecretKey = new string('0', 62) + "d2",
        Region = "garage",
        StagingBucket = "op2-staging",
        ForcePathStyle = true,
        PerUserQuotaBytes = 8L * 1024 * 1024,   // small, so the quota path is testable
    };

    public async Task InitializeAsync()
    {
        try
        {
            await using (var a = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options))
                await a.Database.ExecuteSqlRawAsync($"CREATE DATABASE {_dbName}");

            _db = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>()
                .UseNpgsql(Admin.Replace("Database=postgres", $"Database={_dbName}")).Options);
            await _db.Database.MigrateAsync();

            var opts = Microsoft.Extensions.Options.Options.Create(Options());
            _store = new S3ObjectStore(opts, NullLogger<S3ObjectStore>.Instance);

            // Prove the store is actually reachable before claiming the suite can run.
            await _store.ExistsAsync("probe-" + Guid.NewGuid().ToString("n"));

            _staging = new StagingService(_db, _store, opts, NullLogger<StagingService>.Instance);

            var a1 = new User { GitHubUserId = 11, GitHubLogin = "alice" };
            var b1 = new User { GitHubUserId = 12, GitHubLogin = "bob" };
            _db.Users.AddRange(a1, b1);
            await _db.SaveChangesAsync();
            (_alice, _bob) = (a1.Id, b1.Id);

            _ready = true;
        }
        catch (Exception ex) { _why = $"Garage/Postgres unavailable: {ex.Message}"; }
    }

    public async Task DisposeAsync()
    {
        _store?.Dispose();
        if (_db is null) return;
        await _db.DisposeAsync();
        await using var a = new OutPostDbContext(
            new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
        await a.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
    }

    private static byte[] Pattern(int size) =>
        [.. Enumerable.Range(0, size).Select(i => (byte)((i * 7919) % 256))];

    [SkippableFact]
    public async Task Uploads_and_reads_back_byte_identical()
    {
        Skip.IfNot(_ready, _why);

        var data = Pattern(64 * 1024);
        var f = await _staging.UploadAsync(_alice, "test doc", "test.bin", "application/octet-stream",
            new MemoryStream(data));

        await using var back = await _staging.OpenAsync(f);
        using var ms = new MemoryStream();
        await back.CopyToAsync(ms);

        Assert.Equal(data, ms.ToArray());
        Assert.Equal(data.Length, f.SizeBytes);
    }

    [SkippableFact]
    public async Task Ranged_reads_reassemble_exactly_in_320KiB_chunks()
    {
        Skip.IfNot(_ready, _why);

        // Exactly the access pattern the Graph upload session demands: chunks that are a
        // multiple of 320 KiB, read straight from storage and never buffered whole.
        const int chunk = 320 * 1024;
        var data = Pattern(chunk * 3 + 12345);

        var f = await _staging.UploadAsync(_alice, "big", "big.bin", "application/octet-stream",
            new MemoryStream(data));

        using var rebuilt = new MemoryStream();
        long sent = 0;
        var reads = 0;

        while (sent < f.SizeBytes)
        {
            var end = Math.Min(sent + chunk, f.SizeBytes) - 1;
            await using var slice = await _staging.OpenRangeAsync(f, sent, end);
            await slice.CopyToAsync(rebuilt);
            sent = end + 1;
            reads++;
        }

        Assert.Equal(4, reads);                       // 3 full chunks plus a remainder
        Assert.Equal(data, rebuilt.ToArray());
    }

    [SkippableFact]
    public async Task Records_a_hash_so_an_upload_can_be_shown_intact()
    {
        Skip.IfNot(_ready, _why);

        var data = Pattern(4096);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

        var f = await _staging.UploadAsync(_alice, "hashme", "h.bin", "application/octet-stream",
            new MemoryStream(data));

        Assert.Equal(expected, f.Sha256);
    }

    // ── name matching: a voice transcript never matches character for character ──

    [SkippableTheory]
    [InlineData("Signed contract v3")]
    [InlineData("signed contract v3")]
    [InlineData("Signed")]
    [InlineData("contract")]
    public async Task Finds_a_file_by_an_approximate_spoken_name(string spoken)
    {
        Skip.IfNot(_ready, _why);

        await _staging.UploadAsync(_alice, "Signed contract v3", "contract.pdf", "application/pdf",
            new MemoryStream(Pattern(1024)));

        var found = await _staging.FindByNameAsync(_alice, spoken);

        Assert.NotNull(found);
        Assert.Equal("Signed contract v3", found!.Name);
    }

    [SkippableFact]
    public async Task Falls_back_to_the_original_filename()
    {
        Skip.IfNot(_ready, _why);

        await _staging.UploadAsync(_alice, "Quarterly numbers", "budget-2026.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            new MemoryStream(Pattern(512)));

        Assert.NotNull(await _staging.FindByNameAsync(_alice, "budget-2026"));
    }

    [SkippableFact]
    public async Task Does_not_find_another_users_file()
    {
        Skip.IfNot(_ready, _why);

        await _staging.UploadAsync(_alice, "alice secret", "a.bin", "application/octet-stream",
            new MemoryStream(Pattern(256)));

        Assert.Null(await _staging.FindByNameAsync(_bob, "alice secret"));
        Assert.Empty(await _staging.ListAsync(_bob));
    }

    [SkippableFact]
    public async Task Cannot_delete_another_users_file()
    {
        Skip.IfNot(_ready, _why);

        var f = await _staging.UploadAsync(_alice, "mine", "m.bin", "application/octet-stream",
            new MemoryStream(Pattern(256)));

        await _staging.DeleteAsync(_bob, f.Id);       // silently a no-op

        Assert.Single(await _staging.ListAsync(_alice));
    }

    [SkippableFact]
    public async Task Quota_is_enforced_and_says_so_plainly()
    {
        Skip.IfNot(_ready, _why);

        await _staging.UploadAsync(_alice, "first", "1.bin", "application/octet-stream",
            new MemoryStream(Pattern(5 * 1024 * 1024)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _staging.UploadAsync(_alice, "second", "2.bin", "application/octet-stream",
                new MemoryStream(Pattern(5 * 1024 * 1024))));

        Assert.Contains("quota", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Deleting_removes_the_object_as_well_as_the_row()
    {
        Skip.IfNot(_ready, _why);

        var f = await _staging.UploadAsync(_alice, "temp", "t.bin", "application/octet-stream",
            new MemoryStream(Pattern(1024)));

        Assert.True(await _store.ExistsAsync(f.StorageKey));

        await _staging.DeleteAsync(_alice, f.Id);

        // An orphaned object is wasted disk that nothing will ever reclaim.
        Assert.False(await _store.ExistsAsync(f.StorageKey));
        Assert.Empty(await _staging.ListAsync(_alice));
    }

    [SkippableFact]
    public async Task Expired_files_are_purged_from_both_places()
    {
        Skip.IfNot(_ready, _why);

        var f = await _staging.UploadAsync(_alice, "old", "o.bin", "application/octet-stream",
            new MemoryStream(Pattern(1024)));

        f.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        Assert.Equal(1, await _staging.PurgeExpiredAsync());
        Assert.False(await _store.ExistsAsync(f.StorageKey));
    }

    [SkippableFact]
    public async Task Expired_files_do_not_appear_in_listings_or_lookups()
    {
        Skip.IfNot(_ready, _why);

        var f = await _staging.UploadAsync(_alice, "stale", "s.bin", "application/octet-stream",
            new MemoryStream(Pattern(256)));

        f.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _db.SaveChangesAsync();

        Assert.Empty(await _staging.ListAsync(_alice));
        Assert.Null(await _staging.FindByNameAsync(_alice, "stale"));
    }
}
