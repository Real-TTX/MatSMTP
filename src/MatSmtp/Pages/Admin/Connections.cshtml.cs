namespace MatSmtp.Pages.Admin;

/// <summary>
/// The Connections area (MatPlatform 2.1, MatDesign docs/PLATFORM.md "The Connections area"): what MatSMTP uses of other apps (the sign-in through MatIdentity), what
/// it offers them (<c>mail.smtp</c>: the apps that send through it, each a relay user made by Mattiverse or here by hand, with a new password and remove) and what it
/// adds to a home screen. The content is the shared <c>&lt;mat-connections /&gt;</c> of MatPlatform, the same in every Mat app; its forms post to MatPlatform.
/// Administrators only (the folder, and <c>ConnectionsAccess</c> in Program.cs).
/// </summary>
public sealed class ConnectionsModel : PageModelBase
{
    public void OnGet()
        => Head("Verbindungen", "Was MatSMTP anderen Apps anbietet und von ihnen nutzt. Mattiverse verbindet die Apps von selbst; ohne Mattiverse verbindest du eine App hier von Hand.",
            ("Einstellungen", null), ("Verbindungen", null));
}
