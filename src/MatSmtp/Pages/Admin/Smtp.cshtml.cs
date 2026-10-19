using System.ComponentModel.DataAnnotations;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Admin;

/// <summary>How the relay receives: ports, who may send without signing in, AUTH without TLS, the size limit, and the TLS certificate.</summary>
public sealed class SmtpModel(ConfigStore config, SmtpListener listener, CertificateService certificates, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        public bool Enabled { get; set; } = true;

        [Required(ErrorMessage = "Bitte gib den Servernamen ein.")]
        [RegularExpression(@"^[A-Za-z0-9.\-]{1,253}$", ErrorMessage = "Ein Hostname: Buchstaben, Ziffern, Punkt und Bindestrich.")]
        public string Hostname { get; set; } = "";

        [Range(0, 65535, ErrorMessage = "0 (aus) bis 65535.")]
        public int Port { get; set; }

        [Range(0, 65535, ErrorMessage = "0 (aus) bis 65535.")]
        public int SubmissionPort { get; set; }

        [Range(0, 65535, ErrorMessage = "0 (aus) bis 65535.")]
        public int TlsPort { get; set; }

        public bool RequireAuth { get; set; }

        public string? TrustedNetworks { get; set; }

        public bool AllowPlainAuth { get; set; }

        public string? PlainAuthNetworks { get; set; }

        public string? DefaultSenderDomain { get; set; }

        [Range(1, 500, ErrorMessage = "1 bis 500 MB.")]
        public int MaxMessageSizeMb { get; set; } = 25;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ListenerState Listener => listener.State;

    public CertificateInfo? Certificate { get; private set; }

    public string? CertificateError { get; private set; }

    public bool PortFixed(string name) => ConfigStore.PortFromEnvironment(name);

    [BindProperty]
    public string CertFormat { get; set; } = "pfx";

    [BindProperty]
    public string? CertPassword { get; set; }

    public void OnGet()
    {
        Load();
        var s = config.Current.Smtp;
        Input = new InputModel
        {
            Enabled = s.Enabled, Hostname = s.Hostname, Port = s.Port, SubmissionPort = s.SubmissionPort, TlsPort = s.TlsPort, RequireAuth = s.RequireAuth,
            TrustedNetworks = s.TrustedNetworks.Replace(", ", "\n"), AllowPlainAuth = s.AllowPlainAuth, MaxMessageSizeMb = s.MaxMessageSizeMb,
            PlainAuthNetworks = s.PlainAuthNetworks.Replace(", ", "\n"), DefaultSenderDomain = s.DefaultSenderDomain,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Load();
        if (IpNetworks.Problem(Input.TrustedNetworks) is { } problem)
        {
            ModelState.AddModelError("Input.TrustedNetworks", problem);
        }

        if (IpNetworks.Problem(Input.PlainAuthNetworks) is { } plainProblem)
        {
            ModelState.AddModelError("Input.PlainAuthNetworks", plainProblem);
        }

        var domain = (Input.DefaultSenderDomain ?? "").Trim().TrimStart('@').ToLowerInvariant();
        if (!RelaySettingsService.IsValidDomain(domain))
        {
            ModelState.AddModelError("Input.DefaultSenderDomain", "Eine Domain wie example.com, oder leer.");
        }

        var ports = new[] { Input.Port, Input.SubmissionPort, Input.TlsPort }.Where(p => p > 0).ToList();
        if (ports.Count != ports.Distinct().Count())
        {
            ModelState.AddModelError("Input.Port", "Jeder Port darf nur einmal vorkommen.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var hostChanged = !string.Equals(config.Current.Smtp.Hostname, Input.Hostname.Trim(), StringComparison.OrdinalIgnoreCase);
        config.Update(c =>
        {
            var s = c.Smtp;
            s.Enabled = Input.Enabled;
            s.Hostname = Input.Hostname.Trim();
            if (!PortFixed("Port")) { s.Port = Input.Port; }
            if (!PortFixed("SubmissionPort")) { s.SubmissionPort = Input.SubmissionPort; }
            if (!PortFixed("TlsPort")) { s.TlsPort = Input.TlsPort; }
            s.RequireAuth = Input.RequireAuth;
            if (!Input.RequireAuth)
            {
                s.TrustedNetworks = string.Join(", ", IpNetworks.Parse(Input.TrustedNetworks).Select(n => n.ToString()));
            }

            s.AllowPlainAuth = Input.AllowPlainAuth;
            if (!Input.AllowPlainAuth)
            {
                s.PlainAuthNetworks = string.Join(", ", IpNetworks.Parse(Input.PlainAuthNetworks).Select(n => n.ToString()));
            }

            s.DefaultSenderDomain = domain;
            s.MaxMessageSizeMb = Input.MaxMessageSizeMb;
        });

        if (hostChanged && !certificates.IsUploaded)
        {
            // The self-signed certificate carries the host name: a new name, a new certificate.
            certificates.RenewSelfSigned();
        }

        await log.LogAsync("Empfang geändert", "Settings", null, $"{(Input.Enabled ? "an" : "aus")}, Ports {Input.Port}/{Input.SubmissionPort}/{Input.TlsPort}, {(Input.RequireAuth ? "Anmeldung immer" : "vertrauenswürdig: " + config.Current.Smtp.TrustedNetworks)}");
        Flash("Der Empfang ist gespeichert. Er startet mit den neuen Einstellungen neu.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCertificateAsync(IFormFile? certFile, IFormFile? keyFile)
    {
        if (certFile is null || certFile.Length == 0)
        {
            FlashError("Bitte wähle die Zertifikatsdatei aus.");
            return RedirectToPage();
        }

        if (certFile.Length > 1024 * 1024 || keyFile?.Length > 1024 * 1024)
        {
            FlashError("Die Datei ist zu groß für ein Zertifikat.");
            return RedirectToPage();
        }

        var bytes = await Read(certFile);
        byte[]? key = null;
        if (CertFormat == "pem")
        {
            if (keyFile is null || keyFile.Length == 0)
            {
                FlashError("Zu einem PEM-Zertifikat gehört die Datei mit dem privaten Schlüssel.");
                return RedirectToPage();
            }

            key = await Read(keyFile);
        }

        if (certificates.Upload(bytes, CertPassword, key) is { } error)
        {
            FlashError(error);
            return RedirectToPage();
        }

        listener.Restart();
        await log.LogAsync("Zertifikat hochgeladen", "Settings", null, certificates.Info().Subject);
        Flash("Das Zertifikat ist hochgeladen und wird ab sofort verwendet.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveCertificateAsync()
    {
        certificates.RemoveUploaded();
        listener.Restart();
        await log.LogAsync("Zertifikat entfernt", "Settings");
        Flash("Das hochgeladene Zertifikat ist entfernt. Der Empfang verwendet wieder das selbst signierte.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRenewAsync()
    {
        certificates.RenewSelfSigned();
        listener.Restart();
        await log.LogAsync("Selbst signiertes Zertifikat erneuert", "Settings");
        Flash("Es gibt ein neues selbst signiertes Zertifikat.");
        return RedirectToPage();
    }

    public IActionResult OnPostRestart()
    {
        listener.Restart();
        Flash("Der Empfang startet neu.");
        return RedirectToPage();
    }

    private static async Task<byte[]> Read(IFormFile file)
    {
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        return stream.ToArray();
    }

    private void Load()
    {
        Head("Empfang", "Wie Anwendungen Mails an MatSMTP übergeben: Ports, Anmeldung, vertrauenswürdige Netze und das TLS-Zertifikat.", ("Einstellungen", null), ("Empfang", null));
        try
        {
            Certificate = certificates.Info();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            CertificateError = ex.Message;
        }
    }
}
