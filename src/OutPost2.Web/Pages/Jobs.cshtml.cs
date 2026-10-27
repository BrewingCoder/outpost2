using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Core.Jobs;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

[Authorize]
public class JobsModel(IJobService jobs) : PageModel
{
    public IReadOnlyList<Job> Items { get; private set; } = [];

    public async Task OnGetAsync() => Items = await jobs.ListAsync(User.UserId());
}
