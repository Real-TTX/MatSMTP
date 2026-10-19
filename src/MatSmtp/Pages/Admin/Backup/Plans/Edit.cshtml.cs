using System.ComponentModel.DataAnnotations;
using MatSmtp.Configuration;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Backup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatSmtp.Pages.Admin.Backup.Plans;

/// <summary>One plan. The passphrase (optional) encrypts every backup of the plan; it is kept protected so that the plan can run by itself.</summary>
public sealed class EditModel(BackupAdminService admin, SecretProtector secrets, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib dem Plan einen Namen.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [Display(Name = "Ziel")]
        public long? TargetId { get; set; }

        [Display(Name = "Umfang")]
        public BackupScope Scope { get; set; } = BackupScope.SystemOnly;

        [Display(Name = "Wie oft")]
        public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;

        [Range(1, 168, ErrorMessage = "Das Intervall liegt zwischen 1 und 168 Stunden.")]
        [Display(Name = "Alle … Stunden")]
        public int IntervalHours { get; set; } = 6;

        [Display(Name = "Uhrzeit")]
        public string TimeOfDay { get; set; } = "02:30";

        [Display(Name = "Wochentag")]
        public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Sunday;

        [Range(1, 31, ErrorMessage = "Der Tag liegt zwischen 1 und 31.")]
        [Display(Name = "Tag im Monat")]
        public int DayOfMonth { get; set; } = 1;

        [Range(0, 1000, ErrorMessage = "Bitte gib eine Zahl ab 0 ein.")]
        [Display(Name = "Die neuesten")]
        public int KeepLast { get; set; } = 7;

        [Range(0, 1000, ErrorMessage = "Bitte gib eine Zahl ab 0 ein.")]
        [Display(Name = "Eine pro Tag für")]
        public int KeepDaily { get; set; }

        [Range(0, 1000, ErrorMessage = "Bitte gib eine Zahl ab 0 ein.")]
        [Display(Name = "Eine pro Woche für")]
        public int KeepWeekly { get; set; }

        [Range(0, 1000, ErrorMessage = "Bitte gib eine Zahl ab 0 ein.")]
        [Display(Name = "Eine pro Monat für")]
        public int KeepMonthly { get; set; }

        [Display(Name = "Passwort zum Verschlüsseln")]
        public string? Passphrase { get; set; }

        public bool RemovePassphrase { get; set; }

        public bool IsEnabled { get; set; } = true;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public BackupPlan? Plan { get; private set; }

    public List<SelectListItem> Targets { get; private set; } = [];

    public bool Encrypted => !string.IsNullOrEmpty(Plan?.PassphraseProtected);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (Plan is { } p)
        {
            Input = new InputModel
            {
                Name = p.Name, TargetId = p.TargetId, Scope = p.Scope, Schedule = p.Schedule, IntervalHours = p.IntervalHours, TimeOfDay = p.TimeOfDay.ToString("HH\\:mm"), DayOfWeek = p.DayOfWeek,
                DayOfMonth = p.DayOfMonth, KeepLast = p.KeepLast, KeepDaily = p.KeepDaily, KeepWeekly = p.KeepWeekly, KeepMonthly = p.KeepMonthly, IsEnabled = p.IsEnabled,
            };
        }
        else
        {
            Input.TargetId = Targets.Count > 0 ? long.Parse(Targets[0].Value) : null;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (Input.TargetId is null || !Targets.Any(t => t.Value == Input.TargetId.ToString()))
        {
            ModelState.AddModelError("Input.TargetId", "Bitte wähle ein Ziel. Ein Ziel legst du unter „Ziele“ an.");
        }

        if (!TimeOnly.TryParseExact(Input.TimeOfDay, "HH:mm", out var time) && !TimeOnly.TryParse(Input.TimeOfDay, out time))
        {
            ModelState.AddModelError("Input.TimeOfDay", "Bitte gib die Uhrzeit so ein: 02:30.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var plan = await admin.SavePlanAsync(Plan?.Id, plan => Apply(plan, time), HttpContext.RequestAborted);
        await log.LogAsync("Sicherungsplan gespeichert", "BackupPlan", plan.Id, plan.Name);
        Flash("Der Plan ist gespeichert.");
        return RedirectToPage("/Admin/Backup/Plans/Index");
    }

    private void Apply(BackupPlan plan, TimeOnly time)
    {
        plan.Name = Input.Name.Trim();
        plan.TargetId = Input.TargetId!.Value;
        plan.Scope = Input.Scope;
        plan.Schedule = Input.Schedule;
        plan.IntervalHours = Input.IntervalHours;
        plan.TimeOfDay = time;
        plan.DayOfWeek = Input.DayOfWeek;
        plan.DayOfMonth = Input.DayOfMonth;
        plan.KeepLast = Input.KeepLast;
        plan.KeepDaily = Input.KeepDaily;
        plan.KeepWeekly = Input.KeepWeekly;
        plan.KeepMonthly = Input.KeepMonthly;
        plan.IsEnabled = Input.IsEnabled;
        if (!string.IsNullOrEmpty(Input.Passphrase))
        {
            plan.PassphraseProtected = secrets.Protect(Input.Passphrase);
        }
        else if (Input.RemovePassphrase)
        {
            plan.PassphraseProtected = null;
        }
    }

    /// <summary>Starts the plan now, in the background (a backup of many files takes a while); the overview shows when it is done.</summary>
    public async Task<IActionResult> OnPostRunAsync()
    {
        if (Id is not { } id || await admin.FindPlanAsync(id, HttpContext.RequestAborted) is null)
        {
            return NotFound();
        }

        if (BackupService.IsBusy)
        {
            FlashError("Es läuft gerade schon eine Sicherung oder Wiederherstellung.");
            return RedirectToPage("/Admin/Backup/Index");
        }

        await admin.StartPlanAsync(id, HttpContext.RequestAborted);
        Flash("Die Sicherung läuft. Das Ergebnis erscheint in der Übersicht.");
        return RedirectToPage("/Admin/Backup/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var plan = Id is { } id ? await admin.DeletePlanAsync(id, HttpContext.RequestAborted) : null;
        if (plan is not null)
        {
            await log.LogAsync("Sicherungsplan gelöscht", "BackupPlan", plan.Id, plan.Name);
            Flash("Der Plan ist gelöscht. Die Sicherungen am Ziel bleiben liegen.");
        }

        return RedirectToPage("/Admin/Backup/Plans/Index");
    }

    private async Task<bool> PrepareAsync()
    {
        if (Id is { } id)
        {
            Plan = await admin.FindPlanAsync(id, HttpContext.RequestAborted);
            if (Plan is null)
            {
                return false;
            }
        }

        Targets = [.. (await admin.TargetChoicesAsync(HttpContext.RequestAborted)).Select(t => new SelectListItem(t.Name, t.Id.ToString()))];
        var title = Plan is null ? "Neuer Plan" : Plan.Name;
        Head(title, null, ("Einstellungen", null), ("Sicherung", "/Admin/Backup"), ("Pläne", "/Admin/Backup/Plans"), (title, null));
        ViewData["ActiveHref"] = "/Admin/Backup/Plans";
        ViewData["Width"] = "narrow";
        return true;
    }
}
