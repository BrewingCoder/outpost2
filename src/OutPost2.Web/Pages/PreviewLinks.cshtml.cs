using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Core.Auth;
using OutPost2.Infrastructure.Auth;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

[Authorize]
public class PreviewLinksModel(IPreviewLinkService links) : PageModel
{
    public IReadOnlyList<PreviewLink> Links { get; private set; } = [];

    /// <summary>
    /// The full URL, present for exactly one render after minting. It is not stored, so a
    /// refresh loses it — deliberately: a credential that can be re-read later is one that
    /// lives somewhere, and this one lives nowhere.
    /// </summary>
    public string? JustMintedUrl { get; private set; }

    public string? JustMintedExpires { get; private set; }

    public async Task OnGetAsync(string? token, string? expires)
    {
        Links = await links.ListAsync(User.UserId());

        if (!string.IsNullOrWhiteSpace(token))
        {
            JustMintedUrl = $"{Request.Scheme}://{Request.Host}/preview?t={Uri.EscapeDataString(token)}";
            JustMintedExpires = expires;
        }
    }
}
