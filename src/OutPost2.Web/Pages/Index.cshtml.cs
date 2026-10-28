using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Core.Connections;
using OutPost2.Core.Jobs;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

[Authorize]
public class IndexModel(IConnectionStore store, IJobService jobs) : PageModel
{
    public IReadOnlyList<Connection> All { get; private set; } = [];
    public IReadOnlyList<Connection> NeedAttention { get; private set; } = [];
    public int OnAir { get; private set; }
    public int Waiting { get; private set; }
    public string? Login { get; private set; }

    /// <summary>
    /// One row per thing the user can actually say, built from the connections that exist
    /// rather than written down anywhere. A phrase list that drifts from the connections
    /// it describes is worse than none, because it teaches sentences that fail.
    /// </summary>
    public sealed record Phrase(string Sentence, string Label, bool NeedsName);

    public IReadOnlyList<Phrase> Phrases { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Login = User.FindFirst(ClaimTypes.Name)?.Value;

        All = await store.ListAsync(User.UserId());

        OnAir = All.Count(c => c.State is ConnectionState.Active or ConnectionState.Consented);
        NeedAttention = All.Where(c => c.State is not (ConnectionState.Active or ConnectionState.Consented)).ToList();

        var queue = await jobs.ListAsync(User.UserId());
        Waiting = queue.Count(j => j.State == JobState.AwaitingApproval);

        Phrases =
        [
            .. All
                .Where(c => c.State is ConnectionState.Active or ConnectionState.Consented)
                .OrderBy(c => c.Provider).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .Select(c => new Phrase(
                    CapabilityCatalog.SpokenExample(c.Provider, c.Label),
                    c.Label,
                    // Whether the name is load-bearing: with no default for this provider,
                    // leaving it out means Claude stops and asks.
                    NeedsName: !All.Any(x => x.Provider == c.Provider && x.IsDefaultForProvider)))
        ];
    }
}
