using System.ComponentModel.DataAnnotations;
using MatSmtp.Configuration;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Accounts;

/// <summary>One SMTP account: server, sign-in, a forced sender, the limits, default or not; a check of the connection and a test message.</summary>
public sealed class EditModel(AccountService accounts, SecretProtector secrets, DeliveryService delivery, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        [StringLength(100)]
        public string Name { get; set; } = "";

        [StringLength(300)]
        public string? Description { get; set; }

        [Required(ErrorMessage = "Bitte gib den Server ein.")]
        [StringLength(253)]
        public string Host { get; set; } = "";

        [Range(1, 65535, ErrorMessage = "Ein Port zwischen 1 und 65535.")]
        public int Port { get; set; } = 587;

        public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

        public bool UseAuth { get; set; } = true;

        public string? UserName { get; set; }

        public string? Password { get; set; }

        [EmailAddress(ErrorMessage = "Das ist keine E-Mail-Adresse.")]
        public string? ForceFromAddress { get; set; }

        [Range(0, 100000, ErrorMessage = "0 (keine Grenze) bis 100000.")]
        public int MaxPerMinute { get; set; }

        [Range(0, 10000000, ErrorMessage = "0 (keine Grenze) bis 10000000.")]
        public int MaxPerDay { get; set; }

        [Range(5, 300, ErrorMessage = "5 bis 300 Sekunden.")]
        public int TimeoutSeconds { get; set; } = 60;

        public bool IsDefault { get; set; }

        public bool IsEnabled { get; set; } = true;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? TestTo { get; set; }

    public SmtpAccount? Account { get; private set; }

    public bool HasPassword => !string.IsNullOrEmpty(Account?.PasswordProtected);

    public string? Result { get; private set; }

    public bool ResultOk { get; private set; }

    public int RuleCount { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Account is { } a)
        {
            Input = new InputModel
            {
                Name = a.Name, Description = a.Description, Host = a.Host, Port = a.Port, Security = a.Security, UseAuth = !string.IsNullOrEmpty(a.UserName), UserName = a.UserName,
                ForceFromAddress = a.ForceFromAddress, MaxPerMinute = a.MaxPerMinute, MaxPerDay = a.MaxPerDay, TimeoutSeconds = a.TimeoutSeconds, IsDefault = a.IsDefault, IsEnabled = a.IsEnabled,
            };
        }
        else
        {
            Input.IsDefault = !await accounts.AnyAsync(HttpContext.RequestAborted);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Input.UseAuth && string.IsNullOrWhiteSpace(Input.UserName))
        {
            ModelState.AddModelError("Input.UserName", "Bitte gib den Benutzernamen ein, oder schalte die Anmeldung ab.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var account = await accounts.SaveAsync(Account?.Id, Apply, HttpContext.RequestAborted);
        await log.LogAsync(Account is null ? "SMTP-Konto angelegt" : "SMTP-Konto geändert", nameof(SmtpAccount), account.Id, $"{account.Name} ({account.Host}:{account.Port})");
        Flash("Das Konto ist gespeichert.");
        return RedirectToPage(new { id = account.Id });
    }

    /// <summary>Connects and signs in with what the form holds (an empty password keeps the stored one), without saving.</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        ModelState.Clear();
        var probe = new SmtpAccount();
        Apply(probe);
        var outcome = await delivery.CheckAsync(probe, HttpContext.RequestAborted);
        Result = outcome.Text;
        ResultOk = outcome.Ok;
        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        ModelState.Clear();
        if (string.IsNullOrWhiteSpace(TestTo) || !new EmailAddressAttribute().IsValid(TestTo))
        {
            ModelState.AddModelError(nameof(TestTo), "Bitte gib eine E-Mail-Adresse ein.");
            return Page();
        }

        var probe = new SmtpAccount();
        Apply(probe);
        var outcome = await delivery.SendTestAsync(probe, TestTo.Trim(), HttpContext.RequestAborted);
        Result = outcome.Ok ? $"Die Testnachricht an {TestTo.Trim()} ist angenommen: {outcome.Text}" : outcome.Text;
        ResultOk = outcome.Ok;
        await log.LogAsync(outcome.Ok ? "Testnachricht versendet" : "Testnachricht fehlgeschlagen", nameof(SmtpAccount), Account?.Id, $"{probe.Name} an {TestTo.Trim()}");
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var account = Id is { } id ? await accounts.DeleteAsync(id, HttpContext.RequestAborted) : null;
        if (account is null)
        {
            return RedirectToPage("/Accounts/Index");
        }

        await log.LogAsync("SMTP-Konto gelöscht", nameof(SmtpAccount), account.Id, account.Name);
        Flash("Das Konto ist gelöscht. Regeln, die es nannten, senden jetzt über das Standardkonto.");
        return RedirectToPage("/Accounts/Index");
    }

    private void Apply(SmtpAccount a)
    {
        a.Name = Input.Name.Trim();
        a.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        a.Host = Input.Host.Trim();
        a.Port = Input.Port;
        a.Security = Input.Security;
        a.UserName = Input.UseAuth && !string.IsNullOrWhiteSpace(Input.UserName) ? Input.UserName.Trim() : null;
        if (!Input.UseAuth)
        {
            a.PasswordProtected = null;
        }
        else if (!string.IsNullOrEmpty(Input.Password))
        {
            a.PasswordProtected = secrets.Protect(Input.Password);
        }
        else if (Account is not null && a.PasswordProtected is null)
        {
            // a probe that is not the stored account: the stored password
            a.PasswordProtected = Account.PasswordProtected;
        }

        a.ForceFromAddress = string.IsNullOrWhiteSpace(Input.ForceFromAddress) ? null : Input.ForceFromAddress.Trim();
        a.MaxPerMinute = Input.MaxPerMinute;
        a.MaxPerDay = Input.MaxPerDay;
        a.TimeoutSeconds = Input.TimeoutSeconds;
        a.IsDefault = Input.IsDefault;
        a.IsEnabled = Input.IsEnabled;
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Account = await accounts.FindAsync(id, HttpContext.RequestAborted);
            if (Account is null)
            {
                return false;
            }

            RuleCount = await accounts.CountRulesAsync(id, HttpContext.RequestAborted);
        }

        var title = Account?.Name ?? "Neues SMTP-Konto";
        Head(title, null, ("Relay", null), ("SMTP-Konten", "/Accounts"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Accounts";
        return true;
    }
}
