using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OutPost2.Web.Auth;

namespace OutPost2.Web.Pages;

[AllowAnonymous]
public class SignInModel(GitHubOptions github) : PageModel
{
    public bool Configured => github.IsConfigured;
}
