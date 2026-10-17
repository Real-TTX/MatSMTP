using MatSmtp.Configuration;

namespace MatSmtp.Services;

/// <summary>Absolute addresses for links in mails and share links: the public address of the configuration, else the address of the request.</summary>
public sealed class PublicUrl(ConfigStore config, IHttpContextAccessor accessor)
{
    public string Absolute(string path)
    {
        var configured = config.Current.Server.PublicUrl;
        var baseUrl = !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimEnd('/')
            : accessor.HttpContext is { } http ? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}" : "http://localhost:2525";
        return baseUrl + (path.StartsWith('/') ? path : "/" + path);
    }
}
