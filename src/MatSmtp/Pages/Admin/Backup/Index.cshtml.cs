using MatSmtp.Configuration;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Backup;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Admin.Backup;

/// <summary>
/// The backup at a glance: is it on, what ran last, what runs next, and a button to take a backup to download right now (for the case that there is no NAS yet,
/// or before an update).
/// </summary>
public sealed class IndexModel(BackupAdminService admin, ConfigStore config, BackupService backups, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public IReadOnlyList<BackupPlan> Plans { get; private set; } = [];

    public TableModel Runs { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public BackupRun? LastRun { get; private set; }

    public DateTime? NextRun => Plans.Where(p => p.IsEnabled && p.NextRunDate is not null).Min(p => p.NextRunDate);

    public bool Enabled => config.Current.Backup.Enabled;

    public bool Running => BackupService.IsBusy;

    public int TargetCount { get; private set; }

    public async Task OnGetAsync()
    {
        Head("Sicherung", "Datenbank, Einstellungen und Anhänge auf ein NAS oder in einen Ordner sichern.", ("Einstellungen", null), ("Sicherung", null));
        var overview = await admin.OverviewAsync(ListQuery.Page(Request), 10, HttpContext.RequestAborted);
        (Plans, TargetCount, LastRun) = (overview.Plans, overview.TargetCount, overview.LastRun);
        Pager = new PagerModel { Page = overview.Page, PageSize = 10, Total = overview.RunCount, Url = p => Url.Page("/Admin/Backup/Index", new { page = p })! };
        Runs = TableModel.Of("run-table", "Sicherungen der letzten Zeit", overview.Runs,
        [
            (new TableColumn("Wann"), r => Ui.Text(Fmt.Moment(r.StartDate))),
            (new TableColumn("Plan"), r => Ui.NameWithSub(r.PlanName ?? "Einzelne Sicherung", null, (r.IsManual ? "von Hand" : "automatisch") + (r.TargetName is null ? "" : " → " + r.TargetName))),
            (new TableColumn("Größe", Numeric: true, HideOnPhone: true), r => Ui.Text(r.Status == BackupRunStatus.Success ? Fmt.FileSize(r.SizeBytes) : "")),
            (new TableColumn("Dauer", Numeric: true, HideOnPhone: true), r => Ui.Text($"{Math.Max(1, (int)(r.EndDate - r.StartDate).TotalSeconds)} s")),
            (new TableColumn("Ergebnis"), r => r.Status == BackupRunStatus.Success ? Ui.NameWithSub("erfolgreich", null, r.Message) : Ui.NameWithSub("fehlgeschlagen", null, r.Message)),
        ],
        r => []);
        Runs = Runs with { EmptyTitle = "Noch keine Sicherung", EmptyText = "Lege ein Ziel und einen Plan an, oder lade jetzt eine Sicherung herunter.", EmptyIcon = "shield" };
    }

    public IActionResult OnPostToggle()
    {
        var now = !config.Current.Backup.Enabled;
        config.Update(c => c.Backup.Enabled = now);
        Flash(now ? "Die geplanten Sicherungen laufen wieder." : "Die geplanten Sicherungen sind angehalten.");
        return RedirectToPage();
    }

    /// <summary>A backup to download, made now: the file is built in the temporary folder and removed when the download is done.</summary>
    public async Task<IActionResult> OnPostDownloadAsync(BackupScope scope, string? passphrase)
    {
        if (BackupService.IsBusy)
        {
            FlashError("Es läuft gerade schon eine Sicherung oder Wiederherstellung.");
            return RedirectToPage();
        }

        try
        {
            var path = await backups.CreateFileAsync(scope, passphrase, HttpContext.RequestAborted);
            var name = BackupNames.Make("manuell", clock.GetUtcNow().UtcDateTime, !string.IsNullOrEmpty(passphrase));
            await log.LogAsync("Sicherung heruntergeladen", "Backup", null, scope.ToString());
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return File(stream, "application/octet-stream", name);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or BackupException)
        {
            FlashError("Die Sicherung konnte nicht erstellt werden: " + ex.Message);
            return RedirectToPage();
        }
    }
}
