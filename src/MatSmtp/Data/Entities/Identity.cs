namespace MatSmtp.Data.Entities;

/// <summary>The simple role system: administrator and user. Anonymous is a share link (the delivery of one message), not a user.</summary>
public enum UserRole
{
    Admin,
    User,
}

public class User : AuditedEntity, ISoftDeletable
{
    /// <summary>The sign-in name (lower case).</summary>
    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string? PasswordHash { get; set; }

    public UserRole Role { get; set; } = UserRole.User;

    public bool IsActive { get; set; } = true;

    public string? Phone { get; set; }

    /// <summary>"system", "light" or "dark": kept per user, applied at sign-in.</summary>
    public string Theme { get; set; } = "system";

    public DateTime? LastLoginDate { get; set; }

    public int FailedLogins { get; set; }

    public DateTime? LockoutEndDate { get; set; }

    public DateTime? PasswordChangedDate { get; set; }

    /// <summary>The person in MatIdentity (claim <c>sub</c>) this user is linked with; null: only the local sign-in.</summary>
    public string? ExternalSubject { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public UserTotp? Totp { get; set; }

    /// <summary>The initials for the round avatar: two letters of the display name.</summary>
    public string Initials => Services.Fmt.Initials(DisplayName);
}

/// <summary>The second factor of a user (RFC 6238). It counts only once <see cref="IsConfirmed"/> is set by a valid first code.</summary>
public class UserTotp : AuditedEntity
{
    public long UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The shared secret, protected with the key ring of the application.</summary>
    public string Secret { get; set; } = "";

    public bool IsConfirmed { get; set; }

    /// <summary>The time step of the last accepted code: a code works once.</summary>
    public long LastUsedStep { get; set; }

    public List<UserRecoveryCode> RecoveryCodes { get; set; } = [];
}

public class UserRecoveryCode : AuditedEntity
{
    public long UserTotpId { get; set; }

    public UserTotp? UserTotp { get; set; }

    public string CodeHash { get; set; } = "";

    public DateTime? UsedDate { get; set; }
}

/// <summary>A signed-in browser. The cookie carries only <see cref="Token"/>; the session is valid as long as this row is.</summary>
public class UserSession : AuditedEntity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime ExpiresDate { get; set; }

    public DateTime LastSeenDate { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTime? RevokedDate { get; set; }

    /// <summary>The ID token of a sign-in through MatIdentity: the hint for the sign-out there. Null for a local sign-in.</summary>
    public string? IdToken { get; set; }
}
