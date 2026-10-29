using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Jobs;
using OutPost2.Core.Users;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Jobs;

namespace OutPost2.Tests.Files;

/// <summary>
/// The queue's recovery behaviour (OP2-101).
/// <para>
/// Against real Postgres, because the claim is a <c>FOR UPDATE SKIP LOCKED</c> statement
/// and the requeue is a set-based update — neither exists in memory, and the thing being
/// protected is a user's mail going out twice.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class JobQueueTests : IAsyncLifetime
{
    private const string Admin = "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";
    private readonly string _dbName = "op2_jobs_" + Guid.NewGuid().ToString("n")[..12];

    private OutPostDbContext _db = null!;
    private JobService _jobs = null!;
    private Guid _alice;
    private bool _ready;

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

            _jobs = new JobService(_db, NullLogger<JobService>.Instance);

            var a1 = new User { GitHubUserId = 21, GitHubLogin = "alice" };
            _db.Users.Add(a1);
            await _db.SaveChangesAsync();
            _alice = a1.Id;

            _ready = true;
        }
        catch (Exception) { _ready = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_ready) return;
        await _db.DisposeAsync();

        await using var admin = new OutPostDbContext(
            new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
    }

    private async Task<Job> Enqueue(string key = "k1")
    {
        var (job, _) = await _jobs.EnqueueAsync(_alice, JobKind.SendMail, "a big attachment",
            new { To = new[] { "x@example.com" } }, key, "work", requiresApproval: false);
        return job;
    }

    /// <summary>Pushes the heartbeat into the past, which is what a dead worker looks like.</summary>
    private Task Silence(Guid jobId, TimeSpan ago) =>
        _db.Jobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LastProgressAt, DateTimeOffset.UtcNow - ago));

    private async Task<Job> Reload(Guid id) =>
        await _db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id);

    // ── claiming ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Claiming_starts_the_heartbeat_and_counts_the_attempt()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        var job = await Enqueue();
        var claimed = await _jobs.ClaimNextAsync();

        Assert.NotNull(claimed);
        Assert.Equal(job.Id, claimed!.Id);
        Assert.Equal(1, claimed.Attempts);
        Assert.NotNull(claimed.LastProgressAt);
    }

    // ── recovery ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_job_whose_worker_went_away_goes_back_on_the_queue()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        var job = await Enqueue();
        await _jobs.ClaimNextAsync();
        await _jobs.UpdateProgressAsync(job.Id, JobState.Uploading, 40, "uploading report.pdf");
        await _jobs.SetResumeStateAsync(job.Id, """{"DraftId":"draft-1"}""");
        await Silence(job.Id, TimeSpan.FromMinutes(30));

        Assert.Equal(1, await _jobs.RequeueAbandonedAsync(TimeSpan.FromMinutes(5), 5));

        var back = await Reload(job.Id);
        Assert.Equal(JobState.Queued, back.State);

        // The whole point of requeueing rather than failing: the draft and its upload
        // sessions survive, so the next worker continues instead of starting over.
        Assert.Equal("""{"DraftId":"draft-1"}""", back.ResumeState);
    }

    [SkippableFact]
    public async Task A_job_that_is_merely_slow_is_left_alone()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        var job = await Enqueue();
        await _jobs.ClaimNextAsync();
        await _jobs.UpdateProgressAsync(job.Id, JobState.Uploading, 10, "uploading");

        Assert.Equal(0, await _jobs.RequeueAbandonedAsync(TimeSpan.FromMinutes(5), 5));
        Assert.Equal(JobState.Uploading, (await Reload(job.Id)).State);
    }

    [SkippableFact]
    public async Task A_job_interrupted_while_SENDING_is_never_requeued()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        // This is the one that matters. A send whose POST reached the mailbox before the
        // process died looks identical, from here, to one that did not — and the cost of
        // guessing wrong is the recipient getting the message twice. Up to Sending
        // nothing has left the mailbox; from Sending on, a person decides.
        var job = await Enqueue();
        await _jobs.ClaimNextAsync();
        await _jobs.UpdateProgressAsync(job.Id, JobState.Sending, 95, "sending");
        await Silence(job.Id, TimeSpan.FromHours(6));

        Assert.Equal(0, await _jobs.RequeueAbandonedAsync(TimeSpan.FromMinutes(5), 5));
        Assert.Equal(JobState.Sending, (await Reload(job.Id)).State);
    }

    [SkippableFact]
    public async Task A_job_that_keeps_killing_its_worker_is_failed_rather_than_requeued_forever()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        var job = await Enqueue();

        for (var i = 0; i < 3; i++)
        {
            await _jobs.ClaimNextAsync();
            await _jobs.UpdateProgressAsync(job.Id, JobState.Uploading, 5, "uploading");
            await Silence(job.Id, TimeSpan.FromHours(1));
            await _jobs.RequeueAbandonedAsync(TimeSpan.FromMinutes(5), maxAttempts: 3);
        }

        var dead = await Reload(job.Id);
        Assert.Equal(JobState.Failed, dead.State);

        // Retryable and explicit that nothing went out, because the user's first question
        // is whether the mail was sent.
        Assert.True(dead.IsRetryable);
        Assert.Contains("Nothing was sent", dead.Error);
    }

    // ── finishing ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Resume_state_is_kept_on_failure_and_cleared_on_success()
    {
        Skip.IfNot(_ready, "Postgres not reachable on localhost:5435");

        var failed = await Enqueue("fail");
        await _jobs.SetResumeStateAsync(failed.Id, """{"DraftId":"d"}""");
        await _jobs.CompleteAsync(failed.Id, false, "network", true);
        Assert.NotNull((await Reload(failed.Id)).ResumeState);

        var sent = await Enqueue("sent");
        await _jobs.SetResumeStateAsync(sent.Id, """{"DraftId":"d"}""");
        await _jobs.CompleteAsync(sent.Id, true, null, false);

        // Nothing left to resume, and leaving it would make a retry of a sent job look
        // like it had work outstanding.
        Assert.Null((await Reload(sent.Id)).ResumeState);
    }
}
