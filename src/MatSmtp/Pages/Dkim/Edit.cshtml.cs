using System.ComponentModel.DataAnnotations;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using Microsoft.AspNetCore.Mvc;
using DkimRules = MatSmtp.Core.Relay.Dkim;

namespace MatSmtp.Pages.Dkim;

/// <summary>Where the key of a new DKIM entry comes from.</summary>
public enum DkimKeySource
{
    Rsa2048,
    Rsa1024,
    Import,
}

/// <summary>One DKIM key: domain, selector, a new or imported key; the TXT record to publish and a look into the DNS whether it is there.</summary>
public sealed class EditModel(DkimService dkim, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib die Domain ein.")]
        [StringLength(253)]
        public string Domain { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib einen Selektor ein.")]
        [StringLength(63)]
        public string Selector { get; set; } = "";

        [StringLength(300)]
        public string? Description { get; set; }

        public bool IncludeSubdomains { get; set; }

        public bool IsEnabled { get; set; }

        public DkimKeySource Source { get; set; } = DkimKeySource.Rsa2048;

        public string? PrivateKey { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public DkimKey? Item { get; private set; }

    /// <summary>The name of the TXT record of the saved key.</summary>
    public string RecordName => Item is null ? "" : DkimRules.RecordName(Item.Selector, Item.Domain);

    /// <summary>The value of the TXT record of the saved key.</summary>
    public string RecordValue => Item is null ? "" : DkimRules.RecordValue(Item.PublicKey);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Input = Item is { } k
            ? new InputModel { Domain = k.Domain, Selector = k.Selector, Description = k.Description, IncludeSubdomains = k.IncludeSubdomains, IsEnabled = k.IsEnabled }
            : new InputModel { Selector = dkim.SuggestSelector() };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var newKey = await ValidateAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var key = await dkim.SaveAsync(Item?.Id, Apply, newKey, HttpContext.RequestAborted);
        await log.LogAsync(Item is null ? "DKIM-Schlüssel angelegt" : "DKIM-Schlüssel geändert", nameof(DkimKey), key.Id, $"{key.Selector}._domainkey.{key.Domain}{(key.IsEnabled ? "" : " (abgeschaltet)")}");
        Flash(Item is null ? "Der Schlüssel ist angelegt. Trage jetzt den DNS-Eintrag ein, prüfe ihn und schalte die Signatur ein." : "Der Schlüssel ist gespeichert.");
        return RedirectToPage(new { id = key.Id });
    }

    /// <summary>Looks the record up in the DNS (the saved key, not what the form holds).</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        var key = Id is { } id ? await dkim.CheckDnsAsync(id, HttpContext.RequestAborted) : null;
        if (key is null)
        {
            return NotFound();
        }

        Flash(key.DnsState == Core.Relay.DkimDnsState.Ok ? "Der DNS-Eintrag stimmt." : "Der DNS-Eintrag stimmt noch nicht: " + key.DnsDetail);
        return RedirectToPage(new { id = key.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var key = Id is { } id ? await dkim.DeleteAsync(id, HttpContext.RequestAborted) : null;
        if (key is null)
        {
            return RedirectToPage("/Dkim/Index");
        }

        await log.LogAsync("DKIM-Schlüssel gelöscht", nameof(DkimKey), key.Id, $"{key.Selector}._domainkey.{key.Domain}");
        Flash("Der Schlüssel ist gelöscht. Den DNS-Eintrag kannst du entfernen, sobald keine signierte Nachricht mehr unterwegs ist.");
        return RedirectToPage("/Dkim/Index");
    }

    /// <summary>Checks the form; for a new entry also makes or reads the key (null for a change: the key stays).</summary>
    private async Task<(string PrivateKeyPem, string PublicKey, int Bits)?> ValidateAsync()
    {
        var domain = DkimRules.NormalizeDomain(Input.Domain);
        if (!string.IsNullOrWhiteSpace(Input.Domain) && domain is null)
        {
            ModelState.AddModelError("Input.Domain", "Das ist keine Domain. Schreibe sie ohne @, zum Beispiel firma.de.");
        }

        var selector = Input.Selector?.Trim() ?? "";
        if (selector.Length > 0 && !DkimRules.IsSelector(selector))
        {
            ModelState.AddModelError("Input.Selector", "Ein Selektor besteht aus Buchstaben, Ziffern und Bindestrichen, zum Beispiel matsmtp2026.");
        }
        else if (domain is not null && selector.Length > 0 && await dkim.SelectorTakenAsync(domain, selector, Item?.Id, HttpContext.RequestAborted))
        {
            ModelState.AddModelError("Input.Selector", "Diese Domain hat schon einen Schlüssel mit diesem Selektor. Nimm einen anderen Namen.");
        }

        if (Item is not null)
        {
            return null;
        }

        switch (Input.Source)
        {
            case DkimKeySource.Import when string.IsNullOrWhiteSpace(Input.PrivateKey):
                ModelState.AddModelError("Input.PrivateKey", "Bitte füge den privaten Schlüssel ein.");
                return null;
            case DkimKeySource.Import:
                var imported = DkimRules.Import(Input.PrivateKey!.Trim());
                if (imported is null)
                {
                    ModelState.AddModelError("Input.PrivateKey", "Das ist kein privater RSA-Schlüssel im PEM-Format (-----BEGIN RSA PRIVATE KEY----- oder -----BEGIN PRIVATE KEY-----).");
                    return null;
                }

                if (imported.Value.Bits < 1024)
                {
                    ModelState.AddModelError("Input.PrivateKey", $"Der Schlüssel hat nur {imported.Value.Bits} Bit; Empfänger verlangen mindestens 1024.");
                }

                return imported;
            default:
                var bits = Input.Source == DkimKeySource.Rsa1024 ? 1024 : 2048;
                var (pem, publicKey) = DkimRules.Generate(bits);
                return (pem, publicKey, bits);
        }
    }

    private void Apply(DkimKey k)
    {
        k.Domain = DkimRules.NormalizeDomain(Input.Domain)!;
        k.Selector = Input.Selector.Trim();
        k.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        k.IncludeSubdomains = Input.IncludeSubdomains;
        k.IsEnabled = Input.IsEnabled;
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Item = await dkim.FindAsync(id, HttpContext.RequestAborted);
            if (Item is null)
            {
                return false;
            }
        }

        var title = Item is null ? "Neuer DKIM-Schlüssel" : Item.Domain;
        Head(title, null, ("Relay", null), ("DKIM", "/Dkim"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Dkim";
        return true;
    }
}
