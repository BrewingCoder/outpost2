using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Jobs;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Infrastructure.Jobs;

/// <summary>Payload for a <see cref="JobKind.SendMail"/> job.</summary>
public sealed record SendMailPayload(
    Guid ConnectionId,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string Subject,
    string Body,
    IReadOnlyList<Guid> AttachmentIds);

public interface IJobService
{
    /// <summary>
    /// Enqueues, or returns the existing job for the same idempotency key. A model that
    /// retries a timed-out tool call must not send twice.
    /// </summary>
    Task<(Job Job, bool WasExisting)> EnqueueAsync(
        Guid userId, JobKind kind, string description, object payload,
        string idempotencyKey, string? connectionLabel, bool requiresApproval,
        CancellationToken ct = default);

    Task<Job?> GetAsync(Guid userId, Guid jobId, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> ListAsync(Guid userId, bool activeOnly = false, CancellationToken ct = default);
    Task<Job?> ApproveAsync(Guid userId, Guid jobId, CancellationToken ct = default);
    Task<Job?> CancelAsync(Guid userId, Guid jobId, CancellationToken ct = default);

    /// <summary>Claims the next runnable job for the worker. Null when there is nothing to do.</summary>
    Task<Job?> ClaimNextAsync(CancellationToken ct = default);

    Task UpdateProgressAsync(Guid jobId, JobState state, int percent, string? detail, CancellationToken ct = default);
    Task CompleteAsync(Guid jobId, bool ok, string? error, bool retryable, CancellationToken ct = default);
}

public sealed class JobService(OutPostDbContext db, ILogger<JobService> log) : IJobService
{
    public async Task<(Job Job, bool WasExisting)> EnqueueAsync(
        Guid userId, JobKind kind, string description, object payload,
        string idempotencyKey, string? connectionLabel, bool requiresApproval,
        CancellationToken ct = default)
    {
        var existing = await db.Jobs.FirstOrDefaultAsync(
            j => j.UserId == userId && j.IdempotencyKey == idempotencyKey, ct);

        if (existing is not null) return (existing, true);

        var job = new Job
        {
            UserId = userId,
            Kind = kind,
            Description = description,
            Payload = JsonSerializer.Serialize(payload),
            IdempotencyKey = idempotencyKey,
            ConnectionLabel = connectionLabel,
            State = requiresApproval ? JobState.AwaitingApproval : JobState.Queued,
        };

        db.Jobs.Add(job);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent retry. The unique index did its job; return
            // the winner rather than failing, because the caller's intent is satisfied.
            db.Entry(job).State = EntityState.Detached;
            var winner = await db.Jobs.FirstAsync(
                j => j.UserId == userId && j.IdempotencyKey == idempotencyKey, ct);
            return (winner, true);
        }

        return (job, false);
    }

    public Task<Job?> GetAsync(Guid userId, Guid jobId, CancellationToken ct = default) =>
        db.Jobs.FirstOrDefaultAsync(j => j.UserId == userId && j.Id == jobId, ct);

    public async Task<IReadOnlyList<Job>> ListAsync(
        Guid userId, bool activeOnly = false, CancellationToken ct = default)
    {
        var q = db.Jobs.Where(j => j.UserId == userId);

        if (activeOnly)
            q = q.Where(j => j.State != JobState.Sent
                          && j.State != JobState.Failed
                          && j.State != JobState.Cancelled);

        return await q.OrderByDescending(j => j.CreatedAt).Take(100).ToListAsync(ct);
    }

    public async Task<Job?> ApproveAsync(Guid userId, Guid jobId, CancellationToken ct = default)
    {
        var job = await GetAsync(userId, jobId, ct);
        if (job is null || job.State != JobState.AwaitingApproval) return job;

        job.State = JobState.Queued;
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<Job?> CancelAsync(Guid userId, Guid jobId, CancellationToken ct = default)
    {
        var job = await GetAsync(userId, jobId, ct);
        if (job is null || job.IsTerminal) return job;

        job.State = JobState.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<Job?> ClaimNextAsync(CancellationToken ct = default)
    {
        // SKIP LOCKED so several workers can run without handing the same job to two of
        // them — which for a send job would mean the recipient gets it twice.
        var claimed = await db.Jobs
            .FromSqlRaw("""
                UPDATE jobs SET "State" = 'Staging', "StartedAt" = now()
                WHERE "Id" = (
                    SELECT "Id" FROM jobs
                    WHERE "State" = 'Queued'
                    ORDER BY "CreatedAt"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(ct);

        return claimed.FirstOrDefault();
    }

    public async Task UpdateProgressAsync(
        Guid jobId, JobState state, int percent, string? detail, CancellationToken ct = default) =>
        await db.Jobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, state)
                .SetProperty(j => j.PercentComplete, percent)
                .SetProperty(j => j.Detail, detail), ct);

    public async Task CompleteAsync(
        Guid jobId, bool ok, string? error, bool retryable, CancellationToken ct = default)
    {
        await db.Jobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, ok ? JobState.Sent : JobState.Failed)
                .SetProperty(j => j.PercentComplete, ok ? 100 : 0)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.IsRetryable, retryable)
                .SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow), ct);

        log.LogInformation("Job {JobId} finished: {Outcome}.", jobId, ok ? "sent" : $"failed ({error})");
    }
}
