using System.Security.Claims;
using MatSmtp.Configuration;
using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Auth;

public enum SignInOutcome
{
    /// <summary>The credentials are right and nothing else is needed.</summary>
    Success,
    /// <summary>The credentials are right; a code of the authenticator app is still needed.</summary>
    SecondFactor,
    Invalid,
    LockedOut,
    Inactive,
}

public sealed record SignInResult(SignInOutcome Outcome, User? User = null, DateTime? LockedUntil = null);

/// <summary>
/// Credentials, lock-out and sessions. A session is a row of <see cref="UserSession"/>; the cookie only names it.
/// <b>Nothing but the sign-in pages decides that the second step happened</b> (<see cref="StartSessionAsync"/> does not check it):
/// every new way to open a session has to go through <see cref="NeedsSecondFactor"/>.
/// </summary>
public sealed class SignInService(MsDbContext db, ConfigStore config, TimeProvider clock, SessionCache cache)
{
    public const string CookieScheme = "ms";

    /// <summary>The hash that is compared when a user does not exist, so that a wrong address costs as much time as a wrong password.</summary>
    private static readonly string DummyHash = PasswordHasher.Hash("not-a-password");

    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public Task<User?> FindByEmailAsync(string? email, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        return db.User.Include(u => u.Totp).FirstOrDefaultAsync(u => u.Email == normalized, ct);
    }

    public async Task<SignInResult> CheckPasswordAsync(string? email, string? password, CancellationToken ct = default)
    {
        var user = await FindByEmailAsync(email, ct);
        if (user is null)
        {
            PasswordHasher.Verify(password ?? string.Empty, DummyHash);
            return new SignInResult(SignInOutcome.Invalid);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (user.LockoutEndDate > now)
        {
            return new SignInResult(SignInOutcome.LockedOut, user, user.LockoutEndDate);
        }

        if (!PasswordHasher.Verify(password ?? string.Empty, user.PasswordHash))
        {
            await RegisterFailureAsync(user, ct);
            return new SignInResult(SignInOutcome.Invalid);
        }

        if (!user.IsActive)
        {
            return new SignInResult(SignInOutcome.Inactive, user);
        }

        if (PasswordHasher.NeedsRehash(user.PasswordHash!))
        {
            user.PasswordHash = PasswordHasher.Hash(password!);
            await db.SaveChangesAsync(ct);
        }

        return new SignInResult(NeedsSecondFactor(user) ? SignInOutcome.SecondFactor : SignInOutcome.Success, user);
    }

    public static bool NeedsSecondFactor(User user) => user.Totp is { IsConfirmed: true };

    /// <summary>Counts a wrong password, code or second factor; after five in a row the account is locked for 15 minutes.</summary>
    public async Task RegisterFailureAsync(User user, CancellationToken ct = default)
    {
        user.FailedLogins++;
        if (user.FailedLogins >= config.Current.Security.MaxFailedLogins)
        {
            user.LockoutEndDate = clock.GetUtcNow().UtcDateTime + LockoutTime;
            user.FailedLogins = 0;
        }

        await db.SaveChangesAsync(ct);
    }

    public bool IsLockedOut(User user) => user.LockoutEndDate > clock.GetUtcNow().UtcDateTime;

    /// <summary>Opens a session and signs the browser in. <paramref name="idToken"/>: the ID token of a sign-in through MatIdentity, kept for the sign-out there.</summary>
    public async Task<UserSession> StartSessionAsync(HttpContext http, User user, CancellationToken ct = default, string? idToken = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var session = new UserSession
        {
            UserId = user.Id,
            CreateUserId = user.Id,
            ExpiresDate = now.AddDays(Math.Max(1, config.Current.Security.SessionDays)),
            LastSeenDate = now,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 300),
            IdToken = string.IsNullOrEmpty(idToken) ? null : idToken,
        };
        db.UserSession.Add(session);
        user.LastLoginDate = now;
        user.FailedLogins = 0;
        user.LockoutEndDate = null;
        await db.SaveChangesAsync(ct);

        var snapshot = new SessionSnapshot(user.Id, user.DisplayName, user.Email, user.Role, user.Theme, NeedsSecondFactor(user));
        await http.SignInAsync(CookieScheme, BuildPrincipal(snapshot, session.Token), new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = session.ExpiresDate,
        });
        return session;
    }

    /// <summary>
    /// Ends the session of this browser: the row is revoked, so the cookie is worthless even if somebody kept a copy. Returns the ID token of a sign-in
    /// through MatIdentity (null for a local one), for the sign-out there.
    /// </summary>
    public async Task<string?> EndSessionAsync(HttpContext http, CancellationToken ct = default)
    {
        string? idToken = null;
        if (Guid.TryParse(http.User.FindFirstValue(AppClaims.Session), out var token))
        {
            var session = await db.UserSession.FirstOrDefaultAsync(s => s.Token == token, ct);
            if (session is not null)
            {
                idToken = session.IdToken;
                session.RevokedDate = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                cache.InvalidateUser(session.UserId);
            }
        }

        await http.SignOutAsync(CookieScheme);
        return idToken;
    }

    /// <summary>A user that may open a session (active, with its second factor loaded); null otherwise.</summary>
    public Task<User?> FindActiveAsync(long userId, CancellationToken ct = default)
        => db.User.Include(u => u.Totp).FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);

    /// <summary>Ends every session of a user (password changed, user switched off).</summary>
    public async Task EndAllSessionsAsync(long userId, Guid? except = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var sessions = await db.UserSession.Where(s => s.UserId == userId && s.RevokedDate == null && s.Token != except).ToListAsync(ct);
        foreach (var session in sessions)
        {
            session.RevokedDate = now;
        }

        await db.SaveChangesAsync(ct);
        cache.InvalidateUser(userId);
    }

    /// <summary>Writes the claims again (the display name or the theme changed) without a new session.</summary>
    public async Task RefreshAsync(HttpContext http, User user)
    {
        if (!Guid.TryParse(http.User.FindFirstValue(AppClaims.Session), out var token))
        {
            return;
        }

        cache.InvalidateUser(user.Id);
        var snapshot = new SessionSnapshot(user.Id, user.DisplayName, user.Email, user.Role, user.Theme, NeedsSecondFactor(user));
        var current = await http.AuthenticateAsync(CookieScheme);
        await http.SignInAsync(CookieScheme, BuildPrincipal(snapshot, token), current.Properties);
    }

    public static ClaimsPrincipal BuildPrincipal(SessionSnapshot s, Guid token)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, s.UserId.ToString()),
                new Claim(ClaimTypes.Name, s.DisplayName),
                new Claim(ClaimTypes.Email, s.Email),
                new Claim(ClaimTypes.Role, s.Role.ToString()),
                new Claim(AppClaims.Session, token.ToString()),
                new Claim(AppClaims.Theme, s.Theme),
            ],
            CookieScheme);
        return new ClaimsPrincipal(identity);
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
