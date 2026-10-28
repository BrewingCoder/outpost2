using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Code;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Code;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

public sealed class RepoNamesModel(
    OutPostDbContext db,
    IConnectionStore connections,
    IRepoResolver resolver) : PageModel
{
    public sealed record Group(Connection Connection, IReadOnlyList<RepoAlias> Aliases, int Visible, string? Error);

    public List<Group> Groups { get; } = [];
    public string? Error { get; set; }
    public string? Added { get; set; }

    public async Task OnGetAsync([FromQuery] string? added, [FromQuery] string? error, CancellationToken ct)
    {
        Added = added;
        Error = error;

        var userId = User.UserId();

        var github = (await connections.ListAsync(userId, ct))
            .Where(c => c.Provider == ProviderKind.GitHub)
            .ToList();

        var all = await db.RepoAliases
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.Alias)
            .ToListAsync(ct);

        foreach (var c in github)
        {
            var mine = all.Where(a => a.ConnectionId == c.Id).ToList();

            // How many repositories this connection can actually see, which is the number
            // that answers "can you even see that repo?" without naming one. A failure
            // here is reported per connection rather than failing the page: a dead token
            // on one connection should not hide the aliases on another.
            var visible = 0;
            string? err = null;
            try
            {
                visible = (await resolver.KnownAsync(c, ct)).Count;
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            Groups.Add(new Group(c, mine, visible, err));
        }
    }
}
