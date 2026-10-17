using System.Diagnostics;
using System.Runtime.InteropServices;
using MatSmtp.Configuration;
using MatSmtp.Services;
using MatSmtp.Services.Backup;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Admin;

/// <summary>What the installation is made of and how it is doing: version, runtime, data volume, database, spool folder, and the maintenance.</summary>
public sealed class SystemModel(SystemService system, ConfigStore config, ActivityLogger log) : PageModelBase
{
    public sealed record Folder(string Name, string Hint, long Bytes, int Files);

    public string Version => AppVersion.Current;

    public string Runtime => RuntimeInformation.FrameworkDescription;

    public string Os => RuntimeInformation.OSDescription;

    public string Architecture => RuntimeInformation.OSArchitecture.ToString();

    public DateTime Started => Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public long Memory => Process.GetCurrentProcess().WorkingSet64;

    public string DataDir => config.DataDir;

    public string DatabasePath => config.DatabasePath;

    public string? DatabaseVersion { get; private set; }

    public long DatabaseBytes { get; private set; }

    public int SpoolFiles { get; private set; }

    public long SpoolBytes { get; private set; }

    public List<Folder> Folders { get; private set; } = [];

    public Dictionary<string, int> Counts { get; private set; } = [];

    public string TimeZone => Fmt.Zone.Id;

    public bool BackupBusy => BackupService.IsBusy;

    public async Task OnGetAsync()
    {
        Head("System", "Version, Speicher und Datenbank.", ("Einstellungen", null), ("System", null));
        var ct = HttpContext.RequestAborted;

        var database = await system.GetDatabaseAsync(ct);
        DatabaseVersion = database.Version;
        DatabaseBytes = database.Bytes;
        (SpoolFiles, SpoolBytes) = system.SpoolSize();
        Counts = await system.CountRowsAsync(ct);

        Folders =
        [
            Measure("db", "Datenbank", "SQLite: Regeln, Konten, Nachrichten, Benutzer"),
            Measure("spool", "Warteschlange", "Inhalt der Nachrichten (.eml), bis er nach der eingestellten Zeit entfernt wird"),
            Measure("certs", "Zertifikate", "TLS-Zertifikat des Empfangs"),
            Measure("keys", "Schlüssel", "Schutz der Cookies und der gespeicherten Passwörter"),
            Measure("config", "Einstellungen", "app.json"),
            Measure("backups", "Sicherungen im Volume", "Lokale Ziele und Sicherheitskopien"),
            Measure("tmp", "Temporär", "Zwischenablage für Sicherungen"),
        ];
    }

    /// <summary>Removes old messages, sessions and logs now (the maintenance does the same every hour).</summary>
    public async Task<IActionResult> OnPostMaintainAsync()
    {
        var result = await system.MaintainAsync(HttpContext.RequestAborted);
        await log.LogAsync("Aufräumen", "System", null, $"{result.Total} Einträge entfernt");
        Flash(result.Total == 0 ? "Es gab nichts aufzuräumen." : $"{Ui.Plural(result.Total, "alter Eintrag", "alte Einträge")} entfernt.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVacuumAsync()
    {
        var before = (await system.GetDatabaseAsync(HttpContext.RequestAborted)).Bytes;
        await system.VacuumAsync(HttpContext.RequestAborted);
        var after = (await system.GetDatabaseAsync(HttpContext.RequestAborted)).Bytes;
        await log.LogAsync("Datenbank verdichtet", "System", null, $"{Fmt.FileSize(before)} → {Fmt.FileSize(after)}");
        Flash($"Die Datenbank ist verdichtet: {Fmt.FileSize(before)} → {Fmt.FileSize(after)}.");
        return RedirectToPage();
    }

    private Folder Measure(string name, string title, string hint)
    {
        var path = Path.Combine(config.DataDir, name);
        if (!Directory.Exists(path))
        {
            return new Folder(title, hint, 0, 0);
        }

        long bytes = 0;
        var count = 0;
        foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            bytes += f.Length;
            count++;
        }

        return new Folder(title, hint, bytes, count);
    }
}
