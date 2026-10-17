using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Auth;

/// <summary>Creating users and changing their passwords: the one place that knows the rules (unique address, password length).</summary>
public sealed class UserService(MsDbContext db, SignInService signIn, SessionCache cache, TimeProvider clock)
{
    public const int MinPasswordLength = 10;

    /// <summary>Null when the password is acceptable, otherwise what is wrong with it.</summary>
    public static string? CheckPassword(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return $"Das Passwort braucht mindestens {MinPasswordLength} Zeichen.";
        }

        if (email is not null && password.Equals(email, StringComparison.OrdinalIgnoreCase))
        {
            return "Das Passwort darf nicht die E-Mail-Adresse sein.";
        }

        return null;
    }

    public async Task<bool> EmailTakenAsync(string email, long? exceptUserId = null, CancellationToken ct = default)
    {
        var normalized = SignInService.NormalizeEmail(email);
        return await db.User.AnyAsync(u => u.Email == normalized && u.Id != exceptUserId, ct);
    }

    /// <summary>Creates a user.</summary>
    public async Task<User> CreateAsync(string email, string displayName, UserRole role, string password, CancellationToken ct = default)
    {
        var normalized = SignInService.NormalizeEmail(email);
        if (await EmailTakenAsync(normalized, null, ct))
        {
            throw new InvalidOperationException("Diese E-Mail-Adresse gehört schon zu einem Konto.");
        }

        var user = new User
        {
            Email = normalized,
            DisplayName = displayName.Trim(),
            Role = role,
            PasswordHash = PasswordHasher.Hash(password),
            PasswordChangedDate = clock.GetUtcNow().UtcDateTime,
        };
        db.User.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Sets a new password and ends every other session of the user: whoever knew the old password is out.</summary>
    public async Task ChangePasswordAsync(User user, string newPassword, Guid? keepSession = null, CancellationToken ct = default)
    {
        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.PasswordChangedDate = clock.GetUtcNow().UtcDateTime;
        user.FailedLogins = 0;
        user.LockoutEndDate = null;
        await db.SaveChangesAsync(ct);
        await signIn.EndAllSessionsAsync(user.Id, keepSession, ct);
    }

    public async Task SetActiveAsync(User user, bool active, CancellationToken ct = default)
    {
        user.IsActive = active;
        await db.SaveChangesAsync(ct);
        cache.InvalidateUser(user.Id);
        if (!active)
        {
            await signIn.EndAllSessionsAsync(user.Id, null, ct);
        }
    }
}
