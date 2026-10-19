using System.ComponentModel.DataAnnotations;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.RelayUsers;

/// <summary>One relay user: name, password (hashed, or generated and shown once), the senders it may use, enabled or not.</summary>
public sealed class EditModel(RelayUserService relayUsers, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        public string? UserName { get; set; } = "";

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

        var password = generate ? RelayUserService.GeneratePassword() : Input.Password;
        // A relay user of the API keeps its name: it is the id the API knows it by.
        var name = Item?.ApiId is not null ? Item.UserName : Input.UserName ?? "";
        Input.UserName = name;
        var input = new RelayUserInput(name, Input.Description, password, Input.AllowedSenders, Input.IsEnabled);
        if (ModelState.IsValid)
        {
            foreach (var (field, error) in await relayUsers.CheckAsync(Item?.Id, input, generate, HttpContext.RequestAborted))
            {
                ModelState.AddModelError("Input." + field, error);
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await relayUsers.SaveAsync(Item?.Id, input, HttpContext.RequestAborted);
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
        var user = Id is { } id ? await relayUsers.DeleteAsync(id, HttpContext.RequestAborted) : null;
        if (user is null)
        {
            return RedirectToPage("/RelayUsers/Index");
        }

        await log.LogAsync("Zugang gelöscht", nameof(RelayUser), user.Id, user.UserName);
        Flash("Der Zugang ist gelöscht. Anwendungen können sich damit nicht mehr anmelden.");
        return RedirectToPage("/RelayUsers/Index");
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Item = await relayUsers.FindAsync(id, HttpContext.RequestAborted);
            if (Item is null)
            {
                return false;
            }

            RuleCount = await relayUsers.CountRulesAsync(id, HttpContext.RequestAborted);
        }

        var title = Item?.UserName ?? "Neuer Zugang";
        Head(title, null, ("Relay", null), ("Zugänge", "/RelayUsers"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/RelayUsers";
        return true;
    }
}
