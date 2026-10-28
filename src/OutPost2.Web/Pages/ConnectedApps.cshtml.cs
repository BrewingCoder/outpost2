using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Core.Auth;
using OutPost2.Infrastructure.Auth;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Pages;

/// <summary>
/// What currently holds a token to this account, and the means to cut it off.
/// <para>
/// This did not exist. Everything needed for it did — tokens are listed, revocation is
/// immediate and already tested — but with no page, connecting Claude was a one-way door
/// from the browser's point of view. A consent screen that cannot point at a way to
/// undo the consent is only telling half the story.
/// </para>
/// </summary>
[Authorize]
public class ConnectedAppsModel(IOAuthService oauth) : PageModel
{
    public IReadOnlyList<OAuthToken> Tokens { get; private set; } = [];

    public async Task OnGetAsync() => Tokens = await oauth.ListTokensAsync(User.UserId());

    public static string Ago(DateTimeOffset t) => ConnectionsModel.Ago(t);
}
