using System.Globalization;
using MatPlatform;
using MatSmtp.Services;
using MatSmtp.Services.Auth;

namespace MatSmtp.Platform;

/// <summary>
/// MatSMTP's side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): the person is mapped to a local user by
/// <see cref="UserService.SignInExternalAsync"/> (by <c>sub</c>, else a new user; the claim <c>role</c> when it is there) and signed in with MatSMTP's
/// own session and cookie, keeping the ID token with the session for the sign-out at MatIdentity. MatIdentity has checked the second factor; the local
/// one is for the local sign-in. An account with the same address is not linked by the address: the answer is
/// <see cref="MatIdentitySignInResult.LinkRequired"/>, and the sign-in page links it after its password (<see cref="MatIdentityPendingLink"/>).
/// The session keeps the <c>sid</c> of MatIdentity: a back-channel logout (<see cref="IMatIdentitySessions"/>) revokes the session rows it names
/// (<see cref="SignInService.EndIdentitySessionsAsync"/>), so the next request of such a session is signed out.
/// </summary>
public sealed class SmtpIdentityUsers(UserService users, SignInService signIn, ActivityLogger log, ICurrentUser current) : IMatIdentityUsers, IMatIdentitySessions
{
    public async Task EndSessionsAsync(string? subject, string? sessionId, CancellationToken ct)
    {
        foreach (var ended in await signIn.EndIdentitySessionsAsync(subject, sessionId, ct))
        {
            using (current.RunAs(ended.UserId, ended.DisplayName, ended.Role))
            {
                var what = sessionId is null ? "überall" : "Sitzung " + sessionId;
                await log.LogAsync(
                    "Abmeldung über MatIdentity",
                    "User",
                    ended.UserId,
                    string.Create(CultureInfo.InvariantCulture, $"{ended.Email}: {what}, {ended.Count} Sitzung(en) beendet"));
            }
        }
    }

    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var result = await users.SignInExternalAsync(ToIdentity(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role), ct);
        if (result.Refused)
        {
            return MatIdentitySignInResult.Refused(result.Error!);
        }

        if (result.LinkUserId is { } linkUserId)
        {
            return MatIdentitySignInResult.LinkRequired(linkUserId.ToString(CultureInfo.InvariantCulture));
        }

        var user = result.User!;
        await signIn.StartSessionAsync(http, user, ct, person.IdToken, person.SessionId);
        using (current.RunAs(user.Id, user.DisplayName, user.Role))
        {
            await log.LogAsync("Anmeldung über MatIdentity", "User", user.Id, user.Email);
        }

        return MatIdentitySignInResult.Success();
    }

    public static ExternalIdentity ToIdentity(string subject, string? email, string? name, string? preferredUserName, string? role)
        => new(subject, email, name, preferredUserName, role);

    /// <summary>The pending link of the sign-in page as the person it stands for.</summary>
    public static ExternalIdentity ToIdentity(MatIdentityPendingLink link)
        => new(link.Subject, link.Email, link.Name, link.PreferredUserName, link.Role);
}
