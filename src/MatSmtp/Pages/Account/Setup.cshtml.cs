using System.ComponentModel.DataAnnotations;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Account;

/// <summary>The first start: creates the first administrator. The page exists only while there is none.</summary>
[AllowAnonymous]
public sealed class SetupModel(MsDbContext db, UserService users, SignInService signIn, SetupState setup, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib deinen Namen ein.")]
        [Display(Name = "Name")]
        public string DisplayName { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib eine E-Mail-Adresse ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        [Display(Name = "E-Mail-Adresse")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Bitte wähle ein Passwort.")]
        [Display(Name = "Passwort")]
        public string Password { get; set; } = "";

        [Required(ErrorMessage = "Bitte wiederhole das Passwort.")]
        [Display(Name = "Passwort wiederholen")]
        public string PasswordRepeat { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        ViewData["Title"] = "Einrichtung";
        return await db.User.AnyAsync(u => u.Role == UserRole.Admin) ? RedirectToPage("/Account/Login") : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = "Einrichtung";
        if (await db.User.AnyAsync(u => u.Role == UserRole.Admin))
        {
            return RedirectToPage("/Account/Login");
        }

        if (ModelState.IsValid)
        {
            if (CheckedPassword() is { } problem)
            {
                ModelState.AddModelError("Input.Password", problem);
            }
            else if (Input.Password != Input.PasswordRepeat)
            {
                ModelState.AddModelError("Input.PasswordRepeat", "Die Passwörter stimmen nicht überein.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var admin = await users.CreateAsync(Input.Email, Input.DisplayName, UserRole.Admin, Input.Password);
        setup.MarkDone();
        await signIn.StartSessionAsync(HttpContext, admin);
        await log.LogAsync("Einrichtung", "User", admin.Id, "Erster Administrator angelegt");
        Flash("Willkommen! Lege als Nächstes das SMTP-Konto an, über das MatSMTP versendet.");
        return RedirectToPage("/Accounts/Edit");
    }

    private string? CheckedPassword() => UserService.CheckPassword(Input.Password, Input.Email);
}
