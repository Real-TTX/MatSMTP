using MatPlatform;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Account;

public sealed class LogoutModel(SignInService signIn, ActivityLogger log, MatPlatformSettings platform) : PageModelBase
{
    /// <summary>Signing out changes state, so it is a POST (with the anti-forgery token of the menu): a link from another site cannot do it.</summary>
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await log.LogAsync("Abmeldung");
        var idToken = await signIn.EndSessionAsync(HttpContext);

        // After a sign-in through MatIdentity the session there ends too; it sends the browser back to /Account/Login?signedOut=1.
        return platform.IdentityEnabled && !string.IsNullOrEmpty(idToken)
            ? MatIdentitySignIn.SignOutAtIdentity(HttpContext, idToken)
            : RedirectToPage("/Account/Login", new { signedOut = platform.IdentityEnabled ? "1" : null });
    }
}
