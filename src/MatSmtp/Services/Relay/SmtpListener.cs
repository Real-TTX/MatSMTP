using System.Buffers;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using Microsoft.Extensions.Caching.Memory;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace MatSmtp.Services.Relay;

/// <summary>Whether the receiver runs, on which ports, and why not.</summary>
public sealed record ListenerState(bool Running, IReadOnlyList<string> Endpoints, string? Error, DateTime? Since);

/// <summary>
/// The SMTP side of the relay: listens on the ports of the settings (plain with STARTTLS, submission, TLS from the start), lets applications in by their network
/// or by a relay user (AUTH PLAIN / LOGIN: over TLS, or without it from the networks of <see cref="SmtpConfig.PlainAuthNetworks"/>), and hands every message to
/// <see cref="RelayService"/>. A change of the settings restarts it; the certificate is asked
/// for per connection, so a new one needs no restart.
/// </summary>
public sealed class SmtpListener(ConfigStore config, IServiceScopeFactory scopes, CertificateService certificates, IMemoryCache cache, ILogger<SmtpListener> logger) : BackgroundService
{
    private const string HeloKey = "matsmtp.helo";

    private CancellationTokenSource? _cycle;
    private string _signature = "";

    public ListenerState State { get; private set; } = new(false, [], null, null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        config.Changed += OnConfigChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _cycle = cycle;
                var smtp = config.Current.Smtp;
                _signature = Signature(smtp);
                var endpoints = Endpoints(smtp);
                if (!smtp.Enabled || endpoints.Count == 0)
                {
                    State = new ListenerState(false, [], smtp.Enabled ? "Kein Port eingestellt." : "Der Empfang ist abgeschaltet.", null);
                    await WaitAsync(Timeout.InfiniteTimeSpan, cycle.Token);
                    continue;
                }

                try
                {
                    var server = Build(smtp, endpoints);
                    State = new ListenerState(true, [.. endpoints.Select(e => e.Label)], null, DateTime.UtcNow);
                    logger.LogInformation("SMTP receiver listening on {Ports}.", string.Join(", ", endpoints.Select(e => e.Label)));
                    await server.StartAsync(cycle.Token);
                }
                catch (OperationCanceledException) when (cycle.IsCancellationRequested)
                {
                    // restart after a change of the settings, or the application stops
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The SMTP receiver could not start.");
                    State = new ListenerState(false, [.. endpoints.Select(e => e.Label)], ex.Message, null);
                    await WaitAsync(TimeSpan.FromSeconds(30), cycle.Token);
                }
            }
        }
        finally
        {
            config.Changed -= OnConfigChanged;
        }
    }

    /// <summary>Restarts the receiver now (after the certificate changed, or by hand).</summary>
    public void Restart() => _cycle?.Cancel();

    private void OnConfigChanged()
    {
        if (Signature(config.Current.Smtp) != _signature)
        {
            _cycle?.Cancel();
        }
    }

    private static string Signature(SmtpConfig s) => $"{s.Enabled}|{s.Hostname}|{s.Port}|{s.SubmissionPort}|{s.TlsPort}|{s.RequireAuth}|{s.AllowPlainAuth}|{s.MaxMessageSizeMb}";

    private static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record EndpointSpec(int Port, bool Tls, string Label);

    private static List<EndpointSpec> Endpoints(SmtpConfig smtp)
    {
        var list = new List<EndpointSpec>();
        if (smtp.Port > 0)
        {
            list.Add(new EndpointSpec(smtp.Port, false, $"{smtp.Port} (SMTP, STARTTLS)"));
        }

        if (smtp.SubmissionPort > 0 && smtp.SubmissionPort != smtp.Port)
        {
            list.Add(new EndpointSpec(smtp.SubmissionPort, false, $"{smtp.SubmissionPort} (Submission, STARTTLS)"));
        }

        if (smtp.TlsPort > 0 && smtp.TlsPort != smtp.Port && smtp.TlsPort != smtp.SubmissionPort)
        {
            list.Add(new EndpointSpec(smtp.TlsPort, true, $"{smtp.TlsPort} (TLS)"));
        }

        return list;
    }

    private SmtpServer.SmtpServer Build(SmtpConfig smtp, List<EndpointSpec> endpoints)
    {
        var certificate = new CertificateFactory(certificates);
        var builder = new SmtpServerOptionsBuilder()
            .ServerName(string.IsNullOrWhiteSpace(smtp.Hostname) ? "matsmtp.local" : smtp.Hostname.Trim())
            .MaxMessageSize(Math.Clamp(smtp.MaxMessageSizeMb, 1, 500) * 1024 * 1024, MaxMessageSizeHandling.Strict)
            .MaxAuthenticationAttempts(3);
        foreach (var e in endpoints)
        {
            builder.Endpoint(b => b
                .Endpoint(new IPEndPoint(IPAddress.Any, e.Port))
                .IsSecure(e.Tls)
                .Certificate(certificate)
                // Per session, not per port: AUTH without TLS only from the networks of PlainAuthNetworks (see AuthWithoutTls).
                .AllowUnsecureAuthentication(true)
                .AuthenticationRequired(false)
                .SessionTimeout(TimeSpan.FromMinutes(5)));
        }

        var services = new SmtpServer.ComponentModel.ServiceProvider();
        services.Add(new MessageStore(scopes));
        services.Add(new MailboxFilter(config));
        services.Add(new UserAuthenticator(scopes, cache, logger));
        services.Add(new CommandFactory(config));
        var server = new SmtpServer.SmtpServer(builder.Build(), services);
        server.SessionCreated += (_, e) => e.Context.CommandExecuting += (_, c) =>
        {
            if (c.Command is AuthCommand && !AuthWithoutTls(config.Current.Smtp, c.Context))
            {
                // The client did not ask EHLO what is offered: refused before the password is checked (it may have crossed the network in clear already).
                throw new SmtpResponseException(new SmtpResponse((SmtpReplyCode)538, "5.7.11 Erst STARTTLS, dann anmelden: ohne TLS geht AUTH nur aus den freigegebenen Netzen."));
            }

            if (c.Command is EhloCommand ehlo)
            {
                e.Context.Properties[HeloKey] = ehlo.DomainOrAddress;
            }
            else if (c.Command is HeloCommand helo)
            {
                e.Context.Properties[HeloKey] = helo.DomainOrAddress;
            }
        };
        return server;
    }

    /// <summary>
    /// Whether this session may sign in (AUTH): always over TLS (STARTTLS or the TLS port); without TLS only from the networks of
    /// <see cref="SmtpConfig.PlainAuthNetworks"/> (the Docker network, the LAN) or from everywhere with <see cref="SmtpConfig.AllowPlainAuth"/>.
    /// </summary>
    public static bool PlainAuthAllowed(SmtpConfig smtp, IPAddress? client)
        => smtp.AllowPlainAuth || IpNetworks.Contains(smtp.PlainAuthNetworks, client);

    private static bool AuthWithoutTls(SmtpConfig smtp, ISessionContext context)
        => (context.Pipe?.IsSecure ?? false) || PlainAuthAllowed(smtp, Session(context).ClientIp);

    /// <summary>EHLO offers AUTH only to a session that may use it (<see cref="AuthWithoutTls"/>): a client never sends a password it should not.</summary>
    private sealed class CommandFactory(ConfigStore config) : SmtpCommandFactory
    {
        public override SmtpCommand CreateEhlo(string domainOrAddress) => new Ehlo(domainOrAddress, config);

        private sealed class Ehlo(string domainOrAddress, ConfigStore config) : EhloCommand(domainOrAddress)
        {
            protected override IEnumerable<string> GetExtensions(ISessionContext context)
            {
                var auth = AuthWithoutTls(config.Current.Smtp, context);
                return base.GetExtensions(context).Where(e => auth || !e.StartsWith("AUTH", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>What the session knows about the client, for the rules and the log.</summary>
    internal static SessionInfo Session(ISessionContext context)
    {
        var remote = context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var r) ? r as IPEndPoint : null;
        var helo = context.Properties.TryGetValue(HeloKey, out var h) ? h as string : null;
        var user = context.Authentication.IsAuthenticated ? context.Authentication.User : null;
        return new SessionInfo(remote is null ? null : IpNetwork.Normalize(remote.Address), helo, user, context.EndpointDefinition.Endpoint.Port, context.Pipe?.IsSecure ?? false);
    }

    private sealed class CertificateFactory(CertificateService certificates) : ICertificateFactory
    {
        public X509Certificate GetServerCertificate(ISessionContext sessionContext) => certificates.Current;
    }

    /// <summary>Who may hand over mail: a session that signed in, or a client of a trusted network (unless every session has to sign in).</summary>
    private sealed class MailboxFilter(ConfigStore config) : IMailboxFilter
    {
        public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
        {
            if (context.Authentication.IsAuthenticated)
            {
                return Task.FromResult(true);
            }

            var smtp = config.Current.Smtp;
            if (!smtp.RequireAuth && IpNetworks.Contains(smtp.TrustedNetworks, Session(context).ClientIp))
            {
                return Task.FromResult(true);
            }

            throw new SmtpResponseException(new SmtpResponse(SmtpReplyCode.AuthenticationRequired, "5.7.0 Bitte zuerst anmelden (AUTH): dieses Netz darf nicht ohne Anmeldung senden."));
        }

        public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>Checks a relay user. After ten wrong passwords from one address in 15 minutes that address cannot sign in for a while.</summary>
    private sealed class UserAuthenticator(IServiceScopeFactory scopes, IMemoryCache cache, ILogger logger) : IUserAuthenticator
    {
        public async Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken)
        {
            var ip = Session(context).ClientIp?.ToString() ?? "?";
            var key = "smtp-auth-fail:" + ip;
            if (cache.TryGetValue(key, out int failures) && failures >= 10)
            {
                logger.LogWarning("SMTP AUTH from {Ip} refused: too many wrong passwords.", ip);
                return false;
            }

            using var scope = scopes.CreateScope();
            var name = (user ?? "").Trim();
            if (!await scope.ServiceProvider.GetRequiredService<RelayUserService>().AuthenticateAsync(name, password, cancellationToken))
            {
                cache.Set(key, failures + 1, TimeSpan.FromMinutes(15));
                logger.LogWarning("SMTP AUTH failed for {User} from {Ip}.", name, ip);
                return false;
            }

            return true;
        }
    }

    /// <summary>Hands the message to the relay service: its answer is the answer of the DATA command.</summary>
    private sealed class MessageStore(IServiceScopeFactory scopes) : SmtpServer.Storage.MessageStore
    {
        public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            var relay = scope.ServiceProvider.GetRequiredService<RelayService>();
            var recipients = transaction.To.Select(t => t.AsAddress()).ToList();
            var from = transaction.From is null || string.IsNullOrEmpty(transaction.From.User) ? "" : transaction.From.AsAddress();
            var result = await relay.ReceiveAsync(Session(context), from, recipients, buffer.ToArray(), cancellationToken);
            return new SmtpResponse((SmtpReplyCode)result.Code, result.Text);
        }
    }
}
