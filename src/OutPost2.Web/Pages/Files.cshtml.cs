using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Files;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

[Authorize]
public class FilesModel(IStagingService staging) : PageModel
{
    public IReadOnlyList<StagedFile> Files { get; private set; } = [];
    public string? Error { get; private set; }
    public string? Added { get; private set; }

    public async Task OnGetAsync(string? error, string? added)
    {
        Error = error;
        Added = added;
        Files = await staging.ListAsync(User.UserId());
    }

    public static string Ago(DateTimeOffset t) => ConnectionsModel.Ago(t);
}
