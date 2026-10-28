using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OutPost2.Web.Pages;

[AllowAnonymous]
public class DeniedModel : PageModel
{
    public string? Reason { get; private set; }
    public string? Login { get; private set; }

    public bool Unconfigured => Reason == "unconfigured";
    public bool Preview => Reason == "preview";

    public void OnGet(string? reason, string? login)
    {
        Reason = reason;

        // Echoed so the user can see WHICH account was refused. Signing in with the wrong
        // one of several GitHub accounts is the common case, and "not authorised" without
        // naming the account leaves no way to tell that is what happened.
        Login = login;
    }
}
