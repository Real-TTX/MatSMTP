using System.Globalization;

namespace MatSmtp.Services;

/// <summary>How the interface writes dates, times, money and numbers: German, in the time zone of the installation.</summary>
public static class Fmt
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("de-DE");

    private static TimeZoneInfo _zone = TimeZoneInfo.Utc;

    public static TimeZoneInfo Zone => _zone;

    /// <summary>Called once at start-up with the time zone of the configuration; an unknown name falls back to UTC.</summary>
    public static void Configure(string timeZoneId)
    {
        try
        {
            _zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _zone = TimeZoneInfo.Utc;
        }
    }

    public static DateTime Local(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(Local(clock.GetUtcNow().UtcDateTime));

    public static DateTime UtcFromLocal(DateTime local)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _zone);

    public static string Date(DateOnly? date) => date is null ? "" : date.Value.ToString("dd.MM.yyyy", Culture);

    public static string Date(DateTime? utc) => utc is null ? "" : Local(utc.Value).ToString("dd.MM.yyyy", Culture);

    public static string Moment(DateTime? utc) => utc is null ? "" : Local(utc.Value).ToString("dd.MM.yyyy HH:mm", Culture);

    public static string Time(DateTime? utc) => utc is null ? "" : Local(utc.Value).ToString("HH:mm", Culture);

    /// <summary>An ISO date for the value of a date input.</summary>
    public static string Iso(DateOnly? date) => date is null ? "" : date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Money(decimal amount, string currency = "EUR")
        => amount.ToString("N2", Culture) + " " + (currency == "EUR" ? "€" : currency);

    public static string Number(decimal value, int decimals = 2) => value.ToString("N" + decimals, Culture);

    public static string Integer(long value) => value.ToString("N0", Culture);

    public static string FileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size.ToString("0.#", Culture)} {units[unit]}";
    }

    /// <summary>"vor 5 Minuten" up to a week, then the date.</summary>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - utc;
        if (span.TotalSeconds < 60) { return "gerade eben"; }
        if (span.TotalMinutes < 60) { return $"vor {(int)span.TotalMinutes} Min."; }
        if (span.TotalHours < 24) { return $"vor {(int)span.TotalHours} Std."; }
        if (span.TotalDays < 7) { return $"vor {(int)span.TotalDays} Tg."; }
        return Date(utc);
    }

    /// <summary>The first letters of the first two words of a name, for an avatar ("Sarah Keller" becomes "SK").</summary>
    public static string Initials(string? name)
    {
        var letters = string.Concat((name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length == 0 ? "?" : letters;
    }

    /// <summary>0 to 1 as "65 %".</summary>
    public static string Percent(decimal share) => Math.Round(share * 100, 0, MidpointRounding.AwayFromZero).ToString("0", Culture) + " %";

    /// <summary>A due date as people say it: "Heute", "Morgen", "Gestern", "in 3 Tagen", else the date.</summary>
    public static string Due(DateOnly? due, DateOnly today)
    {
        if (due is not { } d) { return ""; }
        var days = d.DayNumber - today.DayNumber;
        return days switch
        {
            0 => "Heute",
            1 => "Morgen",
            -1 => "Gestern",
            > 1 and <= 6 => $"in {days} Tagen",
            _ => Date(d),
        };
    }

    /// <summary>Two dates as a period: "01.07.2026 bis 31.12.2026", or one of them, or nothing.</summary>
    public static string Period(DateOnly? from, DateOnly? to)
        => (from, to) switch
        {
            (null, null) => "",
            ({ } a, null) => $"ab {Date(a)}",
            (null, { } b) => $"bis {Date(b)}",
            ({ } a, { } b) => $"{Date(a)} bis {Date(b)}",
        };
}
