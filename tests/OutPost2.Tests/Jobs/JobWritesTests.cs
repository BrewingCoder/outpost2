using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Jobs;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Web.Jobs;

namespace OutPost2.Tests.Jobs;

/// <summary>
/// One gate for every write a running send makes to its job (OP2-262).
/// <para>
/// A send succeeded and was reported "Failed … Worth trying again" with Npgsql's
/// "A command is already in progress: UPDATE jobs …": fire-and-forget progress writes were
/// running on the same DbContext as the resume-state and outcome writes. The fake below
/// throws exactly that whenever two calls overlap, which is what the real context does.
/// </para>
/// </summary>
public sealed class JobWritesTests
{
    /// <summary>A job service with a DbContext's one rule: one command at a time.</summary>
    private sealed class OneAtATime : IJobService
    {
        private int _inFlight;
        public int Overlaps;
        public int FailCompletes;
        public List<string> Writes { get; } = [];
        public (bool Ok, string? Error, bool Retryable)? Outcome;

        private async Task Command(string what)
        {
            if (Interlocked.Increment(ref _inFlight) > 1)
            {
                Interlocked.Increment(ref Overlaps);
                Interlocked.Decrement(ref _inFlight);
                throw new InvalidOperationException(
                    "A command is already in progress: UPDATE jobs AS j SET \"State\" = @p");
            }

            try
            {
                await Task.Delay(2);
                lock (Writes) Writes.Add(what);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task UpdateProgressAsync(Guid jobId, JobState state, int percent, string? detail, CancellationToken ct = default) =>
            Command($"progress {percent}");

        public Task SetResumeStateAsync(Guid jobId, string? resumeState, CancellationToken ct = default) =>
            Command("resume");

        public async Task CompleteAsync(Guid jobId, bool ok, string? error, bool retryable, CancellationToken ct = default)
        {
            if (FailCompletes > 0)
            {
                FailCompletes--;
                throw new InvalidOperationException("database went away");
            }

            await Command(ok ? "complete sent" : "complete failed");
            Outcome = (ok, error, retryable);
        }

        public Task<(Job Job, bool WasExisting)> EnqueueAsync(Guid userId, JobKind kind, string description, object payload,
            string idempotencyKey, string? connectionLabel, bool requiresApproval, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Job?> GetAsync(Guid userId, Guid jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Job>> ListAsync(Guid userId, bool activeOnly = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Job?> ApproveAsync(Guid userId, Guid jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Job?> CancelAsync(Guid userId, Guid jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Job?> ClaimNextAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RequeueAbandonedAsync(TimeSpan silentFor, int maxAttempts, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static JobWrites Writes(OneAtATime jobs) =>
        new(jobs, Guid.NewGuid(), NullLogger.Instance, _ => Task.CompletedTask);

    [Fact]
    public async Task Progress_resume_and_outcome_never_overlap_on_the_context()
    {
        // The upload's shape: a report per chunk, a resume save after each attachment, and
        // the outcome straight after the last report — the moment the bug hit.
        var jobs = new OneAtATime();
        var writes = Writes(jobs);

        for (var i = 0; i < 20; i++)
        {
            writes.Report((i * 4, $"chunk {i}"));
            if (i % 5 == 0) await writes.ResumeAsync("{}", CancellationToken.None);
        }
        writes.Report((95, "sending"));
        writes.Report((100, "sent"));

        Assert.True(await writes.FinishAsync(true, null, false));

        Assert.Equal(0, jobs.Overlaps);
        Assert.Equal((true, (string?)null, false), jobs.Outcome);
    }

    [Fact]
    public async Task The_old_fire_and_forget_shape_does_overlap()
    {
        // The control: the same fake, written to the way the runner used to. If this ever
        // stops overlapping, the test above is no longer proving anything.
        var jobs = new OneAtATime();
        var tasks = new List<Task>();
        for (var i = 0; i < 20; i++)
            tasks.Add(Swallow(jobs.UpdateProgressAsync(Guid.Empty, JobState.Uploading, i, null)));
        tasks.Add(Swallow(jobs.CompleteAsync(Guid.Empty, true, null, false)));
        await Task.WhenAll(tasks);

        Assert.True(jobs.Overlaps > 0);

        static async Task Swallow(Task t) { try { await t; } catch (InvalidOperationException) { } }
    }

    [Fact]
    public async Task Nothing_is_written_after_the_outcome()
    {
        var jobs = new OneAtATime();
        var writes = Writes(jobs);

        await writes.FinishAsync(true, null, false);
        writes.Report((40, "a straggler"));
        await writes.Pending;
        await writes.ResumeAsync("{}", CancellationToken.None);

        Assert.Equal(["complete sent"], jobs.Writes);
    }

    [Fact]
    public async Task A_failed_outcome_write_is_retried_rather_than_lost()
    {
        var jobs = new OneAtATime { FailCompletes = JobWrites.FinishAttempts - 1 };
        var writes = Writes(jobs);

        Assert.True(await writes.FinishAsync(true, null, false));
        Assert.Equal(true, jobs.Outcome?.Ok);
    }

    [Fact]
    public async Task An_outcome_that_cannot_be_recorded_says_so_and_does_not_throw()
    {
        // Throwing here is what used to reach the catch-all and record a sent message as a
        // retryable failure.
        var jobs = new OneAtATime { FailCompletes = JobWrites.FinishAttempts };
        var writes = Writes(jobs);

        Assert.False(await writes.FinishAsync(true, null, false));
        Assert.Null(jobs.Outcome);
    }

    [Fact]
    public async Task A_progress_write_that_fails_is_swallowed()
    {
        var jobs = new ThrowingProgress();
        var writes = new JobWrites(jobs, Guid.NewGuid(), NullLogger.Instance);

        writes.Report((10, "x"));
        await writes.Pending;   // does not throw

        Assert.True(await writes.FinishAsync(true, null, false));
    }

    [Fact]
    public void May_have_sent_is_known_the_moment_sending_is_reported()
    {
        var writes = Writes(new OneAtATime());
        writes.Report((90, "uploading"));
        Assert.False(writes.MayHaveSent);

        writes.Report((95, "sending"));
        Assert.True(writes.MayHaveSent);   // synchronously, before the send request
    }

    private sealed class ThrowingProgress : IJobService
    {
        public Task UpdateProgressAsync(Guid jobId, JobState state, int percent, string? detail, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
        public Task CompleteAsync(Guid jobId, bool ok, string? error, bool retryable, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetResumeStateAsync(Guid jobId, string? resumeState, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(Job Job, bool WasExisting)> EnqueueAsync(Guid userId, JobKind kind, string description, object payload,
            string idempotencyKey, string? connectionLabel, bool requiresApproval, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Job?> GetAsync(Guid userId, Guid jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Job>> ListAsync(Guid userId, bool activeOnly = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Job?> ApproveAsync(Guid userId, Guid jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Job?> CancelAsync(Guid userId, Guid jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Job?> ClaimNextAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RequeueAbandonedAsync(TimeSpan silentFor, int maxAttempts, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
