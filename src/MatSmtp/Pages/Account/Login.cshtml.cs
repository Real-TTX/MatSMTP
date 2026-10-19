using System.ComponentModel.DataAnnotations;
using MatPlatform;
using MatSmtp.Platform;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Account;

/// <summary>
/// The local sign-in with address (or name) and password, then the code of the authenticator app when the account has one. With MatIdentity configured
/// (<c>MATIDENTITY_AUTHORITY</c>) a visit goes straight on to MatIdentity; the local form stays reachable with <c>?local=1</c> (the way in when MatIdentity is
/// down), <c>?signedOut=1</c> (back from the sign-out there) and <c>?ssoError=1</c> (the sign-in there failed) show the page without going there again.
/// After a sign-in through MatIdentity whose address matches an account that is not linked yet, the page shows <c>?local=1&amp;link=1</c>: a complete
/// sign-in of exactly that account (with its second factor) links it (<see cref="MatIdentityPendingLink"/>, <see cref="UserService.LinkExternalAsync"/>);
/// another account signs in without linking anything; Cancel deletes the pending link.
/// </summary>
[AllowAnonymous]
public sealed class LoginModel(SignInService signIn, UserService users, TwoFactorTicket ticket, ActivityLogger log, MatPlatformSettings platform, ILogger<LoginModel> logger)
    : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib deine E-Mail-Adresse oder deinen Benutzernamen ein.")]
        [Display(Name = "E-Mail-Adresse")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib dein Passwort ein.")]
        [Display(Name = "Passwort")]
        public string Password { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary><c>?local=1</c>: the local form even when MatIdentity is configured.</summary>
    public bool Local { get; private set; }

    /// <summary><c>?signedOut=1</c>: back from the sign-out at MatIdentity, no automatic sign-in.</summary>
    public bool SignedOut { get; private set; }

    /// <summary><c>?ssoError=1</c>: the sign-in at MatIdentity failed or MatIdentity cannot be reached.</summary>
    public bool SsoError { get; private set; }

    /// <summary>The pending link to an account with this address, when MatIdentity sent the person here to prove it (<c>?link=1</c>).</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    /// <summary><c>?link=1</c> without a pending link: it expired (10 minutes) or was cancelled.</summary>
    public bool LinkExpired { get; private set; }

    public bool IdentityEnabled => platform.IdentityEnabled;

    /// <summary>The address that starts the sign-in at MatIdentity and comes back to <see cref="ReturnUrl"/>.</summary>
    public string IdentitySignInUrl => string.IsNullOrWhiteSpace(ReturnUrl) ? "/Account/Login" : "/Account/Login?returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    /// <summary>The address of the local form, with the same <see cref="ReturnUrl"/>.</summary>
    public string LocalSignInUrl => string.IsNullOrWhiteSpace(ReturnUrl) ? "/Account/Login?local=1" : "/Account/Login?local=1&returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    public async Task<IActionResult> OnGetAsync()
    {
        Prepare();
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(Landing());
        }

        // Auto login: straight on to MatIdentity, no local form in the way.
        if (IdentityEnabled && !Local && !SignedOut && !SsoError)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = Landing() });
                return new EmptyResult();
            }
            catch (InvalidOperationException ex)
            {
                // MatIdentity cannot be reached (its configuration cannot be read): the local form is the way in.
                logger.LogWarning(ex, "MatIdentity is not reachable, showing the local sign-in.");
                Local = true;
                SsoError = true;
            }
        }

        if (PendingLink is not null && string.IsNullOrEmpty(Input.Email))
        {
            Input.Email = PendingLink.Email ?? PendingLink.PreferredUserName ?? "";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // A posted form is the local sign-in; on an error it shows the form again.
        Prepare();
        Local = true;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signIn.CheckPasswordAsync(Input.Email, Input.Password, HttpContext.RequestAborted);
        switch (result.Outcome)
        {
            case SignInOutcome.SecondFactor:
                // The pending link of MatIdentity waits for the second factor (TwoFactor takes it).
                ticket.Issue(HttpContext, result.User!.Id, ReturnUrl);
                return RedirectToPage("/Account/TwoFactor");

            case SignInOutcome.Success:
                await CompleteAsync(HttpContext, result.User!, users, signIn, log, "Anmeldung");
                return LocalRedirect(Landing());

            case SignInOutcome.LockedOut:
                ModelState.AddModelError(string.Empty, "Zu viele Fehlversuche. Das Konto ist für einige Minuten gesperrt.");
                return Page();

            case SignInOutcome.Inactive:
                ModelState.AddModelError(string.Empty, "Dieses Konto ist deaktiviert. Bitte wende dich an die Administration.");
                return Page();

            default:
                ModelState.AddModelError(string.Empty, "Anmeldename oder Passwort stimmen nicht.");
                return Page();
        }
    }

    /// <summary>Cancel on <c>?link=1</c>: the account is not linked, the pending link is deleted.</summary>
    public IActionResult OnPostCancelLink()
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return LocalRedirect(LocalSignInUrl);
    }

    /// <summary>
    /// Opens the session of a local sign-in that passed every step. A pending link of MatIdentity is only ever for the account that matched (and is deleted
    /// either way): that account gets the subject, the role of the claim and the ID token with its session.
    /// </summary>
    public static async Task CompleteAsync(HttpContext http, Data.Entities.User user, UserService users, SignInService signIn, ActivityLogger log, string action)
    {
        string? idToken = null;
        if (MatIdentityPendingLink.TakeFor(http, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } pending
            && await users.LinkExternalAsync(user.Id, SmtpIdentityUsers.ToIdentity(pending), http.RequestAborted) is { } linked)
        {
            user = linked;
            idToken = pending.IdToken;
            await log.LogAsync("Mit MatIdentity verbunden", "User", user.Id, user.Email);
        }

        await signIn.StartSessionAsync(http, user, http.RequestAborted, idToken);
        await log.LogAsync(action, "User", user.Id, user.Email);
    }

    private void Prepare()
    {
        ViewData["Title"] = "Anmelden";
        ViewData["ClearKept"] = true;
        Local = QueryFlag("local");
        SignedOut = QueryFlag("signedOut");
        SsoError = QueryFlag("ssoError");
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        LinkExpired = PendingLink is null && QueryFlag("link");
    }

    /// <summary>A flag in the query: <c>1</c> or <c>true</c>.</summary>
    private bool QueryFlag(string name) => Request.Query[name].ToString() is "1" or "true" or "True";

    private string Landing() => SafeReturn(ReturnUrl, "/");
}
