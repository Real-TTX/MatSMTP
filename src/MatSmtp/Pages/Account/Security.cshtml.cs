using System.Security.Claims;
using MatSmtp.Core.Security;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Account;

/// <summary>The second factor (authenticator app, recovery codes) and the devices that are signed in.</summary>
public sealed class SecurityModel(UserService users, ICurrentUser current, TwoFactorService twoFactor, SignInService signIn, ActivityLogger log) : PageModelBase
{
    public sealed record SessionRow(long Id, string Device, string? Ip, DateTime LastSeen, DateTime Created, bool IsThis);

    public bool Enabled { get; private set; }

    public TwoFactorEnrollment? Enrollment { get; private set; }

    /// <summary>Shown once, right after the second factor was set up or the codes were renewed.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public int RemainingCodes { get; private set; }

    public List<SessionRow> Sessions { get; private set; } = [];

    public bool RequiredButMissing { get; private set; }

    [BindProperty]
    public string? Code { get; set; }

    [BindProperty]
    public string? Confirm { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostBeginAsync()
    {
        var user = await LoadUserAsync();
        if (user.Totp is { IsConfirmed: false } || user.Totp is null)
        {
            Enrollment = await twoFactor.BeginEnrollmentAsync(user);
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        var user = await LoadUserAsync();
        var codes = await twoFactor.ConfirmEnrollmentAsync(user, Code);
        if (codes is null)
        {
            ModelState.AddModelError(nameof(Code), "Der Code stimmt nicht. Prüfe die Uhrzeit deines Handys und versuche es noch einmal.");
            await LoadAsync(user);
            Enrollment = await twoFactor.PendingAsync(user);
            return Page();
        }

        await log.LogAsync("Zweiter Faktor eingerichtet", "User", user.Id);
        await LoadAsync(user);
        RecoveryCodes = codes;
        return Page();
    }

    public async Task<IActionResult> OnPostNewCodesAsync()
    {
        var user = await LoadUserAsync();
        if (!await VerifyOwnerAsync(user))
        {
            await LoadAsync(user);
            return Page();
        }

        var codes = await twoFactor.NewRecoveryCodesAsync(user);
        await LoadAsync(user);
        RecoveryCodes = codes;
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await LoadUserAsync();
        if (!await VerifyOwnerAsync(user))
        {
            await LoadAsync(user);
            return Page();
        }

        await twoFactor.DisableAsync(user);
        await log.LogAsync("Zweiter Faktor entfernt", "User", user.Id);
        Flash("Der zweite Faktor ist entfernt.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEndSessionAsync(long id)
    {
        if (await signIn.EndSessionAsync(id, current.UserId!.Value, HttpContext.RequestAborted))
        {
            Flash("Das Gerät ist abgemeldet.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEndOthersAsync()
    {
        await signIn.EndAllSessionsAsync(current.UserId!.Value, ThisSession());
        Flash("Alle anderen Geräte sind abgemeldet.");
        return RedirectToPage();
    }

    private Guid? ThisSession() => Guid.TryParse(User.FindFirstValue(AppClaims.Session), out var token) ? token : null;

    /// <summary>Changing the security of the account needs proof that the person at the keyboard is the owner: the password, or the code of the app for accounts without one.</summary>
    private async Task<bool> VerifyOwnerAsync(User user)
    {
        if (user.PasswordHash is not null)
        {
            if (PasswordHasher.Verify(Confirm ?? string.Empty, user.PasswordHash))
            {
                return true;
            }

            ModelState.AddModelError(nameof(Confirm), "Das Passwort stimmt nicht.");
            return false;
        }

        if (await twoFactor.VerifyAsync(user, Confirm))
        {
            return true;
        }

        ModelState.AddModelError(nameof(Confirm), "Der Code stimmt nicht.");
        return false;
    }

    private async Task<User> LoadUserAsync()
        => await users.FindForChangeAsync(current.UserId!.Value, HttpContext.RequestAborted) ?? throw new InvalidOperationException("The signed-in user is gone.");

    private async Task LoadAsync(User? user = null)
    {
        user ??= await LoadUserAsync();
        Enabled = user.Totp is { IsConfirmed: true };
        Head("Sicherheit", "Zweiter Faktor und angemeldete Geräte.", ("Mein Konto", "/Account/Profile"), ("Sicherheit", null));
        if (Enabled)
        {
            RemainingCodes = await twoFactor.RemainingRecoveryCodesAsync(user);
        }

        RequiredButMissing = !Enabled && user.Role == UserRole.Admin && HttpContext.RequestServices.GetRequiredService<Configuration.ConfigStore>().Current.Security.RequireTotpForAdmins;
        var token = ThisSession();
        Sessions = (await signIn.ActiveSessionsAsync(user.Id, HttpContext.RequestAborted))
            .Select(s => new SessionRow(s.Id, DeviceName(s.UserAgent), s.IpAddress, s.LastSeenDate, s.CreateDate, s.Token == token))
            .ToList();
    }

    /// <summary>A readable name from a user-agent string: "Edge auf Windows".</summary>
    public static string DeviceName(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return "Unbekanntes Gerät";
        }

        var browser = agent.Contains("Edg/") ? "Edge" : agent.Contains("OPR/") ? "Opera" : agent.Contains("Firefox/") ? "Firefox"
            : agent.Contains("Chrome/") ? "Chrome" : agent.Contains("Safari/") ? "Safari" : "Browser";
        var os = agent.Contains("Windows") ? "Windows" : agent.Contains("Android") ? "Android" : agent.Contains("iPhone") || agent.Contains("iPad") ? "iOS"
            : agent.Contains("Mac OS") ? "macOS" : agent.Contains("Linux") ? "Linux" : "unbekanntem System";
        return $"{browser} auf {os}";
    }
}
