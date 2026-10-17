using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Backup;
using MatSmtp.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Admin.Backup.Restore;

/// <summary>
/// Step one of a restore: choose the backup (from a target, from a file on this computer, or one of the safety copies of earlier restores). The file is
/// brought into the temporary folder (and decrypted there); step two shows what is in it and asks again.
/// </summary>
[DisableRequestSizeLimit]
[RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
public sealed class IndexModel(MsDbContext db, BackupService backups, BackupStoreFactory stores, ConfigStore config) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long? TargetId { get; set; }

    public List<SelectListItem> Targets { get; private set; } = [];

    public IReadOnlyList<BackupFileInfo> Files { get; private set; } = [];

    public IReadOnlyList<BackupFileInfo> SafetyCopies { get; private set; } = [];

    public string? ListError { get; private set; }

    private string SafetyFolder => Path.Combine(config.DataDir, "backups", "vor-wiederherstellung");

    public async Task OnGetAsync()
    {
        backups.CleanStaged();
        await PrepareAsync();
        if (TargetId is { } id && await db.BackupTarget.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id) is { } target)
        {
            try
            {
                Files = [.. (await stores.Create(target).ListAsync(HttpContext.RequestAborted)).OrderByDescending(f => f.CreatedUtc)];
            }
            catch (Exception ex) when (ex is StorageException or IOException)
            {
                ListError = ex.Message;
            }
        }

        SafetyCopies = await new LocalBackupStore(SafetyFolder).ListAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, string? passphrase)
    {
        if (file is null || file.Length == 0)
        {
            FlashError("Bitte wähle die Datei der Sicherung aus.");
            return RedirectToPage();
        }

        await using var stream = file.OpenReadStream();
        return await StageAsync(stream, passphrase);
    }

    public async Task<IActionResult> OnPostFetchAsync(long targetId, string name, string? passphrase)
    {
        var target = await db.BackupTarget.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId);
        if (target is null || !BackupNames.IsBackup(name))
        {
            return NotFound();
        }

        try
        {
            await using var stream = await stores.Create(target).OpenReadAsync(name, HttpContext.RequestAborted);
            return await StageAsync(stream, passphrase, targetId);
        }
        catch (Exception ex) when (ex is StorageException or IOException)
        {
            FlashError("Die Sicherung konnte nicht gelesen werden: " + ex.Message);
            return RedirectToPage(new { targetId });
        }
    }

    public async Task<IActionResult> OnPostSafetyAsync(string name)
    {
        if (!BackupNames.IsBackup(name))
        {
            return NotFound();
        }

        await using var stream = await new LocalBackupStore(SafetyFolder).OpenReadAsync(name, HttpContext.RequestAborted);
        return await StageAsync(stream, null);
    }

    private async Task<IActionResult> StageAsync(Stream stream, string? passphrase, long? targetId = null)
    {
        try
        {
            var staged = await backups.StageAsync(stream, passphrase, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Backup/Restore/Confirm", new { file = staged });
        }
        catch (BackupException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { targetId });
        }
    }

    private async Task PrepareAsync()
    {
        Head("Wiederherstellen", "Eine Sicherung zurückspielen.", ("Einstellungen", null), ("Sicherung", "/Admin/Backup"), ("Wiederherstellen", null));
        ViewData["ActiveHref"] = "/Admin/Backup/Restore";
        ViewData["Width"] = "narrow";
        Targets = await db.BackupTarget.AsNoTracking().OrderBy(t => t.Name).Select(t => new SelectListItem(t.Name, t.Id.ToString(), t.Id == TargetId)).ToListAsync();
    }
}
