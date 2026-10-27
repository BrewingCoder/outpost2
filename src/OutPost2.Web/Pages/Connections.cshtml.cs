using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

[Authorize]
public class ConnectionsModel(IConnectionStore store) : PageModel
{
    public IReadOnlyList<Connection> Connections { get; private set; } = [];
    public string? Error { get; private set; }
    public string? Added { get; private set; }

    public async Task OnGetAsync(string? error, string? added)
    {
        Error = error;
        Added = added;
        Connections = await store.ListAsync(User.UserId());
    }
}
