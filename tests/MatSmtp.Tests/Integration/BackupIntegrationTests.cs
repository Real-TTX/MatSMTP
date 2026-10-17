using System.IO.Compression;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatSmtp.Tests.Integration;

/// <summary>A clock the test moves by hand, so that backups made one after the other have different names.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

[Collection(IntegrationCollection.Name)]
public sealed class BackupIntegrationTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BackupIntegrationTests(AppFixture app) => _app = app;

    private static BackupService ServiceOf(IServiceProvider sp, TimeProvider clock) => ActivatorUtilities.CreateInstance<BackupService>(sp, clock);

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ms-backup-it-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task<(BackupTarget Target, BackupPlan Plan)> NewPlanAsync(string folder, int keepLast, BackupScope scope = BackupScope.SystemOnly, string? passphrase = null)
        => await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            var target = new BackupTarget { Name = "Test " + Guid.NewGuid().ToString("N")[..4], Kind = BackupTargetKind.Local, Path = folder };
            db.BackupTarget.Add(target);
            await db.SaveChangesAsync();
            var plan = new BackupPlan
            {
                Name = "Plan " + Guid.NewGuid().ToString("N")[..4], TargetId = target.Id, Scope = scope, Schedule = ScheduleKind.Daily, KeepLast = keepLast,
                PassphraseProtected = passphrase is null ? null : sp.GetRequiredService<Configuration.SecretProtector>().Protect(passphrase),
            };
            db.BackupPlan.Add(plan);
            await db.SaveChangesAsync();
            return (target, plan);
        });

    [Fact]
    public async Task A_plan_writes_to_its_target_and_removes_the_backups_it_no_longer_keeps()
    {
        await _app.EnsureAdminAsync();
        var folder = NewFolder();
        var (_, plan) = await NewPlanAsync(folder, keepLast: 2);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero));

        for (var i = 0; i < 4; i++)
        {
            var run = await _app.WithScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<MsDbContext>();
                var current = await db.BackupPlan.Include(p => p.Target).FirstAsync(p => p.Id == plan.Id);
                return await ServiceOf(sp, clock).RunPlanAsync(current, manual: true);
            });
            Assert.Equal(BackupRunStatus.Success, run.Status);
            Assert.True(run.SizeBytes > 0);
            clock.Advance(TimeSpan.FromMinutes(5));
        }

        var files = new DirectoryInfo(folder).GetFiles("*.msbackup").Select(f => f.Name).OrderBy(n => n).ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains("20261012-011000", files[0]);
        Assert.Contains("20261012-011500", files[1]);

        var runs = await _app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().BackupRun.AsNoTracking().Where(r => r.PlanId == plan.Id).CountAsync());
        Assert.Equal(4, runs);
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task A_plan_that_is_due_runs_by_itself_and_is_planned_again()
    {
        await _app.EnsureAdminAsync();
        var folder = NewFolder();
        var (_, plan) = await NewPlanAsync(folder, keepLast: 3);
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            (await db.BackupPlan.FirstAsync(p => p.Id == plan.Id)).NextRunDate = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });

        await _app.WithScopeAsync(sp => sp.GetRequiredService<BackupService>().RunDueAsync());

        var after = await _app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().BackupPlan.AsNoTracking().FirstAsync(p => p.Id == plan.Id));
        Assert.NotNull(after.LastRunDate);
        Assert.True(after.NextRunDate > DateTime.UtcNow, "the next run is in the future");
        Assert.Single(new DirectoryInfo(folder).GetFiles("*.msbackup"));
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>A message received through the relay (the spooler is paused, so it stays in the queue with its content).</summary>
    private Task<long> ReceiveAsync(string subject) => _app.WithScopeAsync(async sp =>
    {
        sp.GetRequiredService<Configuration.ConfigStore>().Update(c => c.Delivery.Paused = true);
        var raw = System.Text.Encoding.UTF8.GetBytes($"From: app@app.local\r\nTo: kunde@example.test\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@test>\r\n\r\nInhalt {subject}\r\n");
        var result = await sp.GetRequiredService<Services.Relay.RelayService>().ReceiveAsync(
            new Services.Relay.SessionInfo(System.Net.IPAddress.Loopback, "test", null, 25, false), "app@app.local", ["kunde@example.test"], raw);
        Assert.True(result.Accepted);
        return result.MessageId!.Value;
    });

    [Fact]
    public async Task A_restore_brings_back_the_database_and_the_content_of_the_messages()
    {
        await _app.EnsureAdminAsync();
        var kept = await ReceiveAsync("Behalten");
        var ruleId = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            var rule = new Rule { Name = "Aus der Sicherung", MatchSenders = "app.local", Action = MatSmtp.Core.Relay.RuleAction.Relay };
            db.Rule.Add(rule);
            await db.SaveChangesAsync();
            return rule.Id;
        });

        // the backup: encrypted, with the content of the messages
        string archive;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            archive = await ServiceOf(scope.ServiceProvider, TimeProvider.System).CreateFileAsync(BackupScope.SystemAndFiles, "Geheim-2026");
        }

        await using (var stream = File.OpenRead(archive))
        {
            Assert.True(await BackupCrypto.IsEncryptedAsync(stream));
        }

        // afterwards: one more message, the rule is gone and the content of the spool folder too
        var lost = await ReceiveAsync("Verloren");
        await _app.WithScopeAsync(async sp => await sp.GetRequiredService<MsDbContext>().Rule.Where(r => r.Id == ruleId).ExecuteDeleteAsync());
        foreach (var f in Directory.EnumerateFiles(Path.Combine(_app.DataDir, "spool"), "*.eml", SearchOption.AllDirectories))
        {
            File.Delete(f);
        }

        RestoreResult result;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var backups = ServiceOf(scope.ServiceProvider, TimeProvider.System);

            // a wrong passphrase is refused before anything is touched
            await using (var wrong = File.OpenRead(archive))
            {
                await Assert.ThrowsAsync<BackupException>(() => backups.StageAsync(wrong, "falsch"));
            }

            await using (var noPassphrase = File.OpenRead(archive))
            {
                await Assert.ThrowsAsync<BackupException>(() => backups.StageAsync(noPassphrase, null));
            }

            await using var input = File.OpenRead(archive);
            var staged = await backups.StageAsync(input, "Geheim-2026");
            var path = backups.StagedPath(staged)!;
            var manifest = await backups.ReadManifestAsync(path);
            Assert.Equal(BackupScope.SystemAndFiles, manifest.Scope);
            Assert.True(manifest.Tables.Sum(t => t.Rows) > 0);
            Assert.NotEmpty(manifest.Files);

            result = await backups.RestoreAsync(path, new RestoreOptions(Files: true, Settings: false));
            backups.DeleteStaged(path);
        }

        File.Delete(archive);
        Assert.True(result.Tables > 10);
        Assert.True(result.Files >= 1);
        Assert.NotNull(result.SafetyCopy);
        Assert.True(File.Exists(result.SafetyCopy));

        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            Assert.True(await db.Rule.AnyAsync(r => r.Id == ruleId));
            Assert.True(await db.Message.AnyAsync(m => m.Id == kept));
            Assert.False(await db.Message.AnyAsync(m => m.Id == lost));

            // the content is back and readable
            var message = await db.Message.AsNoTracking().FirstAsync(m => m.Id == kept);
            var spool = sp.GetRequiredService<Services.Relay.SpoolStore>();
            Assert.True(spool.Exists(message.StorageKey));
            Assert.Contains("Inhalt Behalten", System.Text.Encoding.UTF8.GetString(await spool.ReadAllAsync(message.StorageKey!)));

            // new rows get numbers after the restored ones
            var next = new Rule { Name = "Nach der Wiederherstellung" };
            db.Rule.Add(next);
            await db.SaveChangesAsync();
            Assert.True(next.Id > ruleId);
        });

        using var client = _app.NewClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task A_backup_from_a_newer_version_or_a_damaged_file_is_refused_and_changes_nothing()
    {
        await _app.EnsureAdminAsync();
        var kept = await ReceiveAsync("Unverändert");

        string archive;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            archive = await ServiceOf(scope.ServiceProvider, TimeProvider.System).CreateFileAsync(BackupScope.SystemOnly, null);
        }

        // from the future: the manifest names a migration this version does not know
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            string manifestText;
            using (var reader = new StreamReader(entry.Open()))
            {
                manifestText = await reader.ReadToEndAsync();
            }

            entry.Delete();
            var changed = System.Text.RegularExpressions.Regex.Replace(manifestText, "\"migration\": \"[^\"]+\"", "\"migration\": \"99999999999999_Zukunft\"");
            await using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            await writer.WriteAsync(changed);
        }

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var backups = ServiceOf(scope.ServiceProvider, TimeProvider.System);
            var error = await Assert.ThrowsAsync<BackupException>(() => backups.RestoreAsync(archive, new RestoreOptions(true, false)));
            Assert.Contains("neueren Version", error.Message);

            var notAZip = Path.Combine(Path.GetTempPath(), "ms-not-a-zip-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
            await File.WriteAllTextAsync(notAZip, "das ist keine Sicherung");
            await Assert.ThrowsAsync<BackupException>(() => backups.RestoreAsync(notAZip, new RestoreOptions(true, false)));
            File.Delete(notAZip);
        }

        File.Delete(archive);
        Assert.True(await _app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().Message.AnyAsync(m => m.Id == kept)));
    }

    [Fact]
    public async Task A_backup_holds_the_database_the_settings_the_keys_and_the_certificate()
    {
        await _app.EnsureAdminAsync();
        // the certificate is made on first use
        _ = _app.Services.GetRequiredService<Services.Relay.CertificateService>().Current;
        await using var scope = _app.Services.CreateAsyncScope();
        var archive = await ServiceOf(scope.ServiceProvider, TimeProvider.System).CreateFileAsync(BackupScope.SystemOnly, null);

        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.NotNull(zip.GetEntry(DatabaseArchive.Entry));
            Assert.NotNull(zip.GetEntry("settings/app.json"));
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("settings/keys/", StringComparison.Ordinal));
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("settings/certs/", StringComparison.Ordinal));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("files/", StringComparison.Ordinal));
        }

        File.Delete(archive);
    }
}

