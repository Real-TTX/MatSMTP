using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MatSmtp.Services.Auth;

/// <summary>
/// The note between "the password was right" and "the code was right": a data-protected cookie that is valid for five minutes.
/// It is not a session; it only tells the next page which user has passed the first step.
/// </summary>
public sealed class TwoFactorTicket(IDataProtectionProvider provider, TimeProvider clock)
{
    public const string CookieName = ".MatSmtp.TwoFactor";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly IDataProtector _protector = provider.CreateProtector("MatSmtp.TwoFactorTicket.v1");

    private sealed record Payload(long UserId, long ExpiresUnix, string? ReturnUrl);

    public void Issue(HttpContext http, long userId, string? returnUrl)
    {
        var expires = clock.GetUtcNow() + Lifetime;
        var json = JsonSerializer.Serialize(new Payload(userId, expires.ToUnixTimeSeconds(), returnUrl));
        http.Response.Cookies.Append(CookieName, _protector.Protect(json), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/Account",
            MaxAge = Lifetime,
        });
    }

    public (long UserId, string? ReturnUrl)? Read(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var value))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(value));
            if (payload is null || payload.ExpiresUnix < clock.GetUtcNow().ToUnixTimeSeconds())
            {
                return null;
            }

            return (payload.UserId, payload.ReturnUrl);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Clear(HttpContext http) => http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/Account" });
}
