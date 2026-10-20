using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MatSmtp.Core.Relay;

/// <summary>What the DNS says about the public key of a DKIM key.</summary>
public enum DkimDnsState
{
    /// <summary>Not looked up yet.</summary>
    Unchecked,
    /// <summary>The TXT record is there and carries this key.</summary>
    Ok,
    /// <summary>No DKIM record under the name.</summary>
    Missing,
    /// <summary>A DKIM record is there, with another key (an old one, or a typo).</summary>
    Different,
    /// <summary>A DKIM record with an empty key (<c>p=</c>): the domain says that nothing under the name signs; often a wildcard <c>*._domainkey</c>.</summary>
    Revoked,
    /// <summary>The lookup itself failed (no answer from the DNS server).</summary>
    Error,
}

/// <summary>
/// The DKIM part that needs no web and no database: names, the TXT record, keys, which key signs a sender. A key is RSA (what every receiver checks); the
/// record is <c>v=DKIM1; k=rsa; p=&lt;public key&gt;</c> under <c>&lt;selector&gt;._domainkey.&lt;domain&gt;</c>.
/// </summary>
public static partial class Dkim
{
    /// <summary>The headers that are signed when the message has them. From is required by the standard; the rest is what a receiver and DMARC look at.</summary>
    public static readonly string[] SignedHeaders =
    [
        "From", "Reply-To", "Subject", "Date", "Message-ID", "To", "Cc", "MIME-Version", "Content-Type", "Content-Transfer-Encoding",
        "In-Reply-To", "References", "List-Id", "List-Unsubscribe", "List-Unsubscribe-Post",
    ];

    /// <summary>The name of the TXT record.</summary>
    public static string RecordName(string selector, string domain) => $"{selector}._domainkey.{domain}";

    /// <summary>The value of the TXT record.</summary>
    public static string RecordValue(string publicKey) => $"v=DKIM1; k=rsa; p={publicKey}";

    /// <summary>A domain name as it is written in the record (lower case, no dot at the end), or null when it is not one.</summary>
    public static string? NormalizeDomain(string? domain)
    {
        var d = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        return d.Length is > 0 and <= 253 && DomainRegex().IsMatch(d) ? d : null;
    }

    /// <summary>A selector: letters, digits and hyphens, parts separated by dots.</summary>
    public static bool IsSelector(string? selector) => !string.IsNullOrEmpty(selector) && selector.Length <= 63 && SelectorRegex().IsMatch(selector);

    /// <summary>The domain of an address (after the last @), lower case; null when there is none.</summary>
    public static string? DomainOf(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        return at < 0 ? null : NormalizeDomain(address![(at + 1)..]);
    }

    /// <summary>
    /// The key that signs mail from <paramref name="fromDomain"/>: the key of the domain itself, else the key of the nearest parent domain that is meant for its
    /// subdomains too (DMARC accepts the organisational domain in relaxed mode). Of several keys for one domain the newest wins (that is how a key is changed).
    /// </summary>
    public static T? Choose<T>(IEnumerable<T> keys, string fromDomain, Func<T, string> domain, Func<T, bool> forSubdomains, Func<T, long> order) where T : class
    {
        var candidates = keys.ToList();
        for (var d = fromDomain; d.Length > 0; d = Parent(d))
        {
            var exact = string.Equals(d, fromDomain, StringComparison.OrdinalIgnoreCase);
            var hit = candidates.Where(k => string.Equals(domain(k), d, StringComparison.OrdinalIgnoreCase) && (exact || forSubdomains(k))).MaxBy(order);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static string Parent(string domain)
    {
        var dot = domain.IndexOf('.');
        // A top level domain alone (com, de) never signs.
        return dot < 0 || domain.IndexOf('.', dot + 1) < 0 ? "" : domain[(dot + 1)..];
    }

    /// <summary>A new RSA key: the private key as PEM (PKCS#1) and the public key as base64, as it goes into the record.</summary>
    public static (string PrivateKeyPem, string PublicKey) Generate(int bits)
    {
        using var rsa = RSA.Create(bits);
        return (rsa.ExportRSAPrivateKeyPem(), Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Reads a private key someone already has (PEM, PKCS#1 or PKCS#8): its size and its public key. Null when it is no RSA private key.</summary>
    public static (string PrivateKeyPem, string PublicKey, int Bits)? Import(string pem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            // Kept in one form, whatever came in: the signer reads PKCS#1.
            return (rsa.ExportRSAPrivateKeyPem(), Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), rsa.KeySize);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The tags of a record (<c>v=DKIM1; k=rsa; p=...</c>); the strings of one TXT record are joined before.</summary>
    public static Dictionary<string, string> Tags(string record)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in record.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                // Whitespace inside a value is allowed (a long key broken over lines) and means nothing.
                tags[part[..eq].Trim()] = WhitespaceRegex().Replace(part[(eq + 1)..], "");
            }
        }

        return tags;
    }

    /// <summary>Compares what the DNS has under the name (one entry per TXT record) with the public key.</summary>
    public static DkimDnsState Compare(IEnumerable<string> records, string publicKey)
    {
        var other = false;
        var revoked = false;
        foreach (var record in records)
        {
            var tags = Tags(record);
            if (!tags.TryGetValue("p", out var p) || (tags.TryGetValue("v", out var v) && !string.Equals(v, "DKIM1", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (p == publicKey)
            {
                return DkimDnsState.Ok;
            }

            revoked |= p.Length == 0;
            other |= p.Length > 0;
        }

        return other ? DkimDnsState.Different : revoked ? DkimDnsState.Revoked : DkimDnsState.Missing;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$")]
    private static partial Regex DomainRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$")]
    private static partial Regex SelectorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
