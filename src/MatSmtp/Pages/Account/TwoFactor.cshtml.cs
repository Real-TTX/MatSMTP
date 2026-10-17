using System.ComponentModel.DataAnnotations;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Account;

/// <summary>The second step: the code of the authenticator app (or a recovery code). Only here, and only with the ticket of the first step, a session is opened.</summary>
[AllowAnonymous]
public sealed class TwoFactorModel(MsDbContext db, TwoFactorService twoFactor, SignInService signIn, TwoFactorTicket ticket, ActivityLogger log) : PageModelBase
{
    [BindProperty]
    [Required(ErrorMessage = "Bitte gib den Code ein.")]
    [Display(Name = "Code")]
    public string Code { get; set; } = "";

    public IActionResult OnGet()
    {
        ViewData["Title"] = "Zweiter Faktor";
        return ticket.Read(HttpContext) is null ? RedirectToPage("/Account/Login") : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = "Zweiter Faktor";
        if (ticket.Read(HttpContext) is not var (userId, returnUrl))
        {
            return RedirectToPage("/Account/Login");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await db.User.Include(u => u.Totp).FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (user is null)
        {
            return RedirectToPage("/Account/Login");
        }

        if (!await twoFactor.VerifyAsync(user, Code, HttpContext.RequestAborted))
        {
            ModelState.AddModelError(nameof(Code), signIn.IsLockedOut(user) ? "Zu viele Fehlversuche. Das Konto ist für einige Minuten gesperrt." : "Der Code stimmt nicht.");
            return Page();
        }

        ticket.Clear(HttpContext);
        await signIn.StartSessionAsync(HttpContext, user, HttpContext.RequestAborted);
        await log.LogAsync("Anmeldung mit zweitem Faktor", "User", user.Id, user.Email);
        return LocalRedirect(SafeReturn(returnUrl, "/"));
    }
}
