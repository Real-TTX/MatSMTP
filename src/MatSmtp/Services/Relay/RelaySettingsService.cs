using System.Net.Mail;
using System.Text.RegularExpressions;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Data.Entities;

namespace MatSmtp.Services.Relay;

/// <summary>The few settings a fresh installation needs (the platform form of Mattiverse): the receiver and the default account it sends through.</summary>
public sealed record RelaySettings(
    string Hostname,
    string DefaultSenderDomain,
    string TrustedNetworks,
    bool Paused,
    string? RelayHost,
    int RelayPort,
    SmtpSecurity RelaySecurity,
    string? RelayUsername,
    bool RelayPasswordSet,
    string? RelayFrom);

/// <summary>A change of <see cref="RelaySettings"/>: only what is not null changes. <see cref="RelayPassword"/> <c>""</c> clears the password.</summary>
public sealed class RelaySettingsChange
{
    public string? Hostname { get; set; }

    public string? DefaultSenderDomain { get; set; }

    public string? TrustedNetworks { get; set; }

    public bool? Paused { get; set; }

    public string? RelayHost { get; set; }

    public int? RelayPort { get; set; }

    public SmtpSecurity? RelaySecurity { get; set; }

    public string? RelayUsername { get; set; }

    public string? RelayPassword { get; set; }

    public string? RelayFrom { get; set; }
}

/// <summary>
/// Reads and changes the settings of <see cref="RelaySettings"/>: the receiver in the configuration file, the default account in the database (made as
/// "Standard" when there is none and a server is given). The pages of the receiver and of the accounts show the same values.
/// </summary>
public sealed partial class RelaySettingsService(ConfigStore config, AccountService accounts, SecretProtector secrets, CertificateService certificates)
{
    [GeneratedRegex(@"^[A-Za-z0-9.\-]{1,253}$")]
    private static partial Regex HostnamePattern();

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$")]
    private static partial Regex DomainPattern();

    /// <summary>Whether <paramref name="domain"/> can be the default domain of senders: a domain with a dot, or empty (off).</summary>
    public static bool IsValidDomain(string? domain) => string.IsNullOrEmpty(domain) || DomainPattern().IsMatch(domain);

    public async Task<RelaySettings> GetAsync(CancellationToken ct = default)
    {
        var c = config.Current;
        var account = await accounts.DefaultAsync(ct);
        return new RelaySettings(
            c.Smtp.Hostname,
            c.Smtp.DefaultSenderDomain,
            c.Smtp.TrustedNetworks,
            c.Delivery.Paused,
            account?.Host,
            account?.Port ?? 587,
            account?.Security ?? SmtpSecurity.Auto,
            account?.UserName,
            !string.IsNullOrEmpty(account?.PasswordProtected),
            account?.ForceFromAddress);
    }

    /// <summary>Sets the default domain of senders (empty switches it off); false when it is no domain.</summary>
    public bool SetDefaultSenderDomain(string? domain)
    {
        var value = (domain ?? "").Trim().TrimStart('@').ToLowerInvariant();
        if (!IsValidDomain(value))
        {
            return false;
        }

        config.Update(c => c.Smtp.DefaultSenderDomain = value);
        return true;
    }

    /// <summary>
    /// Checks the change and saves it when everything is right. The errors are per property name of <see cref="RelaySettingsChange"/>; empty means saved.
    /// <paramref name="t"/> picks the German or the English text.
    /// </summary>
    public async Task<Dictionary<string, string>> ApplyAsync(RelaySettingsChange change, Func<string, string, string> t, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var hostname = change.Hostname?.Trim();
        if (hostname is not null && !HostnamePattern().IsMatch(hostname))
        {
            errors[nameof(change.Hostname)] = t("Ein Hostname: Buchstaben, Ziffern, Punkt und Bindestrich.", "A host name: letters, digits, dot and dash.");
        }

        var domain = change.DefaultSenderDomain?.Trim().TrimStart('@').ToLowerInvariant();
        if (domain is not null && !IsValidDomain(domain))
        {
            errors[nameof(change.DefaultSenderDomain)] = t("Eine Domain wie example.com, oder leer.", "A domain like example.com, or empty.");
        }

        if (change.TrustedNetworks is not null && IpNetworks.Problem(change.TrustedNetworks) is { } problem)
        {
            errors[nameof(change.TrustedNetworks)] = problem;
        }

        var account = await accounts.DefaultAsync(ct);
        var host = change.RelayHost?.Trim();
        var relayChanged = change.RelayHost is not null || change.RelayPort is not null || change.RelaySecurity is not null || change.RelayUsername is not null
            || change.RelayPassword is not null || change.RelayFrom is not null;
        if (relayChanged && string.IsNullOrEmpty(host ?? account?.Host))
        {
            errors[nameof(change.RelayHost)] = t("Bitte den Server des Kontos angeben, über das MatSMTP versendet.", "Name the server of the account MatSMTP sends through.");
        }

        if (change.RelayPort is < 1 or > 65535)
        {
            errors[nameof(change.RelayPort)] = t("Ein Port zwischen 1 und 65535.", "A port between 1 and 65535.");
        }

        var from = change.RelayFrom?.Trim();
        if (!string.IsNullOrEmpty(from) && !(MailAddress.TryCreate(from, out var mail) && mail.Address == from))
        {
            errors[nameof(change.RelayFrom)] = t("Eine E-Mail-Adresse, oder leer.", "An e-mail address, or empty.");
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        var hostChanged = hostname is not null && !string.Equals(hostname, config.Current.Smtp.Hostname, StringComparison.OrdinalIgnoreCase);
        config.Update(c =>
        {
            c.Smtp.Hostname = hostname ?? c.Smtp.Hostname;
            c.Smtp.DefaultSenderDomain = domain ?? c.Smtp.DefaultSenderDomain;
            if (change.TrustedNetworks is not null)
            {
                c.Smtp.TrustedNetworks = string.Join(", ", IpNetworks.Parse(change.TrustedNetworks).Select(n => n.ToString()));
            }

            c.Delivery.Paused = change.Paused ?? c.Delivery.Paused;
        });

        if (hostChanged && !certificates.IsUploaded)
        {
            // The self-signed certificate carries the host name.
            certificates.RenewSelfSigned();
        }

        if (relayChanged)
        {
            await accounts.SaveAsync(account?.Id, a =>
            {
                if (account is null)
                {
                    a.Name = AccountService.DefaultName;
                    a.IsDefault = true;
                    a.IsEnabled = true;
                }

                a.Host = host ?? a.Host;
                a.Port = change.RelayPort ?? a.Port;
                a.Security = change.RelaySecurity ?? a.Security;
                if (change.RelayUsername is not null)
                {
                    a.UserName = change.RelayUsername.Trim().Length == 0 ? null : change.RelayUsername.Trim();
                }

                if (change.RelayPassword is not null)
                {
                    a.PasswordProtected = change.RelayPassword.Length == 0 ? null : secrets.Protect(change.RelayPassword);
                }

                if (from is not null)
                {
                    a.ForceFromAddress = from.Length == 0 ? null : from;
                }
            }, ct);
        }

        return errors;
    }
}
