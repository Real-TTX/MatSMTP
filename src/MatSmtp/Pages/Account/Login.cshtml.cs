using System.ComponentModel.DataAnnotations;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel(SignInService signIn, TwoFactorTicket ticket, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib deine E-Mail-Adresse ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
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

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(Landing());
        }

        ViewData["Title"] = "Anmelden";
        ViewData["ClearKept"] = true;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = "Anmelden";
        ViewData["ClearKept"] = true;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signIn.CheckPasswordAsync(Input.Email, Input.Password, HttpContext.RequestAborted);
        switch (result.Outcome)
        {
            case SignInOutcome.SecondFactor:
                ticket.Issue(HttpContext, result.User!.Id, ReturnUrl);
                return RedirectToPage("/Account/TwoFactor");

            case SignInOutcome.Success:
                await signIn.StartSessionAsync(HttpContext, result.User!, HttpContext.RequestAborted);
                await log.LogAsync("Anmeldung", "User", result.User!.Id, result.User.Email);
                return LocalRedirect(Landing());

            case SignInOutcome.LockedOut:
                ModelState.AddModelError(string.Empty, "Zu viele Fehlversuche. Das Konto ist für einige Minuten gesperrt.");
                return Page();

            case SignInOutcome.Inactive:
                ModelState.AddModelError(string.Empty, "Dieses Konto ist deaktiviert. Bitte wende dich an die Administration.");
                return Page();

            default:
                ModelState.AddModelError(string.Empty, "E-Mail-Adresse oder Passwort stimmen nicht.");
                return Page();
        }
    }

    private string Landing() => SafeReturn(ReturnUrl, "/");
}
