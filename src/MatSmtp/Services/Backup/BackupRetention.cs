namespace MatSmtp.Services.Backup;

/// <summary>A backup file on a target, as far as the retention rules need to know.</summary>
public sealed record BackupFileInfo(string Name, DateTime CreatedUtc, long Size);

/// <summary>
/// Which backups of a plan are kept. The rules add up: the newest N are kept, and the newest of each of the last D days, W weeks and M months.
/// Everything else can go. A rule that is 0 does not keep anything; with all rules 0 nothing is deleted (a plan without a rule must not eat its own history).
/// </summary>
public static class BackupRetention
{
    public static IReadOnlyList<BackupFileInfo> ToDelete(IEnumerable<BackupFileInfo> files, int keepLast, int keepDaily, int keepWeekly, int keepMonthly, TimeZoneInfo zone)
    {
        var all = files.OrderByDescending(f => f.CreatedUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        if (keepLast <= 0 && keepDaily <= 0 && keepWeekly <= 0 && keepMonthly <= 0)
        {
            return [];
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in all.Take(Math.Max(0, keepLast)))
        {
            keep.Add(f.Name);
        }

        KeepNewestPer(all, keep, keepDaily, f => Local(f, zone).Date);
        KeepNewestPer(all, keep, keepWeekly, f => StartOfWeek(Local(f, zone).Date));
        KeepNewestPer(all, keep, keepMonthly, f => new DateTime(Local(f, zone).Year, Local(f, zone).Month, 1));
        return [.. all.Where(f => !keep.Contains(f.Name))];
    }

    private static void KeepNewestPer(List<BackupFileInfo> newestFirst, HashSet<string> keep, int count, Func<BackupFileInfo, DateTime> period)
    {
        if (count <= 0)
        {
            return;
        }

        var seen = new HashSet<DateTime>();
        foreach (var f in newestFirst)
        {
            var p = period(f);
            if (seen.Count >= count && !seen.Contains(p))
            {
                break;
            }

            if (seen.Add(p))
            {
                keep.Add(f.Name);
            }
        }
    }

    private static DateTime Local(BackupFileInfo f, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(f.CreatedUtc, DateTimeKind.Utc), zone);

    private static DateTime StartOfWeek(DateTime date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
