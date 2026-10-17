using System.ComponentModel.DataAnnotations;
using MatSmtp.Configuration;
using MatSmtp.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Admin;

/// <summary>Sign-in rules, how long share links live, and the address the installation is reached at (for the links that are handed on).</summary>
public sealed class SecurityModel(ConfigStore config, ActivityLogger log, PublicUrl urls) : PageModelBase
{
    public sealed class InputModel
    {
        [Range(1, 365, ErrorMessage = "Eine Sitzung dauert zwischen 1 und 365 Tagen.")]
        [Display(Name = "Sitzung bleibt gültig für Tage")]
        public int SessionDays { get; set; } = 14;

        public bool RequireTotpForAdmins { get; set; }

        [Range(1, 365, ErrorMessage = "Ein Link gilt zwischen 1 und 365 Tagen.")]
        [Display(Name = "Freigabe-Link gilt für Tage")]
        public int ShareLinkDays { get; set; } = 14;

        [Display(Name = "Öffentliche Adresse")]
        public string? PublicUrl { get; set; }

        public bool ForceSecureCookie { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>The address that is used now, so that the field can say what happens when it is empty.</summary>
    public string CurrentBase => urls.Absolute("/");

    public bool UrlFromEnvironment => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MATSMTP__Server__PublicUrl"));

    public void OnGet()
    {
        Prepare();
        var s = config.Current.Security;
        Input = new InputModel
        {
            SessionDays = s.SessionDays, RequireTotpForAdmins = s.RequireTotpForAdmins,
            ShareLinkDays = s.ShareLinkDays, PublicUrl = config.Current.Server.PublicUrl, ForceSecureCookie = config.Current.Server.ForceSecureCookie,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Prepare();
        var url = Input.PublicUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(url) && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            ModelState.AddModelError("Input.PublicUrl", "Das ist keine Adresse. Sie beginnt mit https:// oder http://.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        config.Update(c =>
        {
            var s = c.Security;
            s.SessionDays = Input.SessionDays;
            s.RequireTotpForAdmins = Input.RequireTotpForAdmins;
            s.ShareLinkDays = Input.ShareLinkDays;
            c.Server.PublicUrl = url ?? "";
            c.Server.ForceSecureCookie = Input.ForceSecureCookie;
        });
        await log.LogAsync("Sicherheitseinstellungen geändert", "Security");
        Flash("Die Einstellungen sind gespeichert. Die Dauer der Sitzung gilt für neue Anmeldungen.");
        return RedirectToPage();
    }

    private void Prepare()
    {
        Head("Sicherheit", "Anmeldung, Gültigkeit von Freigabe-Links und die Adresse dieser Installation.", ("Einstellungen", null), ("Sicherheit", null));
        ViewData["Width"] = "narrow";
    }
}
