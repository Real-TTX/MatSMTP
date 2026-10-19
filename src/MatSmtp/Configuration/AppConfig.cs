using System.Text.Json.Serialization;

namespace MatSmtp.Configuration;

/// <summary>
/// The configuration (JSON, <c>{data}/config/app.json</c>): how this installation is deployed and how the relay receives and delivers. The rules, accounts,
/// webhooks and messages are logic and live in the database. The section <see cref="Server"/> and the ports of <see cref="Smtp"/> can be overridden by
/// environment variables (<c>MATSMTP__Server__PublicUrl</c>, <c>MATSMTP__Smtp__Port</c>); the rest is edited on the administration pages.
/// </summary>
public sealed class AppConfig
{
    public ServerConfig Server { get; set; } = new();

    public SmtpConfig Smtp { get; set; } = new();

    public DeliveryConfig Delivery { get; set; } = new();

    public SecurityConfig Security { get; set; } = new();

    public BackupConfig Backup { get; set; } = new();
}

public sealed class ServerConfig
{
    /// <summary>The address people use, for the links that are handed on ("https://smtp.example.de"). Empty: taken from the request.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>"Europe/Berlin": the time zone of the installation, for dates and schedules.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>The cookie is marked secure when the site is reached over HTTPS (also behind a reverse proxy that says so).</summary>
    public bool ForceSecureCookie { get; set; }
}

/// <summary>How the relay receives mail from the applications (the smarthost side).</summary>
public sealed class SmtpConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>The name the server gives in its greeting and in the Received header.</summary>
    public string Hostname { get; set; } = "matsmtp.local";

    /// <summary>Plain SMTP with STARTTLS offered (the classic port 25; in the container 2025). 0 switches it off.</summary>
    public int Port { get; set; } = 2025;

    /// <summary>Submission with STARTTLS (587; in the container 2587). 0 switches it off.</summary>
    public int SubmissionPort { get; set; } = 2587;

    /// <summary>TLS from the first byte (465; in the container 2465). 0 switches it off.</summary>
    public int TlsPort { get; set; } = 2465;

    /// <summary>Every session has to sign in with a relay user, also from a trusted network.</summary>
    public bool RequireAuth { get; set; }

    /// <summary>
    /// Clients from these networks may relay without signing in (CIDR, separated by commas). Empty: nobody. By default only this machine: behind Docker's
    /// userland proxy a client from the internet arrives with the address of the gateway (172.17.0.1), so a private range here can make an open relay.
    /// Widen it consciously (a printer in the LAN); applications sign in with their relay user anyway.
    /// </summary>
    public string TrustedNetworks { get; set; } = DefaultTrustedNetworks;

    /// <summary>Relaying without a sign-in: only the loopback.</summary>
    public const string DefaultTrustedNetworks = "127.0.0.0/8, ::1/128";

    /// <summary>The loopback and the private ranges (RFC 1918, unique local IPv6): this machine, the LAN and the Docker networks.</summary>
    public const string DefaultPrivateNetworks = "127.0.0.0/8, ::1/128, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, fc00::/7";

    /// <summary>
    /// Clients from these networks may sign in (AUTH) without TLS (CIDR, separated by commas), and only on the internal SMTP port <see cref="Port"/> (2025, which
    /// Mattiverse does not publish): the applications in the same Docker network, which cannot check a self-signed certificate. On the submission and the TLS
    /// port AUTH always needs TLS, whatever the address. Empty: nobody.
    /// </summary>
    public string PlainAuthNetworks { get; set; } = DefaultPrivateNetworks;

    /// <summary>AUTH without TLS on <see cref="Port"/> from every address (the password in clear text): only when that port is reachable from trusted networks alone.
    /// Never on the submission or the TLS port.</summary>
    public bool AllowPlainAuth { get; set; }

    /// <summary>
    /// A sender without a domain, or with one that cannot exist on the internet (<c>root</c>, <c>app@localhost</c>, <c>cron@nas</c>), gets this domain:
    /// <c>root@example.com</c>. Empty: senders stay as they are.
    /// </summary>
    public string DefaultSenderDomain { get; set; } = "";

    public int MaxMessageSizeMb { get; set; } = 25;

    /// <summary>The uploaded certificate (a PKCS#12 file in {data}/certs); empty: a self-signed one is made.</summary>
    public string CertificateFile { get; set; } = "";

    public string CertificatePasswordProtected { get; set; } = "";
}

/// <summary>How the spooler works off the queue.</summary>
public sealed class DeliveryConfig
{
    /// <summary>Nothing is sent while paused; mail is still received and waits in the queue.</summary>
    public bool Paused { get; set; }

    /// <summary>After this many failed attempts a message counts as failed.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>The waits between the attempts, in minutes (see <see cref="Core.Relay.RetrySchedule"/>).</summary>
    public string RetryMinutes { get; set; } = Core.Relay.RetrySchedule.Default;

    /// <summary>How many messages are sent at the same time.</summary>
    public int Parallel { get; set; } = 4;

    /// <summary>The log of messages is kept this long (days).</summary>
    public int KeepDays { get; set; } = 30;

    /// <summary>The content of a delivered message is kept this long (days); 0 deletes it right after the delivery.</summary>
    public int KeepBodyDays { get; set; } = 7;

    /// <summary>A Received header of MatSMTP is added to every message.</summary>
    public bool AddReceivedHeader { get; set; } = true;
}

public sealed class SecurityConfig
{
    public int SessionDays { get; set; } = 14;

    /// <summary>Administrators have to set up an authenticator app.</summary>
    public bool RequireTotpForAdmins { get; set; }

    /// <summary>Default life of a share link of a message, in days.</summary>
    public int ShareLinkDays { get; set; } = 14;

    /// <summary>
    /// The first start counts as done without an administrator from the setup page (<c>PUT /api/v1/settings</c>): an installation whose people come through
    /// MatIdentity. With an administrator it is done anyway.
    /// </summary>
    public bool SetupCompleted { get; set; }

    [JsonIgnore]
    public int MaxFailedLogins => 5;
}

public sealed class BackupConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>The scratch folder of a backup; empty: {data}/tmp.</summary>
    public string TempDirectory { get; set; } = "";
}
