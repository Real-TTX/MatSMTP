using System.Globalization;
using MatPlatform;
using MatSmtp.Api;
using MatSmtp.Configuration;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;

namespace MatSmtp.Platform;

/// <summary>
/// The bindings of <c>mail.smtp</c> for MatPlatform (<c>/api/bindings</c> of a broker such as Mattiverse, and the Connections area): a binding is a relay user of the
/// API, made by <see cref="RelayUserService"/> like the ones of <c>/api/v1</c> (id = the consumer id, its name, <c>managedBy</c>, its one allowed sender), not a key
/// in the store of MatPlatform. This class only translates: the values are where the consumer reaches the receiver (the host of MatSMTP's internal address, the
/// port and the TLS mode that <c>/api/v1/status</c> names), the relay user, its password when it was made now, and its sender.
/// </summary>
public sealed class SmtpBindings(RelayUserService relayUsers, ConfigStore config, ActivityLogger log, ICurrentUser current) : IMatBindingHandler
{
    /// <summary>The parameter of a consumer: the part of its sender before the <c>@</c>.</summary>
    public const string SenderParam = "sender";

    public string Capability => MatCapabilities.MailSmtp;

    public async Task<IReadOnlyList<MatBindingInfo>> ListAsync(CancellationToken ct)
        => [.. (await relayUsers.ListBindingsAsync(ct)).Select(b => new MatBindingInfo(Capability, b.Consumer, b.Name, b.ManagedBy, Utc(b.Since), b.LastUsed is { } used ? Utc(used) : null))];

    public async Task<MatBindResult> BindAsync(MatBindRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await relayUsers.UpsertBindingAsync(request.Consumer.Id, request.Consumer.Name, request.Param(SenderParam, request.Consumer.Id), request.ManagedBy, request.Renew, ct);

        // MatPlatform has checked the consumer id already; what is left is two binds of one consumer at the same moment, which the unique index refused.
        var user = result.User ?? throw new InvalidOperationException(result.Error);
        var what = result.Created ? "angelegt" : result.Password is null ? "geändert" : "geändert, neues Passwort";
        await LogAsync($"Zugang für eine Verbindung {what}", user, request.ManagedBy);

        var smtp = config.Current.Smtp;
        var (port, tls) = ManagementApi.SendPort(smtp);
        return new MatBindResult(new Dictionary<string, string?>
        {
            ["host"] = HostOf(request.Provider.InternalUrl) ?? smtp.Hostname,
            ["port"] = port.ToString(CultureInfo.InvariantCulture),
            ["username"] = user.UserName,
            ["password"] = result.Password,
            ["from"] = user.AllowedSenders,
            ["tls"] = tls,
        });
    }

    public async Task<bool> UnbindAsync(string consumer, string? managedBy, CancellationToken ct)
    {
        var user = await relayUsers.RemoveBindingAsync(consumer, managedBy, ct);
        if (user is null)
        {
            return false;
        }

        await LogAsync("Zugang einer Verbindung gelöscht", user, managedBy);
        return true;
    }

    /// <summary>The host of an address on the shared network (<c>http://mattiverse-matsmtp-matsmtp:2525</c> → <c>mattiverse-matsmtp-matsmtp</c>), or null.</summary>
    public static string? HostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0 ? uri.IdnHost : null;

    /// <summary>A person in the Connections area is in the log by name; a broker by its <c>managedBy</c>.</summary>
    private async Task LogAsync(string action, RelayUser user, string? managedBy)
    {
        if (current.IsAuthenticated)
        {
            await log.LogAsync(action, nameof(RelayUser), user.Id, user.UserName);
            return;
        }

        using (current.RunAs(null, "Plattform" + (string.IsNullOrEmpty(managedBy) ? "" : $" ({managedBy})")))
        {
            await log.LogAsync(action, nameof(RelayUser), user.Id, user.UserName);
        }
    }

    private static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));
}
