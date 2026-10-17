using System.ComponentModel.DataAnnotations;
using MatSmtp.Services;
using MatSmtp.Services.Backup;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Admin.Backup.Restore;

/// <summary>Step two of a restore: what is in the backup, what to restore, and the second question ("type RESTORE") before anything is replaced.</summary>
public sealed class ConfirmModel(BackupService backups) : PageModelBase
{
    public const string Phrase = "WIEDERHERSTELLEN";

    public sealed class InputModel
    {
        public bool Files { get; set; } = true;

        public bool Settings { get; set; }

        [Display(Name = "Bestätigung")]
        public string? Confirmation { get; set; }
    }

    [BindProperty(SupportsGet = true, Name = "file")]
    public string? Staged { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public BackupManifest? Manifest { get; private set; }

    public RestoreResult? Result { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Prepare();
        return await LoadAsync() ? Page() : RedirectToPage("/Admin/Backup/Restore/Index");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Prepare();
        if (!await LoadAsync())
        {
            return RedirectToPage("/Admin/Backup/Restore/Index");
        }

        if (!string.Equals(Input.Confirmation?.Trim(), Phrase, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Input.Confirmation", $"Bitte tippe {Phrase}, um zu bestätigen.");
            return Page();
        }

        var path = backups.StagedPath(Staged)!;
        try
        {
            Result = await backups.RestoreAsync(path, new RestoreOptions(Input.Files, Input.Settings), HttpContext.RequestAborted);
        }
        catch (BackupException ex)
        {
            ModelState.AddModelError("Input.Confirmation", ex.Message);
            return Page();
        }
        finally
        {
            backups.DeleteStaged(path);
        }

        return Page();
    }

    public IActionResult OnPostCancel()
    {
        if (backups.StagedPath(Staged) is { } path)
        {
            backups.DeleteStaged(path);
        }

        return RedirectToPage("/Admin/Backup/Index");
    }

    private async Task<bool> LoadAsync()
    {
        var path = backups.StagedPath(Staged);
        if (path is null)
        {
            if (Result is null)
            {
                FlashError("Diese Auswahl ist abgelaufen. Bitte wähle die Sicherung noch einmal.");
            }

            return false;
        }

        try
        {
            Manifest = await backups.ReadManifestAsync(path, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is BackupException or InvalidDataException)
        {
            FlashError(ex is BackupException ? ex.Message : "Die Datei ist keine gültige Sicherung.");
            backups.DeleteStaged(path);
            return false;
        }

        return true;
    }

    private void Prepare()
    {
        Head("Wiederherstellen", "Bitte prüfe, was zurückgespielt wird.", ("Einstellungen", null), ("Sicherung", "/Admin/Backup"), ("Wiederherstellen", "/Admin/Backup/Restore"), ("Bestätigen", null));
        ViewData["ActiveHref"] = "/Admin/Backup/Restore";
        ViewData["Width"] = "narrow";
    }
}
