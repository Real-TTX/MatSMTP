using System.Text.RegularExpressions;

namespace MatSmtp.Core.Relay;

/// <summary>
/// The patterns of a rule for addresses and texts. A list holds several entries, separated by commas, semicolons or line breaks; one matching entry is enough.
/// An entry is
/// <list type="bullet">
/// <item><c>*</c>: everything (also an empty sender, the bounce address &lt;&gt;),</item>
/// <item><c>example.com</c> or <c>@example.com</c>: every address of this domain,</item>
/// <item><c>*.example.com</c>: every address of a subdomain,</item>
/// <item><c>app-*@example.com</c>, <c>?</c> for one character: a wildcard over the whole address,</item>
/// <item><c>/^noreply\d+@/</c>: a regular expression between slashes,</item>
/// <item>anything else: the address itself.</item>
/// </list>
/// Upper and lower case never matter.
/// </summary>
public static class AddressPattern
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly char[] Separators = [',', ';', '\n', '\r'];

    /// <summary>The entries of a list, trimmed, without empty ones.</summary>
    public static IReadOnlyList<string> Split(string? list)
        => string.IsNullOrWhiteSpace(list) ? [] : [.. list.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Whether one entry of <paramref name="list"/> matches <paramref name="address"/>. An empty list matches nothing.</summary>
    public static bool MatchesAny(string? list, string? address) => Split(list).Any(entry => Matches(entry, address));

    /// <summary>Whether one entry of <paramref name="list"/> matches one of <paramref name="addresses"/>.</summary>
    public static bool MatchesAny(string? list, IEnumerable<string> addresses)
    {
        var entries = Split(list);
        return addresses.Any(a => entries.Any(entry => Matches(entry, a)));
    }

    public static bool Matches(string entry, string? address)
    {
        var value = (address ?? "").Trim();
        if (entry == "*")
        {
            return true;
        }

        if (IsRegex(entry))
        {
            try
            {
                return Regex.IsMatch(value, entry[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        if (value.Length == 0)
        {
            return false;
        }

        if (entry.Contains('*') || entry.Contains('?'))
        {
            // "*.example.com" is meant for the domain part: a subdomain of example.com.
            var target = !entry.Contains('@') && value.Contains('@') ? DomainOf(value) : value;
            return Regex.IsMatch(target, "^" + Regex.Escape(entry).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }

        if (entry.StartsWith('@'))
        {
            return string.Equals(DomainOf(value), entry[1..], StringComparison.OrdinalIgnoreCase);
        }

        if (!entry.Contains('@') && value.Contains('@'))
        {
            return string.Equals(DomainOf(value), entry, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(value, entry, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a text (a subject) contains one of the entries; <c>/.../</c> is a regular expression, <c>*</c> matches everything.</summary>
    public static bool TextMatchesAny(string? list, string? text)
    {
        var value = text ?? "";
        foreach (var entry in Split(list))
        {
            if (entry == "*")
            {
                return true;
            }

            if (IsRegex(entry))
            {
                try
                {
                    if (Regex.IsMatch(value, entry[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // a pattern that takes this long counts as no match
                }

                continue;
            }

            if (value.Contains(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first entry that is not a valid pattern, with the reason; null when the list is fine.</summary>
    public static string? Problem(string? list)
    {
        foreach (var entry in Split(list))
        {
            if (!IsRegex(entry))
            {
                if (entry.Contains(' '))
                {
                    return $"„{entry}“ enthält ein Leerzeichen. Trenne mehrere Einträge mit Komma oder Zeilenumbruch.";
                }

                continue;
            }

            try
            {
                _ = new Regex(entry[1..^1], RegexOptions.None, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return $"„{entry}“ ist kein gültiger regulärer Ausdruck: {ex.Message}";
            }
        }

        return null;
    }

    public static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? "" : address[(at + 1)..].Trim('>', ' ');
    }

    public static string LocalPartOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? address : address[..at];
    }

    private static bool IsRegex(string entry) => entry.Length > 2 && entry[0] == '/' && entry[^1] == '/';
}
