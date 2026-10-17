using System.Security.Cryptography;
using MatSmtp.Data.Entities;
using MatSmtp.Services.Backup;

namespace MatSmtp.Tests.Web;

public class BackupTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------------------------------------ the schedule

    [Fact]
    public void A_daily_plan_runs_at_its_time_in_the_clock_of_the_installation()
    {
        var plan = new BackupPlan { Schedule = ScheduleKind.Daily, TimeOfDay = new TimeOnly(2, 30) };

        // 2026-10-12 00:10 UTC is 02:10 in Berlin (summer time): the run is 30 minutes later on the same day
        Assert.Equal(Utc(2026, 10, 12, 0, 30), BackupSchedule.Next(plan, Utc(2026, 10, 12, 0, 10), Berlin));

        // after the time has passed, it is tomorrow
        Assert.Equal(Utc(2026, 10, 13, 0, 30), BackupSchedule.Next(plan, Utc(2026, 10, 12, 0, 31), Berlin));
    }

    [Fact]
    public void A_plan_follows_the_change_to_winter_time()
    {
        var plan = new BackupPlan { Schedule = ScheduleKind.Daily, TimeOfDay = new TimeOnly(2, 30) };

        // 2026-10-25 is the day the clocks go back; the night of 26 October is winter time (UTC+1)
        Assert.Equal(Utc(2026, 10, 26, 1, 30), BackupSchedule.Next(plan, Utc(2026, 10, 25, 12), Berlin));
    }

    [Fact]
    public void A_time_that_does_not_exist_runs_an_hour_later()
    {
        var plan = new BackupPlan { Schedule = ScheduleKind.Daily, TimeOfDay = new TimeOnly(2, 30) };

        // 2026-03-29: at 02:00 the clocks go to 03:00, there is no 02:30
        var next = BackupSchedule.Next(plan, Utc(2026, 3, 28, 12), Berlin);
        Assert.Equal(Utc(2026, 3, 29, 1, 30), next);
    }

    [Fact]
    public void A_weekly_plan_runs_on_its_day()
    {
        var plan = new BackupPlan { Schedule = ScheduleKind.Weekly, DayOfWeek = DayOfWeek.Sunday, TimeOfDay = new TimeOnly(3, 0) };

        // 2026-10-12 is a Monday; the next Sunday is the 18th
        var next = BackupSchedule.Next(plan, Utc(2026, 10, 12, 10), Berlin);
        Assert.Equal(DayOfWeek.Sunday, TimeZoneInfo.ConvertTimeFromUtc(next, Berlin).DayOfWeek);
        Assert.Equal(Utc(2026, 10, 18, 1, 0), next);
    }

    [Fact]
    public void A_monthly_plan_on_the_31st_runs_on_the_last_day_of_a_shorter_month()
    {
        var plan = new BackupPlan { Schedule = ScheduleKind.Monthly, DayOfMonth = 31, TimeOfDay = new TimeOnly(4, 0) };

        // after the 31st of October the next one is the 30th of November (30 days)
        Assert.Equal(Utc(2026, 11, 30, 3, 0), BackupSchedule.Next(plan, Utc(2026, 10, 31, 12), Berlin));
    }

    [Fact]
    public void An_hourly_plan_runs_after_its_interval()
    {
        var plan = new BackupPlan { Schedule = ScheduleKind.Hourly, IntervalHours = 6 };
        Assert.Equal(Utc(2026, 10, 12, 16, 5), BackupSchedule.Next(plan, Utc(2026, 10, 12, 10, 5), Berlin));
    }

    // ------------------------------------------------------------------------------------------------ retention

    private static List<BackupFileInfo> Daily(int count, DateTime newest)
        => [.. Enumerable.Range(0, count).Select(i => new BackupFileInfo($"matsmtp-x-{newest.AddDays(-i):yyyyMMdd-HHmmss}.msbackup", newest.AddDays(-i), 100))];

    [Fact]
    public void The_newest_backups_are_kept_and_the_rest_can_go()
    {
        var files = Daily(10, Utc(2026, 10, 12, 1));
        var delete = BackupRetention.ToDelete(files, keepLast: 7, 0, 0, 0, Berlin);

        Assert.Equal(3, delete.Count);
        Assert.All(delete, f => Assert.True(f.CreatedUtc <= Utc(2026, 10, 12, 1).AddDays(-7)));
    }

    [Fact]
    public void Without_any_rule_nothing_is_deleted()
        => Assert.Empty(BackupRetention.ToDelete(Daily(30, Utc(2026, 10, 12, 1)), 0, 0, 0, 0, Berlin));

    [Fact]
    public void The_rules_add_up_the_newest_and_one_per_week_and_month()
    {
        var files = Daily(120, Utc(2026, 10, 12, 1));
        var keep = files.Except(BackupRetention.ToDelete(files, keepLast: 3, keepDaily: 0, keepWeekly: 4, keepMonthly: 3, Berlin)).ToList();

        Assert.Contains(files[0], keep);
        Assert.Contains(files[2], keep);

        // one per week for four weeks, one per month for three months: nowhere near all of them, and more than the newest three
        Assert.InRange(keep.Count, 7, 12);
    }

    [Fact]
    public void Two_backups_on_one_day_count_once_for_the_daily_rule()
    {
        var files = new List<BackupFileInfo>
        {
            new("matsmtp-x-20261012-230000.msbackup", Utc(2026, 10, 12, 21), 1),
            new("matsmtp-x-20261012-120000.msbackup", Utc(2026, 10, 12, 10), 1),
            new("matsmtp-x-20261011-120000.msbackup", Utc(2026, 10, 11, 10), 1),
        };

        var delete = BackupRetention.ToDelete(files, 0, keepDaily: 2, 0, 0, Berlin);
        Assert.Single(delete);
        Assert.Equal("matsmtp-x-20261012-120000.msbackup", delete[0].Name);
    }

    // ------------------------------------------------------------------------------------------------ names

    [Fact]
    public void File_names_carry_the_plan_and_can_be_recognised_again()
    {
        var name = BackupNames.Make("Täglich: System + Dateien", Utc(2026, 10, 12, 1, 30), encrypted: false);

        Assert.Equal("matsmtp-t-glich-system-dateien-20261012-013000.msbackup", name);
        Assert.True(BackupNames.IsBackup(name));
        Assert.Equal("t-glich-system-dateien", BackupNames.LabelOf(name));
        Assert.True(BackupNames.IsEncrypted(BackupNames.Make("x", Utc(2026, 1, 1), true)));
        Assert.False(BackupNames.IsBackup("../../etc/passwd"));
        Assert.False(BackupNames.IsBackup("notes.txt"));
    }

    // ------------------------------------------------------------------------------------------------ encryption

    private static async Task<byte[]> EncryptAsync(byte[] data, string passphrase)
    {
        using var output = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(data), output, passphrase);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    [InlineData(1024 * 1024 + 1)]
    [InlineData(3 * 1024 * 1024 + 17)]
    public async Task An_encrypted_backup_decrypts_to_the_same_bytes(int length)
    {
        var data = RandomNumberGenerator.GetBytes(length);
        var encrypted = await EncryptAsync(data, "Lektorat-2026");

        Assert.True(await BackupCrypto.IsEncryptedAsync(new MemoryStream(encrypted)));
        using var plain = new MemoryStream();
        await BackupCrypto.DecryptAsync(new MemoryStream(encrypted), plain, "Lektorat-2026");
        Assert.Equal(data, plain.ToArray());
    }

    [Fact]
    public async Task A_wrong_passphrase_is_refused()
    {
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(5000), "richtig");
        await Assert.ThrowsAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(new MemoryStream(encrypted), new MemoryStream(), "falsch"));
    }

    [Fact]
    public async Task A_damaged_or_cut_off_backup_is_refused()
    {
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 5), "geheim");

        var flipped = (byte[])encrypted.Clone();
        flipped[flipped.Length / 2] ^= 0xFF;
        await Assert.ThrowsAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(new MemoryStream(flipped), new MemoryStream(), "geheim"));

        // cut off after the first block: without the last block the end is not reached
        var cut = encrypted[..(8 + 16 + 8 + 4 + 1024 * 1024 + 16)];
        await Assert.ThrowsAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(new MemoryStream(cut), new MemoryStream(), "geheim"));
    }

    [Fact]
    public async Task A_plain_file_is_not_taken_for_an_encrypted_one()
        => Assert.False(await BackupCrypto.IsEncryptedAsync(new MemoryStream("PK\u0003\u0004 not encrypted"u8.ToArray())));
}
