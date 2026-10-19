using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Auth;

/// <summary>A person signed in by MatIdentity, as <see cref="UserService.SignInExternalAsync"/> gets it.</summary>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

/// <summary>
/// The outcome of <see cref="UserService.SignInExternalAsync"/>: the user to sign in, the id of the account that matches by its address and has to be
/// proven by its password before it is linked (<see cref="UserService.LinkExternalAsync"/>), or the reason it is refused.
/// </summary>
public sealed record ExternalSignIn(User? User, long? LinkUserId, string? Error)
{
    public bool Refused => Error is not null;

    public static ExternalSignIn SignedIn(User user) => new(user, null, null);

    public static ExternalSignIn LinkRequired(long userId) => new(null, userId, null);

    public static ExternalSignIn Refuse(string error) => new(null, null, error);
}

/// <summary>Creating users and changing their passwords: the one place that knows the rules (unique address, password length), and the users of MatIdentity.</summary>
public sealed class UserService(MsDbContext db, SignInService signIn, SessionCache cache, TimeProvider clock, ILogger<UserService> logger)
{
    public const int MinPasswordLength = 10;

    /// <summary>The shortest password <c>MATSMTP_ADMIN_PASSWORD</c> may have (Mattiverse generates long ones).</summary>
    public const int MinSeedPasswordLength = 8;

    /// <summary>The sign-in name of the administrator that <c>MATSMTP_ADMIN_PASSWORD</c> creates (Mattiverse shows "admin"), unless <c>MATSMTP_ADMIN_EMAIL</c> names another.</summary>
    public const string DefaultAdminEmail = "admin";

    private const string DisabledMessage = "Dieses Konto ist in MatSMTP deaktiviert.";

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

    /// <summary>Whether there is an administrator (the setup is done then).</summary>
    public Task<bool> AnyAdminAsync(CancellationToken ct = default) => db.User.AnyAsync(u => u.Role == UserRole.Admin, ct);

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

    /// <summary>
    /// The administrator of a headless installation (<c>MATSMTP_ADMIN_PASSWORD</c>, like Matcad's <c>MATCAD_ADMIN_PASSWORD</c>): created when there is no
    /// administrator yet, with this password and no change asked for, so the setup page is skipped. A password shorter than
    /// <see cref="MinSeedPasswordLength"/> or equal to <c>admin</c> is ignored with a warning; nothing happens once an administrator exists.
    /// </summary>
    public async Task<User?> SeedAdminAsync(string? password, string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        if (password.Length < MinSeedPasswordLength || password.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("MATSMTP_ADMIN_PASSWORD is ignored: it needs at least {Length} characters and must not be 'admin'.", MinSeedPasswordLength);
            return null;
        }

        if (await AnyAdminAsync(ct))
        {
            return null;
        }

        var address = string.IsNullOrWhiteSpace(email) ? DefaultAdminEmail : email.Trim();
        if (await EmailTakenAsync(address, null, ct))
        {
            logger.LogWarning("MATSMTP_ADMIN_PASSWORD is ignored: a user {Email} exists already (it is no administrator).", address);
            return null;
        }

        var admin = await CreateAsync(address, "Administrator", UserRole.Admin, password, ct);
        logger.LogInformation("The administrator {Email} was created from MATSMTP_ADMIN_PASSWORD.", admin.Email);
        return admin;
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

    /// <summary>Keeps the theme a user chose (system, light, dark); returns the user for the new claims.</summary>
    public async Task<User> SetThemeAsync(long userId, string theme, CancellationToken ct = default)
    {
        var user = await db.User.Include(u => u.Totp).FirstAsync(u => u.Id == userId, ct);
        user.Theme = theme;
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>The list of the users page: search, filter, sort and page of the request, with the second factor of each.</summary>
    public Task<ListResult<User>> ListAsync(HttpRequest request, CancellationToken ct)
    {
        var spec = new ListSpec<User>
        {
            Id = "users",
            Action = "/Admin/Users",
            Label = "Benutzer",
            Noun = "Benutzer",
            SearchPlaceholder = "Name oder E-Mail-Adresse ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(u => EF.Functions.Like(u.DisplayName, like, Like.Escape) || EF.Functions.Like(u.Email, like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<User>("rolle", "Rolle", ListBuilder.EnumOptions<UserRole>(Labels.Of), (q, v) => q.Where(u => u.Role == Enum.Parse<UserRole>(v))),
                new FilterSpec<User>("status", "Status", [new("aktiv", "Aktiv"), new("gesperrt", "Gesperrt")], (q, v) => v == "gesperrt" ? q.Where(u => !u.IsActive) : q.Where(u => u.IsActive)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<User>, bool, IOrderedQueryable<User>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(u => u.DisplayName) : q.OrderBy(u => u.DisplayName)),
                ["email"] = ("E-Mail", (q, d) => d ? q.OrderByDescending(u => u.Email) : q.OrderBy(u => u.Email)),
                ["role"] = ("Rolle", (q, d) => d ? q.OrderByDescending(u => u.Role).ThenBy(u => u.DisplayName) : q.OrderBy(u => u.Role).ThenBy(u => u.DisplayName)),
                ["login"] = ("Letzte Anmeldung", (q, d) => d ? q.OrderByDescending(u => u.LastLoginDate) : q.OrderBy(u => u.LastLoginDate)),
            },
            DefaultSort = "name",
        };

        return ListBuilder.RunAsync(db.User.AsNoTracking().Include(u => u.Totp), request, spec, ct);
    }

    /// <summary>A user with its second factor, for reading; null when there is none.</summary>
    public Task<User?> FindAsync(long id, CancellationToken ct = default)
        => db.User.AsNoTracking().Include(u => u.Totp).FirstOrDefaultAsync(u => u.Id == id, ct);

    /// <summary>A user with its second factor, to change it with the methods of this class or of <see cref="TwoFactorService"/>; null when there is none.</summary>
    public Task<User?> FindForChangeAsync(long id, CancellationToken ct = default)
        => db.User.Include(u => u.Totp).FirstOrDefaultAsync(u => u.Id == id, ct);

    /// <summary>Whether another active administrator than <paramref name="exceptUserId"/> exists: there must always be one.</summary>
    public Task<bool> OtherActiveAdminExistsAsync(long? exceptUserId, CancellationToken ct = default)
        => db.User.AnyAsync(u => u.Id != exceptUserId && u.Role == UserRole.Admin && u.IsActive, ct);

    /// <summary>Creates a user with a telephone number and whether it may sign in.</summary>
    public async Task<User> CreateAsync(string email, string displayName, UserRole role, string password, string? phone, bool isActive, CancellationToken ct = default)
    {
        var user = await CreateAsync(email, displayName, role, password, ct);
        user.Phone = Clean(phone);
        user.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Changes name, address, telephone and role of a user (the users page); null when there is none.</summary>
    public async Task<User?> UpdateAsync(long id, string displayName, string email, string? phone, UserRole role, CancellationToken ct = default)
    {
        var user = await db.User.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return null;
        }

        user.DisplayName = displayName.Trim();
        user.Email = SignInService.NormalizeEmail(email);
        user.Phone = Clean(phone);
        user.Role = role;
        await db.SaveChangesAsync(ct);
        cache.InvalidateUser(user.Id);
        return user;
    }

    /// <summary>Changes what a person sets on their own profile (from <see cref="FindForChangeAsync"/>).</summary>
    public async Task UpdateProfileAsync(User user, string displayName, string? phone, string theme, CancellationToken ct = default)
    {
        user.DisplayName = displayName.Trim();
        user.Phone = Clean(phone);
        user.Theme = theme is "light" or "dark" ? theme : "system";
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Lifts the lock after wrong passwords; false when there is no such user.</summary>
    public async Task<bool> UnlockAsync(long id, CancellationToken ct = default)
    {
        var user = await db.User.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }

        user.FailedLogins = 0;
        user.LockoutEndDate = null;
        await db.SaveChangesAsync(ct);
        cache.InvalidateUser(user.Id);
        return true;
    }

    /// <summary>Deletes a user (softly) after ending every session of it; its entries stay. The caller checks who may be deleted.</summary>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var user = await db.User.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return;
        }

        await signIn.EndAllSessionsAsync(user.Id, null, ct);
        db.User.Remove(user);
        await db.SaveChangesAsync(ct);
        cache.InvalidateUser(user.Id);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    // ------------------------------------------------------------------------------------------------ MatIdentity

    /// <summary>
    /// A person that MatIdentity signed in: the user linked with its <c>sub</c>; else, when exactly one active account that is not linked has the person's
    /// e-mail address as its sign-in, that account's id to prove first (an address proves nothing: anyone may have typed it); else a new user. Refused: a
    /// disabled or deleted user, several matches, a match linked with another person, a match without a password. The claim <c>role</c>
    /// (<c>admin</c>, <c>user</c>) sets the role at every sign-in, never taking it from the last administrator; without it the role stays.
    /// </summary>
    public async Task<ExternalSignIn> SignInExternalAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignIn.Refuse("MatIdentity hat keine Kennung (sub) geschickt.");
        }

        var subject = identity.Subject.Trim();

        // Also a deleted user: its subject stays with it, and a deleted account does not come back through MatIdentity.
        var user = await db.User.IgnoreQueryFilters().Include(u => u.Totp).FirstOrDefaultAsync(u => u.ExternalSubject == subject, ct);
        if (user is not null && (user.UpdateState == UpdateState.Deleted || !user.IsActive))
        {
            return ExternalSignIn.Refuse(DisabledMessage);
        }

        var created = false;
        if (user is null)
        {
            var names = Addresses(identity);
            var matches = names.Count == 0 ? [] : await db.User.AsNoTracking().Where(u => names.Contains(u.Email)).ToListAsync(ct);
            if (matches.Count > 1)
            {
                logger.LogWarning("MatIdentity subject {Subject} matches {Count} accounts; refused.", subject, matches.Count);
                return ExternalSignIn.Refuse("Mehrere Konten in MatSMTP passen zu dieser Person. Bitte wende dich an die Administration.");
            }

            if (matches.Count == 1)
            {
                var match = matches[0];
                if (match.ExternalSubject is not null)
                {
                    logger.LogWarning("MatIdentity subject {Subject} matches user {UserId}, which is linked with another subject; refused.", subject, match.Id);
                    return ExternalSignIn.Refuse("Das Konto mit dieser Adresse ist mit einer anderen Person verbunden.");
                }

                if (!match.IsActive)
                {
                    return ExternalSignIn.Refuse(DisabledMessage);
                }

                if (string.IsNullOrEmpty(match.PasswordHash))
                {
                    return ExternalSignIn.Refuse("Das Konto mit dieser Adresse hat kein Passwort und kann sich nicht ausweisen.");
                }

                // Not linked by the address alone: the person proves the account with its password first.
                logger.LogInformation("MatIdentity subject {Subject} matches user {UserId} by address; the link waits for its password.", subject, match.Id);
                return ExternalSignIn.LinkRequired(match.Id);
            }

            user = new User
            {
                Email = names.FirstOrDefault() ?? $"{subject}@matidentity.invalid",
                DisplayName = Clip(FirstNonEmpty(identity.Name, identity.PreferredUserName, identity.Email) ?? "MatIdentity", 200),
                PasswordHash = null,
                Role = UserRole.User,
                ExternalSubject = subject,
            };
            db.User.Add(user);
            created = true;
        }

        await ApplyRoleAsync(user, identity.Role, created, ct);
        await db.SaveChangesAsync(ct);
        cache.InvalidateUser(user.Id);
        if (created)
        {
            logger.LogInformation("User {Email} ({UserId}) was created at the first sign-in through MatIdentity.", user.Email, user.Id);
        }

        return ExternalSignIn.SignedIn(user);
    }

    /// <summary>
    /// Links the user <paramref name="userId"/> with the person of <paramref name="identity"/> after the user proved the account with its password (and its
    /// second factor): sets the subject and follows the role claim like a sign-in through MatIdentity. Null (nothing changed) when the account is gone,
    /// disabled or linked meanwhile, or another user has the subject by now.
    /// </summary>
    public async Task<User?> LinkExternalAsync(long userId, ExternalIdentity identity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return null;
        }

        var subject = identity.Subject.Trim();
        var user = await db.User.Include(u => u.Totp).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        if (user.ExternalSubject is not null || await db.User.IgnoreQueryFilters().AnyAsync(u => u.ExternalSubject == subject, ct))
        {
            logger.LogWarning("User {UserId} was not linked with MatIdentity subject {Subject}: one of them is linked already.", userId, subject);
            return null;
        }

        user.ExternalSubject = subject;
        await ApplyRoleAsync(user, identity.Role, false, ct);
        await db.SaveChangesAsync(ct);
        cache.InvalidateUser(user.Id);
        logger.LogInformation("User {Email} ({UserId}) linked with MatIdentity subject {Subject} after its password.", user.Email, user.Id, subject);
        return user;
    }

    /// <summary>The claim <c>role</c>: <c>admin</c> or <c>user</c> sets the role (never demoting the last active administrator); anything else leaves it.</summary>
    private async Task ApplyRoleAsync(User user, string? claim, bool created, CancellationToken ct)
    {
        UserRole? wanted = claim?.Trim().ToLowerInvariant() switch
        {
            "admin" => UserRole.Admin,
            "user" => UserRole.User,
            _ => null,
        };

        if (wanted is not { } role || user.Role == role)
        {
            return;
        }

        if (!created && user.Role == UserRole.Admin && !await db.User.AnyAsync(u => u.Id != user.Id && u.IsActive && u.Role == UserRole.Admin, ct))
        {
            logger.LogWarning("MatIdentity asks for role {Role} for user {UserId}, the last administrator; the role stays.", role, user.Id);
            return;
        }

        user.Role = role;
    }

    /// <summary>The addresses a local account of the person could sign in with: the e-mail and a <c>preferred_username</c> that is one, in small letters.</summary>
    private static List<string> Addresses(ExternalIdentity identity)
        => new[] { identity.Email, identity.PreferredUserName }
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.Contains('@'))
            .Select(SignInService.NormalizeEmail)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Clip(string value, int max) => value.Length > max ? value[..max] : value;
}
