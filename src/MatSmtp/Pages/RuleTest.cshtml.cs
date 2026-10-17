using System.Net;
using MatSmtp.Core.Relay;
using MatSmtp.Services.Relay;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages;

/// <summary>What the rules would do with a message like this: a dry run, nothing is received or sent.</summary>
public sealed class RuleTestModel(RelayService relay) : PageModelBase
{
    public sealed class InputModel
    {
        public string? From { get; set; }

        public string? HeaderFrom { get; set; }

        public string? To { get; set; }

        public string? ClientIp { get; set; }

        public string? AuthUser { get; set; }

        public string? Subject { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new() { From = "app@firma.de", To = "kunde@example.com", ClientIp = "10.0.0.20" };

    public RuleTestResult? Result { get; private set; }

    public MessageFacts? Facts { get; private set; }

    public void OnGet() => HeadUp();

    public async Task<IActionResult> OnPostAsync()
    {
        HeadUp();
        IPAddress? ip = null;
        if (!string.IsNullOrWhiteSpace(Input.ClientIp) && !IPAddress.TryParse(Input.ClientIp.Trim(), out ip))
        {
            ModelState.AddModelError("Input.ClientIp", "Das ist keine IP-Adresse.");
        }

        var recipients = AddressPattern.Split(Input.To);
        if (recipients.Count == 0)
        {
            ModelState.AddModelError("Input.To", "Bitte gib mindestens einen Empfänger ein.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        Facts = new MessageFacts
        {
            EnvelopeFrom = (Input.From ?? "").Trim(),
            HeaderFrom = string.IsNullOrWhiteSpace(Input.HeaderFrom) ? (Input.From ?? "").Trim() : Input.HeaderFrom.Trim(),
            Recipients = recipients,
            ClientIp = ip,
            AuthUser = string.IsNullOrWhiteSpace(Input.AuthUser) ? null : Input.AuthUser.Trim(),
            Subject = Input.Subject,
        };
        Result = await relay.TestAsync(Facts, HttpContext.RequestAborted);
        return Page();
    }

    private void HeadUp()
    {
        Head("Regeln testen", "Was die Regeln mit einer solchen Nachricht machen würden. Es wird nichts empfangen und nichts versendet.", ("Werkzeuge", null), ("Regeln testen", null));
        ViewData["Width"] = "narrow";
    }
}
