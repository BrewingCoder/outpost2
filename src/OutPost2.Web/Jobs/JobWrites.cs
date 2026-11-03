using OutPost2.Core.Jobs;
using OutPost2.Infrastructure.Jobs;

namespace OutPost2.Web.Jobs;

/// <summary>
/// Every write a running send makes to its job row, one at a time (OP2-262).
/// <para>
/// The job's <see cref="IJobService"/> sits on the request scope's single
/// <c>DbContext</c>, which cannot run two commands at once. Progress used to be reported
/// with fire-and-forget writes from a <see cref="Progress{T}"/> callback, firing on every
/// chunk while the sender saved resume state and the runner recorded the outcome on that
/// same context. When two met, Npgsql threw "A command is already in progress", the
/// outcome write lost, and the catch-all recorded a send that HAD gone as
/// "Failed … Worth trying again" — advice that, followed, sends the message twice. This is
/// the same root cause as OP2-239, which was fixed for <c>IConnectionStore</c> only.
/// </para>
/// <para>
/// So: one gate for all of them; progress is best effort and never throws; nothing is
/// written after the outcome; and the outcome itself is retried, because once a message
/// has gone the one thing that must not happen is losing the record that it went.
/// </para>
/// </summary>
public sealed class JobWrites(
    IJobService jobs, Guid jobId, ILogger log, Func<TimeSpan, Task>? delay = null)
    : IProgress<(int Percent, string Detail)>
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, Task> _delay = delay ?? (t => Task.Delay(t));
    private bool _closed;

    /// <summary>How many times the outcome is attempted before giving up on recording it.</summary>
    public const int FinishAttempts = 4;

    /// <summary>
    /// True from the moment the sender says it is sending. Set synchronously inside
    /// <see cref="Report"/>, which the sender calls just before the send request, so
    /// anything that goes wrong after this point is treated as "may have gone".
    /// </summary>
    public bool MayHaveSent { get; private set; }

    /// <summary>The last progress write started, so a test (or a shutdown) can wait on it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Called by the sender, synchronously. This implements <see cref="IProgress{T}"/>
    /// directly instead of using <see cref="Progress{T}"/>, which posts each report to the
    /// thread pool and so can deliver one after the send has finished.
    /// </summary>
    public void Report((int Percent, string Detail) value)
    {
        if (value.Percent >= 95) MayHaveSent = true;

        Pending = ProgressAsync(
            value.Percent >= 95 ? JobState.Sending : JobState.Uploading, value.Percent, value.Detail);
    }

    private async Task ProgressAsync(JobState state, int percent, string detail)
    {
        await _gate.WaitAsync();
        try
        {
            if (_closed) return;
            await jobs.UpdateProgressAsync(jobId, state, percent, detail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Progress is a courtesy. Losing one report costs a stale percentage; letting
            // it throw — or worse, go unobserved and poison the context — cost OP2-262.
            log.LogWarning(ex, "Progress for job {JobId} was not recorded.", jobId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves what a resumed attempt would need. Errors propagate: this is called only
    /// before the send, where failing is safe and nothing has left the mailbox.
    /// </summary>
    public async Task ResumeAsync(string state, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_closed) return;
            await jobs.SetResumeStateAsync(jobId, state, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes the gate without recording an outcome, once any write in flight has finished.
    /// For a job being left for the abandoned-job sweep to resume: nothing more may be
    /// written to it from this attempt, and the scope's context must not be disposed under
    /// a progress write still running on it.
    /// </summary>
    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        _closed = true;
        _gate.Release();
    }

    /// <summary>
    /// Records the outcome and closes the gate to everything after it. Never throws, and
    /// never takes a cancellation token: a shutdown arriving after the message went must
    /// not stop the job saying so. Returns false only when every attempt failed — then the
    /// job is left as it was (Sending, if it got that far), which the abandoned-job sweep
    /// deliberately never requeues.
    /// </summary>
    public async Task<bool> FinishAsync(bool ok, string? error, bool retryable)
    {
        await _gate.WaitAsync();
        try
        {
            _closed = true;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await jobs.CompleteAsync(jobId, ok, error, retryable, CancellationToken.None);
                    return true;
                }
                catch (Exception ex) when (attempt < FinishAttempts)
                {
                    log.LogWarning(ex, "Recording the outcome of job {JobId} failed (attempt {Attempt}); retrying.",
                        jobId, attempt);
                    await _delay(TimeSpan.FromMilliseconds(250 * attempt));
                }
                catch (Exception ex)
                {
                    log.LogError(ex,
                        "The outcome of job {JobId} ({Outcome}) could not be recorded after {Attempts} attempts.",
                        jobId, ok ? "sent" : "failed", attempt);
                    return false;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
