using System.ComponentModel.DataAnnotations;
using MatSmtp.Configuration;
using MatSmtp.Core.Security;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Webhooks;

/// <summary>One webhook: address, what it gets, the secret of the signature, extra headers; a test call.</summary>
public sealed class EditModel(WebhookAdminService hooks, SecretProtector secrets, WebhookService webhooks, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        [StringLength(100)]
        public string Name { get; set; } = "";

        [StringLength(300)]
        public string? Description { get; set; }

        [Required(ErrorMessage = "Bitte gib die Adresse ein.")]
        [Url(ErrorMessage = "Das ist keine Adresse (http:// oder https://).")]
        public string Url { get; set; } = "";

        public WebhookPayload Payload { get; set; } = WebhookPayload.Metadata;

        public bool Sign { get; set; }

        public string? Secret { get; set; }

        public string? Headers { get; set; }

        [Range(1, 120, ErrorMessage = "1 bis 120 Sekunden.")]
        public int TimeoutSeconds { get; set; } = 15;

        [Range(1, 20, ErrorMessage = "1 bis 20 Versuche.")]
        public int MaxAttempts { get; set; } = 5;

        public bool IsEnabled { get; set; } = true;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Webhook? Item { get; private set; }

    public bool HasSecret => !string.IsNullOrEmpty(Item?.SecretProtected);

    public string? Result { get; private set; }

    public bool ResultOk { get; private set; }

    public int RuleCount { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Item is { } w)
        {
            Input = new InputModel
            {
                Name = w.Name, Description = w.Description, Url = w.Url, Payload = w.Payload, Sign = HasSecret, Headers = w.Headers, TimeoutSeconds = w.TimeoutSeconds,
                MaxAttempts = w.MaxAttempts, IsEnabled = w.IsEnabled,
            };
        }

        if (!HasSecret)
        {
            // A secret ready to take: the receiving side needs the same one, so it is shown in clear text until it is saved.
            Input.Secret = NewSecret();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var hook = await hooks.SaveAsync(Item?.Id, Apply, HttpContext.RequestAborted);
        await log.LogAsync(Item is null ? "Webhook angelegt" : "Webhook geändert", nameof(Webhook), hook.Id, $"{hook.Name} ({hook.Url})");
        Flash("Der Webhook ist gespeichert.");
        return RedirectToPage(new { id = hook.Id });
    }

    /// <summary>Calls the address with a sample message and what the form holds (unsaved changes too).</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var probe = new Webhook();
        Apply(probe);
        var result = await webhooks.TestAsync(probe, HttpContext.RequestAborted);
        ResultOk = result.Ok;
        Result = result.Ok ? $"Die Adresse hat geantwortet: {result.Text} in {result.Milliseconds} ms." : $"Der Aufruf ist fehlgeschlagen: {result.Text}";
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var hook = Id is { } id ? await hooks.DeleteAsync(id, HttpContext.RequestAborted) : null;
        if (hook is null)
        {
            return RedirectToPage("/Webhooks/Index");
        }

        await log.LogAsync("Webhook gelöscht", nameof(Webhook), hook.Id, hook.Name);
        Flash("Der Webhook ist gelöscht.");
        return RedirectToPage("/Webhooks/Index");
    }

    private void Validate()
    {
        if (!string.IsNullOrWhiteSpace(Input.Url) && !Input.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !Input.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Input.Url", "Die Adresse muss mit http:// oder https:// beginnen.");
        }

        foreach (var line in (Input.Headers ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.IndexOf(':') <= 0)
            {
                ModelState.AddModelError("Input.Headers", $"„{line}“ ist keine Kopfzeile. Schreibe Name: Wert, eine pro Zeile.");
                break;
            }
        }

        if (Input.Sign && (string.IsNullOrEmpty(Input.Secret) ? !HasSecret : Input.Secret.Length < 16))
        {
            ModelState.AddModelError("Input.Secret", "Bitte gib ein Geheimnis ein (mindestens 16 Zeichen).");
        }
    }

    private void Apply(Webhook w)
    {
        w.Name = Input.Name.Trim();
        w.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        w.Url = Input.Url.Trim();
        w.Payload = Input.Payload;
        w.Headers = string.IsNullOrWhiteSpace(Input.Headers) ? null : Input.Headers.Trim();
        w.TimeoutSeconds = Input.TimeoutSeconds;
        w.MaxAttempts = Input.MaxAttempts;
        w.IsEnabled = Input.IsEnabled;
        if (!Input.Sign)
        {
            w.SecretProtected = null;
        }
        else if (!string.IsNullOrEmpty(Input.Secret))
        {
            w.SecretProtected = secrets.Protect(Input.Secret);
        }
        else if (Item is not null && w.SecretProtected is null)
        {
            w.SecretProtected = Item.SecretProtected;
        }
    }

    /// <summary>Generates a secret for the signature and shows it (it is needed on the receiving side).</summary>
    public string NewSecret() => PasswordHasher.RandomCode(32);

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Item = await hooks.FindAsync(id, HttpContext.RequestAborted);
            if (Item is null)
            {
                return false;
            }

            RuleCount = await hooks.CountRulesAsync(id, HttpContext.RequestAborted);
        }

        var title = Item?.Name ?? "Neuer Webhook";
        Head(title, null, ("Relay", null), ("Webhooks", "/Webhooks"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Webhooks";
        return true;
    }
}
