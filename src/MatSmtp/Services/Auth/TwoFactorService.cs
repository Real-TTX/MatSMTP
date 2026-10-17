using MatSmtp.Configuration;
using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace MatSmtp.Services.Auth;

public sealed record TwoFactorEnrollment(string Secret, string Uri, string QrSvg);

/// <summary>
/// The second factor (TOTP): enrolment with a QR code, the check at sign-in, ten recovery codes (shown once, kept as hashes).
/// A code works once: the time step of the last accepted code is stored.
/// </summary>
public sealed class TwoFactorService(MsDbContext db, SecretProtector secrets, TimeProvider clock, SessionCache cache, SignInService signIn)
{
    public const string Issuer = "MatSMTP";

    private const int RecoveryCodeCount = 10;

    /// <summary>Starts (or restarts) the enrolment: a new secret that counts only after the first valid code.</summary>
    public async Task<TwoFactorEnrollment> BeginEnrollmentAsync(User user, CancellationToken ct = default)
    {
        var totp = await db.UserTotp.Include(t => t.RecoveryCodes).FirstOrDefaultAsync(t => t.UserId == user.Id, ct);
        if (totp is { IsConfirmed: true })
        {
            throw new InvalidOperationException("The second factor is already set up.");
        }

        var secret = Totp.NewSecret();
        if (totp is null)
        {
            totp = new UserTotp { UserId = user.Id };
            db.UserTotp.Add(totp);
        }

        totp.Secret = secrets.Protect(secret);
        totp.LastUsedStep = 0;
        await db.SaveChangesAsync(ct);
        return Describe(user, secret);
    }

    /// <summary>The secret and QR code of an enrolment that is under way (the page shows them again after a wrong code).</summary>
    public async Task<TwoFactorEnrollment?> PendingAsync(User user, CancellationToken ct = default)
    {
        var totp = await db.UserTotp.FirstOrDefaultAsync(t => t.UserId == user.Id && !t.IsConfirmed, ct);
        return totp is null ? null : Describe(user, secrets.Unprotect(totp.Secret));
    }

    /// <summary>Confirms the enrolment with the first code. Returns the recovery codes (plain, once) or null when the code was wrong.</summary>
    public async Task<IReadOnlyList<string>?> ConfirmEnrollmentAsync(User user, string? code, CancellationToken ct = default)
    {
        var totp = await db.UserTotp.Include(t => t.RecoveryCodes).FirstOrDefaultAsync(t => t.UserId == user.Id && !t.IsConfirmed, ct);
        if (totp is null)
        {
            return null;
        }

        var step = Totp.Verify(secrets.Unprotect(totp.Secret), code, clock.GetUtcNow(), totp.LastUsedStep);
        if (step is null)
        {
            return null;
        }

        totp.IsConfirmed = true;
        totp.LastUsedStep = step.Value;
        var codes = NewRecoveryCodes(totp);
        await db.SaveChangesAsync(ct);
        cache.InvalidateUser(user.Id);
        return codes;
    }

    /// <summary>Checks a code of the app or a recovery code. A wrong one counts against the lock-out like a wrong password.</summary>
    public async Task<bool> VerifyAsync(User user, string? input, CancellationToken ct = default)
    {
        var totp = await db.UserTotp.Include(t => t.RecoveryCodes).FirstOrDefaultAsync(t => t.UserId == user.Id && t.IsConfirmed, ct);
        if (totp is null)
        {
            return true;
        }

        if (signIn.IsLockedOut(user))
        {
            return false;
        }

        var step = Totp.Verify(secrets.Unprotect(totp.Secret), input, clock.GetUtcNow(), totp.LastUsedStep);
        if (step is not null)
        {
            totp.LastUsedStep = step.Value;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var hash = PasswordHasher.HashToken(NormalizeRecovery(input), RecoveryContext(user));
        var recovery = totp.RecoveryCodes.FirstOrDefault(r => r.UsedDate is null && r.CodeHash == hash);
        if (recovery is not null)
        {
            recovery.UsedDate = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return true;
        }

        await signIn.RegisterFailureAsync(user, ct);
        return false;
    }

    public async Task DisableAsync(User user, CancellationToken ct = default)
    {
        var totp = await db.UserTotp.FirstOrDefaultAsync(t => t.UserId == user.Id, ct);
        if (totp is not null)
        {
            db.UserTotp.Remove(totp);
            await db.SaveChangesAsync(ct);
        }

        cache.InvalidateUser(user.Id);
    }

    public async Task<IReadOnlyList<string>> NewRecoveryCodesAsync(User user, CancellationToken ct = default)
    {
        var totp = await db.UserTotp.Include(t => t.RecoveryCodes).FirstAsync(t => t.UserId == user.Id && t.IsConfirmed, ct);
        var codes = NewRecoveryCodes(totp);
        await db.SaveChangesAsync(ct);
        return codes;
    }

    public async Task<int> RemainingRecoveryCodesAsync(User user, CancellationToken ct = default)
        => await db.UserRecoveryCode.CountAsync(r => r.UserTotp!.UserId == user.Id && r.UsedDate == null, ct);

    private List<string> NewRecoveryCodes(UserTotp totp)
    {
        db.UserRecoveryCode.RemoveRange(totp.RecoveryCodes);
        totp.RecoveryCodes.Clear();
        var codes = new List<string>();
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var code = PasswordHasher.RandomCode(10);
            codes.Add($"{code[..5]}-{code[5..]}");
            totp.RecoveryCodes.Add(new UserRecoveryCode { CodeHash = PasswordHasher.HashToken(code, RecoveryContext(totp.UserId)) });
        }

        return codes;
    }

    private static string NormalizeRecovery(string? input) => (input ?? string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);

    private static string RecoveryContext(User user) => RecoveryContext(user.Id);

    private static string RecoveryContext(long userId) => "recovery:" + userId;

    private static TwoFactorEnrollment Describe(User user, string secret)
    {
        var uri = Totp.Uri(Issuer, user.Email, secret);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(new System.Drawing.Size(200, 200));
        return new TwoFactorEnrollment(secret, uri, svg);
    }
}
