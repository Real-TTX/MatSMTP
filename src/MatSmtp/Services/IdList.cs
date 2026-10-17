namespace MatSmtp.Services;

/// <summary>The keys of a picker with several choices: written and read as "3, 7, 12".</summary>
public static class IdList
{
    public static List<long> Parse(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => long.TryParse(s, out var id) ? id : 0).Where(id => id > 0).Distinct()];

    public static string Join(IEnumerable<long> ids) => string.Join(", ", ids);
}
