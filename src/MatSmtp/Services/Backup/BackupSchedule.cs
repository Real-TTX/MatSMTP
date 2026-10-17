using MatSmtp.Data.Entities;

namespace MatSmtp.Services.Backup;

/// <summary>When a backup plan runs next. The times of a plan are the clock of the installation (its time zone), the answer is UTC.</summary>
public static class BackupSchedule
{
    /// <summary>The first run after <paramref name="afterUtc"/>.</summary>
    public static DateTime Next(BackupPlan plan, DateTime afterUtc, TimeZoneInfo zone)
    {
        var after = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        if (plan.Schedule == ScheduleKind.Hourly)
        {
            return after.AddHours(Math.Clamp(plan.IntervalHours, 1, 168));
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(after, zone);
        var date = DateOnly.FromDateTime(local);
        for (var i = 0; i < 400; i++)
        {
            var day = date.AddDays(i);
            if (!Matches(plan, day))
            {
                continue;
            }

            var candidate = ToUtc(day, plan.TimeOfDay, zone);
            if (candidate > after)
            {
                return candidate;
            }
        }

        return after.AddDays(1);
    }

    private static bool Matches(BackupPlan plan, DateOnly day) => plan.Schedule switch
    {
        ScheduleKind.Daily => true,
        ScheduleKind.Weekly => day.DayOfWeek == plan.DayOfWeek,

        // "the 31st" in a month with 30 days means the last day of that month
        ScheduleKind.Monthly => day.Day == Math.Min(Math.Clamp(plan.DayOfMonth, 1, 31), DateTime.DaysInMonth(day.Year, day.Month)),
        _ => false,
    };

    /// <summary>A time that does not exist (the hour that is skipped when the clocks go forward) runs an hour later.</summary>
    private static DateTime ToUtc(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static string Describe(BackupPlan plan) => plan.Schedule switch
    {
        ScheduleKind.Hourly => plan.IntervalHours == 1 ? "stündlich" : $"alle {plan.IntervalHours} Stunden",
        ScheduleKind.Daily => $"täglich um {plan.TimeOfDay:HH\\:mm} Uhr",
        ScheduleKind.Weekly => $"jeden {DayName(plan.DayOfWeek)} um {plan.TimeOfDay:HH\\:mm} Uhr",
        _ => $"monatlich am {plan.DayOfMonth}. um {plan.TimeOfDay:HH\\:mm} Uhr",
    };

    public static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Montag",
        DayOfWeek.Tuesday => "Dienstag",
        DayOfWeek.Wednesday => "Mittwoch",
        DayOfWeek.Thursday => "Donnerstag",
        DayOfWeek.Friday => "Freitag",
        DayOfWeek.Saturday => "Samstag",
        _ => "Sonntag",
    };
}
