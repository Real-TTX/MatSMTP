using System.ComponentModel.DataAnnotations;
using MatSmtp.Core.Relay;
using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.RelayUsers;

/// <summary>One relay user: name, password (hashed, or generated and shown once), the senders it may use, enabled or not.</summary>
public sealed class EditModel(MsDbContext db, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Benutzernamen ein.")]
        [RegularExpression(@"^[A-Za-z0-9._@+\-]{2,100}$", ErrorMessage = "2 bis 100 Zeichen: Buchstaben, Ziffern und . _ @ + -")]
        public string UserName { get; set; } = "";

        [StringLength(300)]
        public string? Description { get; set; }

        public string? Password { get; set; }

        public string? AllowedSenders { get; set; }

        public bool IsEnabled { get; set; } = true;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public RelayUser? Item { get; private set; }

    public int RuleCount { get; private set; }

    /// <summary>A generated password, shown once after the save.</summary>
    public string? Generated { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Item is { } u)
        {
            Input = new InputModel { UserName = u.UserName, Description = u.Description, AllowedSenders = u.AllowedSenders, IsEnabled = u.IsEnabled };
        }

        Generated = TempData["Generated"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(bool generate = false)
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var name = Input.UserName.Trim();
        if (ModelState.IsValid && await db.RelayUser.AnyAsync(u => u.UserName == name && u.Id != Id))
        {
            ModelState.AddModelError("Input.UserName", "Diesen Benutzernamen gibt es schon.");
        }

        if (AddressPattern.Problem(Input.AllowedSenders) is { } problem)
        {
            ModelState.AddModelError("Input.AllowedSenders", problem);
        }

        var password = generate ? PasswordHasher.RandomCode(24) : Input.Password;
        if (!generate && !string.IsNullOrEmpty(password) && password.Length < 12)
        {
            ModelState.AddModelError("Input.Password", "Mindestens 12 Zeichen. Oder lass dir eines erzeugen.");
        }

        if (Item is null && string.IsNullOrEmpty(password))
        {
            ModelState.AddModelError("Input.Password", "Bitte vergib ein Passwort, oder lass dir eines erzeugen.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = Item is null ? new RelayUser() : await db.RelayUser.FirstAsync(u => u.Id == Item.Id);
        user.UserName = name;
        user.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        user.AllowedSenders = string.IsNullOrWhiteSpace(Input.AllowedSenders) ? null : string.Join("\n", AddressPattern.Split(Input.AllowedSenders));
        user.IsEnabled = Input.IsEnabled;
        if (!string.IsNullOrEmpty(password))
        {
            user.PasswordHash = PasswordHasher.Hash(password);
        }

        if (Item is null)
        {
            db.RelayUser.Add(user);
        }

        await db.SaveChangesAsync();
        await log.LogAsync(Item is null ? "Zugang angelegt" : "Zugang geändert", nameof(RelayUser), user.Id, user.UserName + (string.IsNullOrEmpty(password) ? "" : ", neues Passwort"));
        if (generate)
        {
            TempData["Generated"] = password;
        }

        Flash("Der Zugang ist gespeichert.");
        return RedirectToPage(new { id = user.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var user = await db.RelayUser.FirstOrDefaultAsync(u => u.Id == Id);
        if (user is null)
        {
            return RedirectToPage("/RelayUsers/Index");
        }

        db.RelayUser.Remove(user);
        await db.SaveChangesAsync();
        await log.LogAsync("Zugang gelöscht", nameof(RelayUser), user.Id, user.UserName);
        Flash("Der Zugang ist gelöscht. Anwendungen können sich damit nicht mehr anmelden.");
        return RedirectToPage("/RelayUsers/Index");
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Item = await db.RelayUser.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
            if (Item is null)
            {
                return false;
            }

            RuleCount = await db.Rule.CountAsync(r => r.MatchRelayUserId == id);
        }

        var title = Item?.UserName ?? "Neuer Zugang";
        Head(title, null, ("Relay", null), ("Zugänge", "/RelayUsers"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/RelayUsers";
        return true;
    }
}
