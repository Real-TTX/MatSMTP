using System.ComponentModel.DataAnnotations;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatSmtp.Pages.Admin.Users;

/// <summary>One account: name, address, job, department, role, whether it may sign in, a new password, the second factor and the sessions.</summary>
public sealed class EditModel(UserService users, SignInService signIn, TwoFactorService twoFactor, ICurrentUser current, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib den Namen ein.")]
        [Display(Name = "Name")]
        public string DisplayName { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib die E-Mail-Adresse ein.")]
        [Display(Name = "E-Mail-Adresse")]
        public string Email { get; set; } = "";

        [Display(Name = "Telefon")]
        public string? Phone { get; set; }

        [Display(Name = "Rolle")]
        public UserRole Role { get; set; } = UserRole.User;

        public bool IsActive { get; set; } = true;

        [Display(Name = "Passwort")]
        public string? Password { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public User? Account { get; private set; }

    public bool IsMe => Account is not null && Account.Id == current.UserId;

    public bool HasTotp => Account?.Totp is { IsConfirmed: true };

    public bool IsLocked => Account is { } a && a.LockoutEndDate > clock.GetUtcNow().UtcDateTime;

    public int Sessions { get; private set; }


    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Account is { } a)
        {
            Input = new InputModel { DisplayName = a.DisplayName, Email = a.Email, Phone = a.Phone, Role = a.Role, IsActive = a.IsActive };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var email = SignInService.NormalizeEmail(Input.Email);

        // An address, or the name the account already has (the administrator "admin" of a headless installation keeps it).
        if (ModelState.IsValid && !new EmailAddressAttribute().IsValid(email) && email != SignInService.NormalizeEmail(Account?.Email))
        {
            ModelState.AddModelError("Input.Email", "Das ist keine gültige E-Mail-Adresse.");
        }

        if (ModelState.IsValid && await users.EmailTakenAsync(email, Id))
        {
            ModelState.AddModelError("Input.Email", "Zu dieser E-Mail-Adresse gibt es schon ein Konto.");
        }

        if (!string.IsNullOrEmpty(Input.Password) && UserService.CheckPassword(Input.Password, email) is { } weak)
        {
            ModelState.AddModelError("Input.Password", weak);
        }

        if (Id is null && string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Bitte vergib ein Passwort. ");
        }

        if (Account is { Role: UserRole.Admin } && (Input.Role != UserRole.Admin || !Input.IsActive) && !await OtherAdminExistsAsync())
        {
            ModelState.AddModelError("Input.Role", "Das ist der einzige aktive Administrator. Es muss immer einen geben.");
        }

        if (IsMe && (!Input.IsActive || Input.Role != UserRole.Admin))
        {
            ModelState.AddModelError("Input.Role", "Du kannst dir nicht selbst die Rechte nehmen oder dich sperren.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Account is null)
        {
            var created = await users.CreateAsync(email, Input.DisplayName.Trim(), Input.Role, Input.Password!, Input.Phone, Input.IsActive, HttpContext.RequestAborted);
            await log.LogAsync("Benutzer angelegt", "User", created.Id, $"{created.Email} ({Labels.Of(created.Role)})");
            Flash("Der Benutzer ist angelegt.");
            return RedirectToPage("/Admin/Users/Edit", new { id = created.Id });
        }

        var roleChanged = Account.Role != Input.Role;
        var user = await users.UpdateAsync(Account.Id, Input.DisplayName, email, Input.Phone, Input.Role, HttpContext.RequestAborted);
        if (user is null)
        {
            return NotFound();
        }

        if (user.IsActive != Input.IsActive)
        {
            await users.SetActiveAsync(user, Input.IsActive);
        }

        if (!string.IsNullOrEmpty(Input.Password))
        {
            await users.ChangePasswordAsync(user, Input.Password);
        }

        await log.LogAsync("Benutzer geändert", "User", user.Id, roleChanged ? $"{user.Email}, neue Rolle: {Labels.Of(user.Role)}" : user.Email);
        Flash("Der Benutzer ist gespeichert.");
        return RedirectToPage("/Admin/Users/Edit", new { id = user.Id });
    }

    public async Task<IActionResult> OnPostResetTotpAsync()
    {
        var user = Id is { } id ? await users.FindForChangeAsync(id, HttpContext.RequestAborted) : null;
        if (user is null)
        {
            return NotFound();
        }

        await twoFactor.DisableAsync(user);
        await log.LogAsync("Zwei-Faktor-Anmeldung zurückgesetzt", "User", user.Id, user.Email);
        Flash("Die Zwei-Faktor-Anmeldung ist abgeschaltet. Die Person kann sie neu einrichten.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostEndSessionsAsync()
    {
        if (Id is not { } id)
        {
            return NotFound();
        }

        // An administrator who ends their own sessions would lock themselves out in the middle of the click; keep the one in use.
        var keep = id == current.UserId ? Guid.TryParse(User.FindFirst(AppClaims.Session)?.Value, out var token) ? token : (Guid?)null : null;
        await signIn.EndAllSessionsAsync(id, keep);
        await log.LogAsync("Sitzungen beendet", "User", id);
        Flash("Alle Sitzungen sind beendet. Die Person muss sich neu anmelden.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnlockAsync()
    {
        if (Id is not { } id || !await users.UnlockAsync(id, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash("Die Sperre nach falschen Anmeldungen ist aufgehoben.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var user = Id is { } id ? await users.FindAsync(id, HttpContext.RequestAborted) : null;
        if (user is null)
        {
            return RedirectToPage("/Admin/Users/Index");
        }

        if (user.Id == current.UserId)
        {
            FlashError("Du kannst dich nicht selbst löschen.");
            return RedirectToPage(new { id = Id });
        }

        if (user.Role == UserRole.Admin && user.IsActive && !await users.OtherActiveAdminExistsAsync(user.Id, HttpContext.RequestAborted))
        {
            FlashError("Das ist der einzige aktive Administrator.");
            return RedirectToPage(new { id = Id });
        }

        await users.DeleteAsync(user.Id, HttpContext.RequestAborted);
        await log.LogAsync("Benutzer gelöscht", "User", user.Id, user.Email);
        Flash("Der Benutzer ist gelöscht. Seine Einträge bleiben erhalten.");
        return RedirectToPage("/Admin/Users/Index");
    }

    private Task<bool> OtherAdminExistsAsync() => users.OtherActiveAdminExistsAsync(Id, HttpContext.RequestAborted);

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Account = await users.FindAsync(id, HttpContext.RequestAborted);
            if (Account is null)
            {
                return false;
            }

            Sessions = await signIn.CountActiveSessionsAsync(id, HttpContext.RequestAborted);
        }

        var title = Id is null ? "Neuer Benutzer" : Account!.DisplayName;
        Head(title, null, ("Einstellungen", null), ("Benutzer", "/Admin/Users"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Users";
        return true;
    }
}
