using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Core.Jobs;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

[Authorize]
public class JobsModel(IJobService jobs) : PageModel
{
    public IReadOnlyList<Job> Waiting { get; private set; } = [];
    public IReadOnlyList<Job> Running { get; private set; } = [];
    public IReadOnlyList<Job> Recent { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var all = await jobs.ListAsync(User.UserId());

        Waiting = all.Where(j => j.State == JobState.AwaitingApproval)
            .OrderBy(j => j.CreatedAt).ToList();

        Running = all.Where(j => !j.IsTerminal && j.State != JobState.AwaitingApproval)
            .OrderBy(j => j.CreatedAt).ToList();

        // A week. Long enough to answer "did that actually send", short enough that the
        // page stays readable without paging.
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        Recent = all.Where(j => j.IsTerminal && j.CreatedAt >= since)
            .OrderByDescending(j => j.CompletedAt ?? j.CreatedAt).ToList();
    }

    public static string Ago(DateTimeOffset t) => ConnectionsModel.Ago(t);

    public static (string Word, string Class) Status(JobState s) => s switch
    {
        JobState.Sent => ("Done", "active"),
        JobState.Failed => ("Failed", "revoked"),
        JobState.Cancelled => ("Discarded", "expired"),
        JobState.AwaitingApproval => ("Waiting for you", "pending"),
        _ => (s.ToString(), "pending"),
    };
}
