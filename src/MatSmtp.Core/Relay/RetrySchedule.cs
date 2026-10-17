using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MatSmtp.Core.Relay;

/// <summary>
/// When the spooler tries a message again after a temporary error: the minutes of the list, one per attempt, the last one repeats
/// ("1, 5, 15, 60" = after 1, 5, 15, 60, 60 ... minutes) until the maximum number of attempts is reached.
/// </summary>
public static class RetrySchedule
{
    public const string Default = "1, 5, 15, 30, 60, 120, 240, 480";

    public static IReadOnlyList<int> Parse(string? list)
    {
        var minutes = (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : -1)
            .Where(m => m > 0)
            .ToList();
        return minutes.Count > 0 ? minutes : Parse(Default);
    }

    /// <summary>The wait after the failed attempt number <paramref name="attempt"/> (1 = the first attempt failed).</summary>
    public static TimeSpan Delay(string? list, int attempt)
    {
        var minutes = Parse(list);
        return TimeSpan.FromMinutes(minutes[Math.Clamp(attempt - 1, 0, minutes.Count - 1)]);
    }

    /// <summary>The first entry that is not a number of minutes, or null.</summary>
    public static string? Problem(string? list)
    {
        foreach (var s in (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m <= 0 || m > 7 * 24 * 60)
            {
                return $"„{s}“ ist keine Wartezeit in Minuten (1 bis 10080).";
            }
        }

        return null;
    }
}

/// <summary>
/// The signature of a webhook call: HMAC-SHA256 over "<c>timestamp.body</c>" with the secret of the webhook, as hex, in the header
/// <c>X-MatSMTP-Signature: t=1760000000,v1=...</c>. The receiver computes the same and compares; the timestamp lets it refuse old calls.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "X-MatSMTP-Signature";

    public static string Compute(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + body));
        return Convert.ToHexStringLower(mac);
    }

    public static string HeaderValue(string secret, long timestamp, string body) => $"t={timestamp.ToString(CultureInfo.InvariantCulture)},v1={Compute(secret, timestamp, body)}";

    public static bool Verify(string secret, string headerValue, string body)
    {
        var parts = headerValue.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (!parts.TryGetValue("t", out var t) || !parts.TryGetValue("v1", out var v) || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, timestamp, body)), Encoding.ASCII.GetBytes(v.ToLowerInvariant()));
    }
}
