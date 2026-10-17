using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using MimeKit;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace MatSmtp.Tests.Integration;

/// <summary>What the upstream server took: the envelope and the message.</summary>
public sealed record UpstreamMail(string From, IReadOnlyList<string> To, MimeMessage Message, string? User);

/// <summary>
/// The SMTP server of a provider, for the tests: takes mail on a free port of 127.0.0.1, optionally wants a sign-in, and answers with a fixed error when asked to
/// (to see the retries and the permanent failures of the spooler).
/// </summary>
public sealed class FakeUpstream : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _running;

    public FakeUpstream(string? user = null, string? password = null)
    {
        Port = AppFixture.FreePort();
        var options = new SmtpServerOptionsBuilder()
            .ServerName("upstream.test")
            .Endpoint(b => b.Endpoint(new IPEndPoint(IPAddress.Loopback, Port)).AllowUnsecureAuthentication(true).AuthenticationRequired(user is not null))
            .Build();
        var services = new SmtpServer.ComponentModel.ServiceProvider();
        services.Add(new Store(this));
        if (user is not null)
        {
            services.Add(new SmtpServer.Authentication.DelegatingUserAuthenticator((u, p) => u == user && p == password));
        }

        _running = new SmtpServer.SmtpServer(options, services).StartAsync(_stop.Token);
    }

    public int Port { get; }

    public ConcurrentQueue<UpstreamMail> Received { get; } = new();

    /// <summary>When set, every message is answered with this code and text (for example 451 or 550).</summary>
    public (int Code, string Text)? Fail { get; set; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class Store(FakeUpstream owner) : MessageStore
    {
        public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            if (owner.Fail is { } fail)
            {
                return new SmtpResponse((SmtpReplyCode)fail.Code, fail.Text);
            }

            using var stream = new MemoryStream(buffer.ToArray());
            var message = await MimeMessage.LoadAsync(stream, cancellationToken);
            owner.Received.Enqueue(new UpstreamMail(transaction.From.AsAddress(), [.. transaction.To.Select(t => t.AsAddress())], message, context.Authentication.User));
            return SmtpResponse.Ok;
        }
    }
}
