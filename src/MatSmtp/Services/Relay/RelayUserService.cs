using System.Text.RegularExpressions;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Relay;

/// <summary>What the page of a relay user saves.</summary>
public sealed record RelayUserInput(string UserName, string? Description, string? Password, string? AllowedSenders, bool IsEnabled);

/// <summary>What the management API sends for a relay user (<c>PUT /api/v1/relay-users/{id}</c>).</summary>
public sealed record RelayUserApiInput(string? DisplayName, IReadOnlyList<string>? AllowedSenders, string? ManagedBy, bool RenewSecret);

/// <summary>How an upsert of the API ended: the user and its new password (only when it was made or renewed), or why not (<see cref="Status"/> 400 or 409).</summary>
public sealed record RelayUserApiResult(int Status, string? Error, RelayUser? User = null, string? Password = null, bool Created = false)
{
    public static RelayUserApiResult Fail(int status, string error) => new(status, error);
}

/// <summary>A relay user as a binding of the capability <c>mail.smtp</c>: the consumer (its id), its name, who manages it, since when and when it last signed in.</summary>
public sealed record RelayUserBinding(string Consumer, string Name, string ManagedBy, DateTime Since, DateTime? LastUsed);

/// <summary>
/// The relay users: the accounts applications sign in with at the receiver (SMTP AUTH). The pages, the management API of Mattiverse, the bindings of
/// <c>mail.smtp</c> (MatPlatform) and the receiver use this one class. Passwords are only ever stored as a hash; a generated one is handed out once. A user
/// that the API made with <c>managedBy</c> belongs to that caller: the API lets only the same <c>managedBy</c> change or delete it (the pages may, and show a badge).
/// </summary>
public sealed partial class RelayUserService(MsDbContext db, ConfigStore config, TimeProvider clock, ILogger<RelayUserService> logger)
{
    /// <summary>Who made a binding by hand (the Connections area); a relay user of the API without <c>managedBy</c> is listed as one too.</summary>
    public const string ManagedByHand = "manual";

    /// <summary>The domain of the sender of a binding while the settings name no default sender domain.</summary>
    public const string BindingDomainFallback = "localhost";

    /// <summary>The shortest password the page accepts (a generated one is longer).</summary>
    public const int MinPasswordLength = 12;

    /// <summary>The length of a generated password (32 characters of a 32-letter alphabet: 160 bits).</summary>
    public const int GeneratedLength = 32;

    [GeneratedRegex("^[a-z0-9-]{2,64}$")]
    private static partial Regex ApiIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._@+\-]{2,100}$")]
    private static partial Regex UserNamePattern();

    // The part of an address before the @ as a binding passes it: no pattern (*, /.../), no list, no second @.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,63}$")]
    private static partial Regex LocalPartPattern();

    public static bool IsValidApiId(string? id) => id is not null && ApiIdPattern().IsMatch(id);

    public static bool IsValidUserName(string? name) => name is not null && UserNamePattern().IsMatch(name);

    public static string GeneratePassword() => PasswordHasher.RandomCode(GeneratedLength);

    // ------------------------------------------------------------------------------------------------ the pages

    /// <summary>The list of the page: search, filter, sort and page of the request.</summary>
    public Task<ListResult<RelayUser>> ListAsync(HttpRequest request, CancellationToken ct)
    {
        var spec = new ListSpec<RelayUser>
        {
            Id = "relayusers",
            Action = "/RelayUsers",
            Label = "Zugänge",
            Noun = "Zugänge",
            SearchPlaceholder = "Benutzername oder Beschreibung ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(u => EF.Functions.Like(u.UserName, like, Like.Escape) || EF.Functions.Like(u.Description ?? "", like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<RelayUser>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Gesperrt")], (q, v) => v == "aus" ? q.Where(u => !u.IsEnabled) : q.Where(u => u.IsEnabled)),
                new FilterSpec<RelayUser>("verwaltet", "Verwaltet", [new("api", "Über die API"), new("hier", "Hier angelegt")], (q, v) => v == "api" ? q.Where(u => u.ManagedBy != null) : q.Where(u => u.ManagedBy == null)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<RelayUser>, bool, IOrderedQueryable<RelayUser>>)>
            {
                ["name"] = ("Benutzername", (q, d) => d ? q.OrderByDescending(u => u.UserName) : q.OrderBy(u => u.UserName)),
                ["used"] = ("Zuletzt benutzt", (q, d) => d ? q.OrderByDescending(u => u.LastUsedDate) : q.OrderBy(u => u.LastUsedDate)),
            },
            DefaultSort = "name",
        };

        return ListBuilder.RunAsync(db.RelayUser.AsNoTracking(), request, spec, ct);
    }

    public Task<RelayUser?> FindAsync(long id, CancellationToken ct = default)
        => db.RelayUser.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);

    /// <summary>How many rules apply only to this relay user.</summary>
    public Task<int> CountRulesAsync(long id, CancellationToken ct = default) => db.Rule.CountAsync(r => r.MatchRelayUserId == id, ct);

    /// <summary>The problems of an input per field (<c>UserName</c>, <c>Password</c>, <c>AllowedSenders</c>); empty: it can be saved.</summary>
    public async Task<Dictionary<string, string>> CheckAsync(long? id, RelayUserInput input, bool generated, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var name = input.UserName.Trim();
        if (name.Length == 0)
        {
            errors["UserName"] = "Bitte gib einen Benutzernamen ein.";
        }
        else if (!IsValidUserName(name))
        {
            errors["UserName"] = "2 bis 100 Zeichen: Buchstaben, Ziffern und . _ @ + -";
        }
        else if (await db.RelayUser.AnyAsync(u => u.UserName == name && u.Id != id, ct))
        {
            errors["UserName"] = "Diesen Benutzernamen gibt es schon.";
        }

        if (AddressPattern.Problem(input.AllowedSenders) is { } problem)
        {
            errors["AllowedSenders"] = problem;
        }

        if (!generated && !string.IsNullOrEmpty(input.Password) && input.Password.Length < MinPasswordLength)
        {
            errors["Password"] = $"Mindestens {MinPasswordLength} Zeichen. Oder lass dir eines erzeugen.";
        }
        else if (id is null && string.IsNullOrEmpty(input.Password))
        {
            errors["Password"] = "Bitte vergib ein Passwort, oder lass dir eines erzeugen.";
        }

        return errors;
    }

    /// <summary>Saves the input of the page (checked with <see cref="CheckAsync"/>); a password that is empty keeps the old one.</summary>
    public async Task<RelayUser> SaveAsync(long? id, RelayUserInput input, CancellationToken ct = default)
    {
        var user = id is { } existing ? await db.RelayUser.FirstAsync(u => u.Id == existing, ct) : db.RelayUser.Add(new RelayUser()).Entity;
        user.UserName = input.UserName.Trim();
        user.Description = Clean(input.Description);
        user.AllowedSenders = string.IsNullOrWhiteSpace(input.AllowedSenders) ? null : string.Join("\n", AddressPattern.Split(input.AllowedSenders));
        user.IsEnabled = input.IsEnabled;
        if (!string.IsNullOrEmpty(input.Password))
        {
            user.PasswordHash = PasswordHasher.Hash(input.Password);
        }

        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Deletes a relay user (softly); null when there is none.</summary>
    public async Task<RelayUser?> DeleteAsync(long id, CancellationToken ct = default)
    {
        var user = await db.RelayUser.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return null;
        }

        db.RelayUser.Remove(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    // ------------------------------------------------------------------------------------------------ the receiver

    /// <summary>
    /// Checks the user name and password of an SMTP AUTH and notes the use. False for an unknown, disabled or deleted user or a wrong password
    /// (each costs the same: the hash is checked anyway).
    /// </summary>
    public async Task<bool> AuthenticateAsync(string? userName, string? password, CancellationToken ct = default)
    {
        var name = (userName ?? "").Trim();
        var user = name.Length == 0 ? null : await db.RelayUser.FirstOrDefaultAsync(u => u.UserName == name, ct);
        var valid = PasswordHasher.Verify(password ?? "", user?.PasswordHash ?? DummyHash);
        if (user is null || !user.IsEnabled || !valid)
        {
            return false;
        }

        user.LastUsedDate = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Whether the relay user <paramref name="userName"/> may send as these senders: the envelope (MAIL FROM) and the From of the header, each when there is one.
    /// Null when it may, else the address it may not use. A user without a list may use any sender.
    /// </summary>
    /// <remarks>The address as the application gave it counts as much as the one with the default domain (<paramref name="givenEnvelopeFrom"/>,
    /// <paramref name="givenHeaderFrom"/>): a relay user allowed to send as <c>app@localhost</c> may, and the message leaves as <c>app@example.com</c>.</remarks>
    public async Task<string?> DisallowedSenderAsync(string userName, string envelopeFrom, string? headerFrom, CancellationToken ct = default,
        string? givenEnvelopeFrom = null, string? givenHeaderFrom = null)
    {
        var allowed = await db.RelayUser.AsNoTracking().Where(u => u.UserName == userName).Select(u => u.AllowedSenders).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(allowed))
        {
            return null;
        }

        // An empty envelope sender (a bounce, <>) is judged by the From of the header alone; "*" allows it too.
        if (envelopeFrom.Length > 0 && !AddressPattern.MatchesAny(allowed, envelopeFrom) && !AddressPattern.MatchesAny(allowed, givenEnvelopeFrom))
        {
            return envelopeFrom;
        }

        if (!string.IsNullOrEmpty(headerFrom) && !AddressPattern.MatchesAny(allowed, headerFrom) && !AddressPattern.MatchesAny(allowed, givenHeaderFrom))
        {
            return headerFrom;
        }

        return envelopeFrom.Length == 0 && string.IsNullOrEmpty(headerFrom) && !AddressPattern.MatchesAny(allowed, "") ? "<>" : null;
    }

    private static readonly string DummyHash = PasswordHasher.Hash("not-a-relay-password");

    // ------------------------------------------------------------------------------------------------ the management API

    /// <summary>The relay users of the API, ordered by their id.</summary>
    public Task<List<RelayUser>> ListApiAsync(CancellationToken ct = default)
        => db.RelayUser.AsNoTracking().Where(u => u.ApiId != null).OrderBy(u => u.ApiId).ToListAsync(ct);

    /// <summary>
    /// Creates or changes the relay user <paramref name="apiId"/> (its user name is the id). A password is generated when it is made and when
    /// <see cref="RelayUserApiInput.RenewSecret"/> asks for it, and only then returned. 409 when another <c>managedBy</c> owns it or a user of the page has
    /// the name; 400 for a wrong input.
    /// </summary>
    public async Task<RelayUserApiResult> UpsertApiAsync(string apiId, RelayUserApiInput input, CancellationToken ct = default)
    {
        if (!IsValidApiId(apiId))
        {
            return RelayUserApiResult.Fail(400, "The id must be 2 to 64 characters: small letters, digits and '-'.");
        }

        var senders = (input.AllowedSenders ?? []).Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (senders.Count == 0)
        {
            return RelayUserApiResult.Fail(400, "allowedSenders needs at least one entry (\"*\" allows every sender).");
        }

        if (senders.FirstOrDefault(s => s.IndexOfAny([',', ';', ' ']) >= 0) is { } wrong)
        {
            return RelayUserApiResult.Fail(400, $"\"{wrong}\" is not one sender: send one address or pattern per entry.");
        }

        var list = string.Join("\n", senders);
        if (AddressPattern.Problem(list) is { } problem)
        {
            return RelayUserApiResult.Fail(400, problem);
        }

        var managedBy = Clean(input.ManagedBy);
        var displayName = Clean(input.DisplayName);
        if (displayName?.Length > 300 || managedBy?.Length > 100)
        {
            return RelayUserApiResult.Fail(400, "displayName may have 300 characters, managedBy 100.");
        }

        var user = await db.RelayUser.FirstOrDefaultAsync(u => u.ApiId == apiId, ct);
        if (user is not null && user.ManagedBy is not null && !string.Equals(user.ManagedBy, managedBy, StringComparison.Ordinal))
        {
            return RelayUserApiResult.Fail(409, $"The relay user {apiId} is managed by {user.ManagedBy}.");
        }

        if (user is null && await db.RelayUser.AnyAsync(u => u.UserName == apiId, ct))
        {
            return RelayUserApiResult.Fail(409, $"A relay user named {apiId} was made in MatSMTP itself; rename or delete it there first.");
        }

        return await SaveApiUserAsync(user, apiId, displayName, list, managedBy ?? user?.ManagedBy, input.RenewSecret, ct);
    }

    /// <summary>
    /// Writes a relay user of the API (<paramref name="user"/>, or a new one named <paramref name="apiId"/> when it is null): its name, its senders, who manages it
    /// and, when it is new or <paramref name="newPassword"/>, a generated password that is returned once. The one place that makes and changes such users.
    /// </summary>
    private async Task<RelayUserApiResult> SaveApiUserAsync(RelayUser? user, string apiId, string? displayName, string senders, string? managedBy, bool newPassword, CancellationToken ct)
    {
        var created = user is null;
        user ??= db.RelayUser.Add(new RelayUser { ApiId = apiId, UserName = apiId }).Entity;
        user.ApiId = apiId;
        user.Description = displayName;
        user.AllowedSenders = senders;
        user.ManagedBy = managedBy;
        string? password = null;
        if (created || newPassword)
        {
            password = GeneratePassword();
            user.PasswordHash = PasswordHasher.Hash(password);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two calls for the same id at the same moment: the unique index decides, the second one hears so.
            logger.LogWarning(ex, "The relay user {ApiId} could not be saved.", apiId);
            return RelayUserApiResult.Fail(409, $"The relay user {apiId} was changed at the same time; try again.");
        }

        return new RelayUserApiResult(created ? 201 : 200, null, user, password, created);
    }

    /// <summary>Deletes the relay user <paramref name="apiId"/>: 204, 404 when there is none, 409 when another <c>managedBy</c> owns it.</summary>
    public async Task<RelayUserApiResult> DeleteApiAsync(string apiId, string? managedBy, CancellationToken ct = default)
    {
        var user = IsValidApiId(apiId) ? await db.RelayUser.FirstOrDefaultAsync(u => u.ApiId == apiId, ct) : null;
        if (user is null)
        {
            return RelayUserApiResult.Fail(404, $"There is no relay user {apiId}.");
        }

        if (user.ManagedBy is not null && !string.Equals(user.ManagedBy, Clean(managedBy), StringComparison.Ordinal))
        {
            return RelayUserApiResult.Fail(409, $"The relay user {apiId} is managed by {user.ManagedBy}.");
        }

        db.RelayUser.Remove(user);
        await db.SaveChangesAsync(ct);
        return new RelayUserApiResult(204, null, user);
    }

    // ------------------------------------------------------------------------------------------------ the bindings of mail.smtp (MatPlatform)

    /// <summary>The relay users of the API as the bindings of <c>mail.smtp</c>; one without <c>managedBy</c> counts as made by hand.</summary>
    public async Task<IReadOnlyList<RelayUserBinding>> ListBindingsAsync(CancellationToken ct = default)
        => [.. (await ListApiAsync(ct)).Select(u => new RelayUserBinding(u.ApiId!, u.Description ?? u.UserName, u.ManagedBy ?? ManagedByHand, u.CreateDate, u.LastUsedDate))];

    /// <summary>The id of the relay user of a consumer: the consumer id, with <c>-app</c> when it is shorter than an id of the API may be (<c>x</c> → <c>x-app</c>).</summary>
    public static string BindingIdOf(string consumer) => consumer.Length < 2 ? consumer + "-app" : consumer;

    /// <summary>
    /// The sender of a binding: <paramref name="sender"/> (the part before the <c>@</c>, default: the consumer id) at the default sender domain of the settings, else at
    /// <c>localhost</c> (which the relay turns into the default domain once there is one). A part that is no plain local part (a pattern, a list) counts as none.
    /// </summary>
    public string SenderOf(string consumer, string? sender)
    {
        var local = Clean(sender) is { } given && LocalPartPattern().IsMatch(given) ? given : BindingIdOf(consumer);
        var domain = Clean(config.Current.Smtp.DefaultSenderDomain)?.TrimStart('@').ToLowerInvariant();
        return $"{local}@{domain ?? BindingDomainFallback}";
    }

    /// <summary>
    /// Makes or updates the relay user of a binding of <c>mail.smtp</c> (docs/PLATFORM.md of MatDesign): its id is the consumer's, its description the consumer's name,
    /// <c>managedBy</c> the caller's and <see cref="SenderOf"/> its only allowed sender (the <c>From</c> of the binding). The same call twice is one user. A password is
    /// made when the binding is new or <paramref name="renew"/>; else it is null and the old one stays. Unlike <see cref="UpsertApiAsync"/> a binding never refuses:
    /// the caller of a binding (a broker with the key of the app, an administrator in the Connections area) takes over a user of another <c>managedBy</c>, and a user
    /// of the pages with the consumer's name becomes the binding; since it was no binding before, it gets a new password like a new one.
    /// </summary>
    /// <remarks>Not called <c>BindAsync</c>: a minimal API takes a method of that name on the type of a parameter (this service) for its own binding.</remarks>
    public async Task<RelayUserApiResult> UpsertBindingAsync(string consumer, string? name, string? sender, string managedBy, bool renew, CancellationToken ct = default)
    {
        var apiId = BindingIdOf(consumer ?? "");
        if (!IsValidApiId(apiId))
        {
            return RelayUserApiResult.Fail(400, "The consumer id must be 2 to 64 characters: small letters, digits and '-'.");
        }

        var displayName = Clean(name);
        var user = await db.RelayUser.FirstOrDefaultAsync(u => u.ApiId == apiId, ct)
            ?? await db.RelayUser.FirstOrDefaultAsync(u => u.ApiId == null && u.UserName == apiId, ct);
        var adopted = user is not null && user.ApiId is null;
        return await SaveApiUserAsync(user, apiId, displayName is { Length: > 300 } ? displayName[..300] : displayName, SenderOf(apiId, sender),
            Clean(managedBy) ?? ManagedByHand, renew || adopted, ct);
    }

    /// <summary>Removes the relay user of a binding: only one made by <paramref name="managedBy"/> (null: any). Null when there is none (or it is another's).</summary>
    public async Task<RelayUser?> RemoveBindingAsync(string consumer, string? managedBy, CancellationToken ct = default)
    {
        var apiId = BindingIdOf(consumer ?? "");
        var user = IsValidApiId(apiId) ? await db.RelayUser.FirstOrDefaultAsync(u => u.ApiId == apiId, ct) : null;
        if (user is null || (Clean(managedBy) is { } by && (user.ManagedBy ?? ManagedByHand) != by))
        {
            return null;
        }

        db.RelayUser.Remove(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
