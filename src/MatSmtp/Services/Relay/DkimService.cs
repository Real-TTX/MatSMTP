using System.Text;
using DnsClient;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Cryptography;

namespace MatSmtp.Services.Relay;

/// <summary>What signing did to a message: the key that signed, or why a key that should have signed could not.</summary>
public sealed record DkimOutcome(DkimKey? Key, string? Problem)
{
    public static readonly DkimOutcome None = new(null, null);
}

/// <summary>
/// The DKIM keys: the list and the page (create with a new or an imported key, change, delete), the look into the DNS, and the signature of an outgoing message
/// (<see cref="SignAsync"/>, called by the spooler as the very last change before sending).
/// </summary>
public sealed class DkimService(MsDbContext db, SecretProtector secrets, ILookupClient dns, TimeProvider clock, ILogger<DkimService> logger)
{
    /// <summary>The list of the page: search, filter, sort and page of the request.</summary>
    public Task<ListResult<DkimKey>> ListAsync(HttpRequest request, CancellationToken ct)
    {
        var spec = new ListSpec<DkimKey>
        {
            Id = "dkim",
            Action = "/Dkim",
            Label = "DKIM-Schlüssel",
            Noun = "Schlüssel",
            SearchPlaceholder = "Domain oder Selektor ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(k => EF.Functions.Like(k.Domain, like, Like.Escape) || EF.Functions.Like(k.Selector, like, Like.Escape) || EF.Functions.Like(k.Description ?? "", like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<DkimKey>("status", "Status", [new("aktiv", "Signiert"), new("aus", "Abgeschaltet")],
                    (q, v) => v == "aus" ? q.Where(k => !k.IsEnabled) : q.Where(k => k.IsEnabled)),
                new FilterSpec<DkimKey>("dns", "DNS", [new("ok", "Eintrag stimmt"), new("problem", "Eintrag fehlt oder stimmt nicht"), new("offen", "Nicht geprüft")],
                    (q, v) => v switch
                    {
                        "ok" => q.Where(k => k.DnsState == DkimDnsState.Ok),
                        "offen" => q.Where(k => k.DnsState == DkimDnsState.Unchecked),
                        _ => q.Where(k => k.DnsState != DkimDnsState.Ok && k.DnsState != DkimDnsState.Unchecked),
                    }),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<DkimKey>, bool, IOrderedQueryable<DkimKey>>)>
            {
                ["domain"] = ("Domain", (q, d) => d ? q.OrderByDescending(k => k.Domain).ThenByDescending(k => k.Id) : q.OrderBy(k => k.Domain).ThenByDescending(k => k.Id)),
                ["used"] = ("Zuletzt signiert", (q, d) => d ? q.OrderByDescending(k => k.LastUsedDate) : q.OrderBy(k => k.LastUsedDate)),
            },
            DefaultSort = "domain",
        };

        return ListBuilder.RunAsync(db.DkimKey.AsNoTracking(), request, spec, ct);
    }

    public Task<DkimKey?> FindAsync(long id, CancellationToken ct = default) => db.DkimKey.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct);

    /// <summary>Is the selector of the domain taken by another key?</summary>
    public Task<bool> SelectorTakenAsync(string domain, string selector, long? exceptId, CancellationToken ct = default)
        => db.DkimKey.AnyAsync(k => k.Domain == domain && k.Selector == selector && k.Id != exceptId, ct);

    /// <summary>
    /// Creates (<paramref name="id"/> null) or changes a key with <paramref name="apply"/>. A new key gets <paramref name="privateKeyPem"/> (protected with the
    /// key ring); a change of the domain or the selector forgets what the DNS said, because the record has another name now.
    /// </summary>
    public async Task<DkimKey> SaveAsync(long? id, Action<DkimKey> apply, (string PrivateKeyPem, string PublicKey, int Bits)? newKey, CancellationToken ct = default)
    {
        var key = id is { } existing ? await db.DkimKey.FirstAsync(k => k.Id == existing, ct) : db.DkimKey.Add(new DkimKey()).Entity;
        var name = Dkim.RecordName(key.Selector, key.Domain);
        apply(key);
        if (newKey is { } k)
        {
            key.PrivateKeyProtected = secrets.Protect(k.PrivateKeyPem);
            key.PublicKey = k.PublicKey;
            key.KeyBits = k.Bits;
        }

        if (newKey is not null || !string.Equals(name, Dkim.RecordName(key.Selector, key.Domain), StringComparison.OrdinalIgnoreCase))
        {
            key.DnsState = DkimDnsState.Unchecked;
            key.DnsCheckedDate = null;
            key.DnsDetail = null;
        }

        await db.SaveChangesAsync(ct);
        return key;
    }

    /// <summary>Deletes a key (softly); null when there is none.</summary>
    public async Task<DkimKey?> DeleteAsync(long id, CancellationToken ct = default)
    {
        var key = await db.DkimKey.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return null;
        }

        db.DkimKey.Remove(key);
        await db.SaveChangesAsync(ct);
        return key;
    }

    /// <summary>Looks up the TXT record of the key and keeps what came out (the list shows it).</summary>
    public async Task<DkimKey?> CheckDnsAsync(long id, CancellationToken ct = default)
    {
        var key = await db.DkimKey.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return null;
        }

        var name = Dkim.RecordName(key.Selector, key.Domain);
        try
        {
            var answer = await dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            if (answer.HasError && answer.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
            {
                key.DnsState = DkimDnsState.Error;
                key.DnsDetail = $"Der DNS-Server hat mit einem Fehler geantwortet: {answer.ErrorMessage}";
            }
            else
            {
                var records = answer.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
                key.DnsState = Dkim.Compare(records, key.PublicKey);
                key.DnsDetail = key.DnsState switch
                {
                    DkimDnsState.Ok => "Der Eintrag ist veröffentlicht und enthält diesen Schlüssel.",
                    DkimDnsState.Different => $"Unter dem Namen steht ein anderer Schlüssel: {Shorten(records.FirstOrDefault(r => r.Contains("p=", StringComparison.Ordinal)) ?? records[0])}",
                    DkimDnsState.Revoked => "Unter dem Namen steht ein leerer Schlüssel (p=). Damit erklärt die Domain, dass unter diesem Selektor nichts signiert; oft gilt ein Platzhalter *._domainkey für alle Selektoren. Lege den Eintrag mit diesem Schlüssel an, er geht dem Platzhalter vor.",
                    _ => records.Count == 0 ? "Unter dem Namen gibt es keinen TXT-Eintrag (oder er ist noch nicht verteilt; das kann je nach Anbieter bis zu einer Stunde dauern)." : "Unter dem Namen steht kein DKIM-Eintrag.",
                };
            }
        }
        catch (DnsResponseException ex)
        {
            key.DnsState = DkimDnsState.Error;
            key.DnsDetail = $"Keine Antwort vom DNS-Server: {ex.Message}";
        }

        key.DnsCheckedDate = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return key;
    }

    private static string Shorten(string text) => text.Length > 120 ? text[..120] + " …" : text;

    /// <summary>
    /// Signs the message with the key of its From domain, if there is an active one. The message is prepared for 7 bit first: a server without 8BITMIME would
    /// otherwise make MailKit re-encode the body, and the signature would no longer fit.
    /// </summary>
    public async Task<DkimOutcome> SignAsync(MimeMessage message, CancellationToken ct = default)
    {
        var domain = Dkim.DomainOf(message.From.Mailboxes.FirstOrDefault()?.Address);
        if (domain is null)
        {
            return DkimOutcome.None;
        }

        var keys = await db.DkimKey.AsNoTracking().Where(k => k.IsEnabled).ToListAsync(ct);
        var key = Dkim.Choose(keys, domain, k => k.Domain, k => k.IncludeSubdomains, k => k.Id);
        if (key is null)
        {
            return DkimOutcome.None;
        }

        var pem = secrets.Unprotect(key.PrivateKeyProtected);
        if (pem.Length == 0)
        {
            logger.LogWarning("The private DKIM key {Selector}._domainkey.{Domain} can not be read (key ring lost?).", key.Selector, key.Domain);
            return new DkimOutcome(null, $"Der private Schlüssel für {key.Domain} ist nicht lesbar (Schlüsselbund verloren?). Die Nachricht geht ohne DKIM-Signatur hinaus.");
        }

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(pem));
        var signer = new DkimSigner(stream, key.Domain, key.Selector, DkimSignatureAlgorithm.RsaSha256)
        {
            HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
        };
        message.Prepare(EncodingConstraint.SevenBit);
        var headers = Dkim.SignedHeaders.Where(h => message.Headers.Contains(h)).ToList();
        signer.Sign(message, headers);

        var now = clock.GetUtcNow().UtcDateTime;
        await db.DkimKey.Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedDate, now), ct);
        return new DkimOutcome(key, null);
    }

    /// <summary>A selector for a new key: the month it was made, so the next one gets another name.</summary>
    public string SuggestSelector() => $"matsmtp{clock.GetUtcNow():yyyyMM}";
}
