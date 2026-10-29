namespace OutPost2.Core.Jobs;

/// <summary>
/// One unit of long-running or approval-gated work.
/// <para>
/// Two requirements converged on this single surface (OP2-11). D1 says voice means the
/// user cannot see what they are approving, so destructive and outbound actions want
/// read-back or deferral to a screen. D3 says large attachments make sending an
/// asynchronous job needing progress and completion. The approval queue and the job queue
/// are the same list — things proposed, in flight, done and failed — so it is built once.
/// </para>
/// </summary>
public sealed class Job
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid UserId { get; init; }
    public required JobKind Kind { get; init; }
    public JobState State { get; set; } = JobState.Queued;

    /// <summary>A sentence the model can read aloud. Every state must have one.</summary>
    public required string Description { get; set; }

    /// <summary>JSON payload describing the work. Shape depends on <see cref="Kind"/>.</summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Caller-supplied or derived. A model retrying a timed-out tool call must not send
    /// twice, so a repeat with the same key returns the existing job.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    public string? ConnectionLabel { get; init; }

    public int PercentComplete { get; set; }
    public string? Detail { get; set; }
    public string? Error { get; set; }

    /// <summary>Whether a failure is worth retrying, so the UI can say "try again" honestly.</summary>
    public bool IsRetryable { get; set; }

    /// <summary>Draft id, upload session URL, bytes sent — whatever a resume needs.</summary>
    public string? ResumeState { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Last sign of life from the worker running this job (OP2-101).
    /// <para>
    /// A heartbeat rather than <see cref="StartedAt"/>, because the question being asked
    /// is "did the process holding this die" and a legitimately long upload has been
    /// started for just as long as an abandoned one. Progress is reported per chunk, so
    /// silence here really is silence.
    /// </para>
    /// </summary>
    public DateTimeOffset? LastProgressAt { get; set; }

    /// <summary>
    /// How many times this job has been picked up. Carried so a job that kills its
    /// worker cannot be requeued forever — the queue would spend every cycle on it.
    /// </summary>
    public int Attempts { get; set; }

    public bool IsTerminal => State is JobState.Sent or JobState.Failed or JobState.Cancelled;

    /// <summary>
    /// The user will ask "did that go?" while driving, so every state owes a plain answer.
    /// </summary>
    public string Spoken() => State switch
    {
        JobState.AwaitingApproval => $"Waiting for you to approve: {Description}",
        JobState.Queued => $"Queued: {Description}",
        JobState.Staging => $"Preparing: {Description}",
        JobState.Uploading => $"Uploading {Description} — {PercentComplete}% done",
        JobState.Sending => $"Sending: {Description}",
        JobState.Sent => $"Sent: {Description}",
        JobState.Cancelled => $"Cancelled: {Description}",
        JobState.Failed => $"Failed: {Description}. {Error}"
                           + (IsRetryable ? " Worth trying again." : ""),
        _ => Description,
    };
}

public enum JobKind
{
    SendMail,
}

public enum JobState
{
    /// <summary>Proposed but not authorised — the deferred-approval path from D1.</summary>
    AwaitingApproval,

    Queued,
    Staging,
    Uploading,
    Sending,

    Sent,
    Failed,
    Cancelled,
}
