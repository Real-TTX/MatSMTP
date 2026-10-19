using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MatSmtp.Core.Security;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Account;

public sealed class ProfileModel(ICurrentUser current, UserService users, SignInService signIn, ActivityLogger log) : PageModelBase
{
    public sealed class ProfileInput
    {
        [Required(ErrorMessage = "Bitte gib deinen Namen ein.")]
        [Display(Name = "Name")]
        public string DisplayName { get; set; } = "";

        [Display(Name = "Telefon")]
        public string? Phone { get; set; }

        [Display(Name = "Darstellung")]
        public string Theme { get; set; } = "system";
    }

    public sealed class PasswordInput
    {
        [Display(Name = "Aktuelles Passwort")]
        public string? Current { get; set; }

        [Required(ErrorMessage = "Bitte gib das neue Passwort ein.")]
        [Display(Name = "Neues Passwort")]
        public string New { get; set; } = "";

        [Required(ErrorMessage = "Bitte wiederhole das neue Passwort.")]
        [Display(Name = "Neues Passwort wiederholen")]
        public string Repeat { get; set; } = "";
    }

    [BindProperty]
    public ProfileInput Profile { get; set; } = new();

    [BindProperty]
    public PasswordInput Password { get; set; } = new();

    public string Email { get; private set; } = "";

    public bool HasPassword { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await LoadAsync();
        Profile = new ProfileInput { DisplayName = user.DisplayName, Phone = user.Phone, Theme = user.Theme };
        Prepare(user);
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        var user = await LoadAsync();
        ModelState.Remove("Password.New");
        ModelState.Remove("Password.Repeat");
        if (!ModelState.IsValid)
        {
            Prepare(user);
            return Page();
        }

        await users.UpdateProfileAsync(user, Profile.DisplayName, Profile.Phone, Profile.Theme, HttpContext.RequestAborted);
        await signIn.RefreshAsync(HttpContext, user);
        Flash("Dein Profil ist gespeichert.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        var user = await LoadAsync();
        ModelState.Remove("Profile.DisplayName");
        if (user.PasswordHash is not null && !PasswordHasher.Verify(Password.Current ?? string.Empty, user.PasswordHash))
        {
            ModelState.AddModelError("Password.Current", "Das aktuelle Passwort stimmt nicht.");
        }

        if (UserService.CheckPassword(Password.New, user.Email) is { } problem)
        {
            ModelState.AddModelError("Password.New", problem);
        }
        else if (Password.New != Password.Repeat)
        {
            ModelState.AddModelError("Password.Repeat", "Die Passwörter stimmen nicht überein.");
        }

        if (!ModelState.IsValid)
        {
            Profile = new ProfileInput { DisplayName = user.DisplayName, Phone = user.Phone, Theme = user.Theme };
            Prepare(user);
            return Page();
        }

        var keep = Guid.TryParse(User.FindFirstValue(AppClaims.Session), out var token) ? token : (Guid?)null;
        await users.ChangePasswordAsync(user, Password.New, keep);
        await log.LogAsync("Passwort geändert", "User", user.Id);
        Flash("Das Passwort ist geändert. Andere Geräte sind abgemeldet.");
        return RedirectToPage();
    }

    private async Task<User> LoadAsync()
        => await users.FindForChangeAsync(current.UserId!.Value, HttpContext.RequestAborted) ?? throw new InvalidOperationException("The signed-in user is gone.");

    private void Prepare(User user)
    {
        Email = user.Email;
        HasPassword = user.PasswordHash is not null;
        Head("Mein Konto", "Dein Name, deine Darstellung und dein Passwort.", ("Mein Konto", null));
    }
}
