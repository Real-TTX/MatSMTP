using System.ComponentModel.DataAnnotations;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Admin;

/// <summary>How the spooler works: pause, attempts and their waits, how many at once, how long messages and their content are kept.</summary>
public sealed class DeliveryModel(ConfigStore config, SpoolSignal signal, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        public bool Paused { get; set; }

        [Range(1, 50, ErrorMessage = "1 bis 50 Versuche.")]
        public int MaxAttempts { get; set; } = 8;

        [Required(ErrorMessage = "Bitte gib die Wartezeiten ein.")]
        public string RetryMinutes { get; set; } = RetrySchedule.Default;

        [Range(1, 32, ErrorMessage = "1 bis 32.")]
        public int Parallel { get; set; } = 4;

        [Range(1, 3650, ErrorMessage = "1 bis 3650 Tage.")]
        public int KeepDays { get; set; } = 30;

        [Range(0, 3650, ErrorMessage = "0 bis 3650 Tage.")]
        public int KeepBodyDays { get; set; } = 7;

        public bool AddReceivedHeader { get; set; } = true;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public void OnGet()
    {
        HeadUp();
        var d = config.Current.Delivery;
        Input = new InputModel
        {
            Paused = d.Paused, MaxAttempts = d.MaxAttempts, RetryMinutes = d.RetryMinutes, Parallel = d.Parallel, KeepDays = d.KeepDays, KeepBodyDays = d.KeepBodyDays,
            AddReceivedHeader = d.AddReceivedHeader,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        HeadUp();
        if (RetrySchedule.Problem(Input.RetryMinutes) is { } problem)
        {
            ModelState.AddModelError("Input.RetryMinutes", problem);
        }

        if (Input.KeepBodyDays > Input.KeepDays)
        {
            ModelState.AddModelError("Input.KeepBodyDays", "Der Inhalt kann nicht länger bleiben als die Nachricht.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var wasPaused = config.Current.Delivery.Paused;
        config.Update(c =>
        {
            var d = c.Delivery;
            d.Paused = Input.Paused;
            d.MaxAttempts = Input.MaxAttempts;
            d.RetryMinutes = string.Join(", ", RetrySchedule.Parse(Input.RetryMinutes));
            d.Parallel = Input.Parallel;
            d.KeepDays = Input.KeepDays;
            d.KeepBodyDays = Input.KeepBodyDays;
            d.AddReceivedHeader = Input.AddReceivedHeader;
        });
        signal.Wake();
        await log.LogAsync(wasPaused != Input.Paused ? (Input.Paused ? "Versand pausiert" : "Versand fortgesetzt") : "Zustellung geändert", "Settings");
        Flash(wasPaused && !Input.Paused ? "Der Versand läuft wieder." : Input.Paused ? "Gespeichert. Der Versand ist pausiert." : "Die Zustellung ist gespeichert.");
        return RedirectToPage();
    }

    private void HeadUp() => Head("Zustellung", "Wie die Warteschlange versendet: Wiederholungen, Gleichzeitigkeit und wie lange Nachrichten aufbewahrt werden.", ("Einstellungen", null), ("Zustellung", null));
}
