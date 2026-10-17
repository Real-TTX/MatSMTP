using System.ComponentModel.DataAnnotations;
using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Backup;
using MatSmtp.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Admin.Backup.Targets;

/// <summary>A target: a folder, or a share of a NAS with its sign-in. The password is kept protected. "Prüfen" writes and removes a small file to see that it works.</summary>
public sealed class EditModel(MsDbContext db, SecretProtector secrets, BackupStoreFactory stores, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib dem Ziel einen Namen.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [Display(Name = "Art")]
        public BackupTargetKind Kind { get; set; } = BackupTargetKind.Smb;

        [Display(Name = "Ordner")]
        public string Path { get; set; } = "";

        [Display(Name = "Server")]
        public string? Host { get; set; }

        [Display(Name = "Freigabe")]
        public string? Share { get; set; }

        [Display(Name = "Domäne (optional)")]
        public string? Domain { get; set; }

        [Display(Name = "Benutzername")]
        public string? UserName { get; set; }

        [Display(Name = "Passwort")]
        public string? Password { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public BackupTarget? Target { get; private set; }

    public string? Test { get; private set; }

    public bool TestOk { get; private set; }

    public bool HasPassword => !string.IsNullOrEmpty(Target?.PasswordProtected);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (Target is { } t)
        {
            Input = new InputModel { Name = t.Name, Kind = t.Kind, Path = t.Path, Host = t.Host, Share = t.Share, Domain = t.Domain, UserName = t.UserName };
        }
        else
        {
            Input.Path = "MatSMTP-Sicherungen";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var target = Target is { } existing ? await db.BackupTarget.FirstAsync(t => t.Id == existing.Id) : db.BackupTarget.Add(new BackupTarget()).Entity;
        Apply(target);
        await db.SaveChangesAsync();
        await log.LogAsync("Sicherungsziel gespeichert", "BackupTarget", target.Id, target.Name);
        Flash("Das Ziel ist gespeichert.");
        return RedirectToPage("/Admin/Backup/Targets/Index");
    }

    /// <summary>Tries the settings in the form (not yet saved).</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var probe = new BackupTarget { PasswordProtected = Target?.PasswordProtected };
        Apply(probe);
        try
        {
            (TestOk, Test) = await stores.Create(probe).CheckAsync(HttpContext.RequestAborted);
        }
        catch (StorageException ex)
        {
            Test = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var target = await db.BackupTarget.FirstOrDefaultAsync(t => t.Id == Id);
        if (target is null)
        {
            return RedirectToPage("/Admin/Backup/Targets/Index");
        }

        if (await db.BackupPlan.AnyAsync(p => p.TargetId == target.Id))
        {
            FlashError("Dieses Ziel wird von einem Plan benutzt. Ändere oder lösche zuerst den Plan.");
            return RedirectToPage(new { id = Id });
        }

        db.BackupTarget.Remove(target);
        await db.SaveChangesAsync();
        await log.LogAsync("Sicherungsziel gelöscht", "BackupTarget", target.Id, target.Name);
        Flash("Das Ziel ist gelöscht. Die Sicherungen dort bleiben liegen.");
        return RedirectToPage("/Admin/Backup/Targets/Index");
    }

    private void Validate()
    {
        if (Input.Kind == BackupTargetKind.Local)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Input.Host))
        {
            ModelState.AddModelError("Input.Host", "Bitte gib den Server (Namen oder Adresse) des NAS an.");
        }

        if (string.IsNullOrWhiteSpace(Input.Share))
        {
            ModelState.AddModelError("Input.Share", "Bitte gib den Namen der Freigabe an.");
        }
    }

    private void Apply(BackupTarget target)
    {
        target.Name = Input.Name?.Trim() ?? "";
        target.Kind = Input.Kind;
        target.Path = (Input.Path ?? "").Trim();
        if (Input.Kind == BackupTargetKind.Local)
        {
            return;
        }

        target.Host = Input.Host?.Trim();
        target.Share = Input.Share?.Trim();
        target.Domain = Input.Domain?.Trim();
        target.UserName = Input.UserName?.Trim();
        if (!string.IsNullOrEmpty(Input.Password))
        {
            target.PasswordProtected = secrets.Protect(Input.Password);
        }
    }

    private async Task<bool> PrepareAsync()
    {
        if (Id is { } id)
        {
            Target = await db.BackupTarget.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
            if (Target is null)
            {
                return false;
            }
        }

        var title = Target is null ? "Neues Ziel" : Target.Name;
        Head(title, null, ("Einstellungen", null), ("Sicherung", "/Admin/Backup"), ("Ziele", "/Admin/Backup/Targets"), (title, null));
        ViewData["ActiveHref"] = "/Admin/Backup/Targets";
        ViewData["Width"] = "narrow";
        return true;
    }
}
