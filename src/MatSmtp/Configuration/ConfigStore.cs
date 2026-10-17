using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace MatSmtp.Configuration;

/// <summary>
/// Reads and writes <c>{data}/config/app.json</c>. Environment variables <c>MATSMTP__Section__Key</c> win for
/// the section Server and the ports of the receiver (how this machine is deployed); they are never written back to the file.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _gate = new();
    private readonly string _path;
    private AppConfig _config;

    public ConfigStore(string dataDir)
    {
        DataDir = dataDir;
        _path = Path.Combine(dataDir, "config", "app.json");
        _config = Load();
    }

    /// <summary>The data volume.</summary>
    public string DataDir { get; }

    public AppConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    /// <summary>Raised after the configuration was changed through <see cref="Update"/>.</summary>
    public event Action? Changed;

    /// <summary>Changes the configuration and writes the file: a temporary file first, then it replaces the old one.</summary>
    public void Update(Action<AppConfig> change)
    {
        lock (_gate)
        {
            change(_config);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Persistable(_config), Json), new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }

        Changed?.Invoke();
    }

    /// <summary>The SQLite file of the application: <c>{data}/db/matsmtp.db</c>.</summary>
    public string DatabasePath => Path.Combine(DataDir, "db", "matsmtp.db");

    /// <summary>The folder of the messages in the queue (one .eml file each).</summary>
    public string SpoolDir => Path.Combine(DataDir, "spool");

    /// <summary>The folder of the TLS certificates of the receiver.</summary>
    public string CertDir => Path.Combine(DataDir, "certs");

    /// <summary>The connection string of the application database. A writer waits up to 30 seconds for another one (the receiver, the spooler and the pages write at the same time).</summary>
    public string ConnectionString(string? path = null) => new SqliteConnectionStringBuilder
    {
        DataSource = path ?? DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = true,
        DefaultTimeout = 30,
    }.ConnectionString;

    private AppConfig Load()
    {
        var config = new AppConfig();
        if (File.Exists(_path))
        {
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), Json) ?? new AppConfig();
            }
            catch (JsonException)
            {
                // A damaged file must not keep the application from starting: keep it for inspection and start with the defaults.
                File.Copy(_path, _path + ".damaged", overwrite: true);
            }
        }

        ApplyEnvironment(config);
        return config;
    }

    /// <summary>The file without what comes from the environment: those values belong to the deployment, not to the file.</summary>
    private AppConfig Persistable(AppConfig live)
    {
        var copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(live, Json), Json)!;
        var fromFile = new AppConfig();
        if (File.Exists(_path))
        {
            try
            {
                fromFile = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), Json) ?? fromFile;
            }
            catch (JsonException)
            {
                // The new file replaces the damaged one.
            }
        }

        copy.Server = fromFile.Server;
        if (Env("Smtp__Port") is not null) { copy.Smtp.Port = fromFile.Smtp.Port; }
        if (Env("Smtp__SubmissionPort") is not null) { copy.Smtp.SubmissionPort = fromFile.Smtp.SubmissionPort; }
        if (Env("Smtp__TlsPort") is not null) { copy.Smtp.TlsPort = fromFile.Smtp.TlsPort; }
        return copy;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable("MATSMTP__" + name);

    /// <summary>Whether a port of the receiver comes from the environment (the page shows it as fixed then).</summary>
    public static bool PortFromEnvironment(string name) => Env("Smtp__" + name) is not null;

    private static void ApplyEnvironment(AppConfig config)
    {
        if (int.TryParse(Env("Smtp__Port"), out var smtp)) { config.Smtp.Port = smtp; }
        if (int.TryParse(Env("Smtp__SubmissionPort"), out var submission)) { config.Smtp.SubmissionPort = submission; }
        if (int.TryParse(Env("Smtp__TlsPort"), out var tls)) { config.Smtp.TlsPort = tls; }
        if (Env("Server__PublicUrl") is { Length: > 0 } url) { config.Server.PublicUrl = url.TrimEnd('/'); }
        if (Env("Server__TimeZone") is { Length: > 0 } zone) { config.Server.TimeZone = zone; }
        if (bool.TryParse(Env("Server__ForceSecureCookie"), out var secure)) { config.Server.ForceSecureCookie = secure; }
    }
}

/// <summary>Protects secrets that are kept in the configuration or in the database (the passwords of the SMTP accounts, of the NAS, the secrets of the webhooks).</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("MatSmtp.Secrets.v1");

    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);

    /// <summary>Returns an empty string for an empty or damaged value (for example after the key ring was lost).</summary>
    public string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return "";
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "";
        }
    }
}
