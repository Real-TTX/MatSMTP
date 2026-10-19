using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MatSmtp.Core.Relay;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatSmtp.Pages.Rules;

/// <summary>One rule: its conditions (sender, recipient, network, sign-in, subject), its decision and its changes (rewrite, webhook, blind copy).</summary>
public sealed class EditModel(RuleService rules, AccountService accounts, WebhookAdminService webhooks, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
        [StringLength(100)]
        public string Name { get; set; } = "";

        [StringLength(300)]
        public string? Description { get; set; }

        public bool IsEnabled { get; set; } = true;

        public string? MatchSenders { get; set; }

        public string? MatchRecipients { get; set; }

        public string? MatchNetworks { get; set; }

        public AuthCondition MatchAuth { get; set; } = AuthCondition.Any;

        public long? MatchRelayUserId { get; set; }

        public string? MatchSubject { get; set; }

        public RuleAction Action { get; set; } = RuleAction.Relay;

        public long? AccountId { get; set; }

        public long? WebhookId { get; set; }

        public string? RewriteFrom { get; set; }

        public string? RewriteFromName { get; set; }

        public bool RewriteHeader { get; set; } = true;

        public bool ReplyToOriginal { get; set; }

        public string? AddBcc { get; set; }

        [StringLength(300)]
        public string? RejectMessage { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Rule? Item { get; private set; }

    public List<SelectListItem> Accounts { get; private set; } = [];

    public List<SelectListItem> Webhooks { get; private set; } = [];

    public List<SelectListItem> RelayUsers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long? copy = null)
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var source = Item ?? (copy is { } c ? await rules.FindAsync(c, HttpContext.RequestAborted) : null);
        if (source is { } r)
        {
            Input = new InputModel
            {
                Name = Item is null ? r.Name + " (Kopie)" : r.Name, Description = r.Description, IsEnabled = r.IsEnabled, MatchSenders = r.MatchSenders, MatchRecipients = r.MatchRecipients,
                MatchNetworks = r.MatchNetworks, MatchAuth = r.MatchRelayUserId is null ? r.MatchAuth : AuthCondition.Authenticated, MatchRelayUserId = r.MatchRelayUserId, MatchSubject = r.MatchSubject,
                Action = r.Action, AccountId = r.AccountId, WebhookId = r.WebhookId, RewriteFrom = r.RewriteFrom, RewriteFromName = r.RewriteFromName, RewriteHeader = r.RewriteHeader,
                ReplyToOriginal = r.ReplyToOriginal, AddBcc = r.AddBcc, RejectMessage = r.RejectMessage,
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Check("Input.MatchSenders", AddressPattern.Problem(Input.MatchSenders));
        Check("Input.MatchRecipients", AddressPattern.Problem(Input.MatchRecipients));
        Check("Input.MatchNetworks", IpNetworks.Problem(Input.MatchNetworks));
        Check("Input.MatchSubject", AddressPattern.Problem(Input.MatchSubject));
        Check("Input.AddBcc", AddressPattern.Split(Input.AddBcc).Where(a => !new EmailAddressAttribute().IsValid(a)).Select(a => $"„{a}“ ist keine E-Mail-Adresse.").FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(Input.RewriteFrom))
        {
            var sample = SenderRewrite.Apply(Input.RewriteFrom, "app@example.com");
            Check("Input.RewriteFrom", new EmailAddressAttribute().IsValid(sample) ? null : $"Daraus wird „{sample}“, keine E-Mail-Adresse. Beispiele: noreply@firma.de, {{local}}@firma.de, firma.de");
        }

        if (Input.Action == RuleAction.WebhookOnly && Input.WebhookId is null)
        {
            ModelState.AddModelError("Input.WebhookId", "„Nur an Webhook“ braucht einen Webhook.");
        }

        if (Input.Action == RuleAction.Continue && string.IsNullOrWhiteSpace(Input.RewriteFrom) && string.IsNullOrWhiteSpace(Input.RewriteFromName) && Input.WebhookId is null && string.IsNullOrWhiteSpace(Input.AddBcc))
        {
            ModelState.AddModelError("Input.Action", "Eine Regel, die nur weiterprüft, sollte etwas ändern: den Absender umschreiben, einen Webhook aufrufen oder eine Blindkopie schicken.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var rule = await rules.SaveAsync(Item?.Id, Apply, HttpContext.RequestAborted);
        await log.LogAsync(Item is null ? "Regel angelegt" : "Regel geändert", nameof(Rule), rule.Id, rule.Name);
        Flash("Die Regel ist gespeichert.");
        return RedirectToPage("/Rules/Index");
    }

    private void Apply(Rule rule)
    {
        rule.Name = Input.Name.Trim();
        rule.Description = Clean(Input.Description);
        rule.IsEnabled = Input.IsEnabled;
        rule.MatchSenders = CleanList(Input.MatchSenders);
        rule.MatchRecipients = CleanList(Input.MatchRecipients);
        rule.MatchNetworks = Clean(Input.MatchNetworks);
        rule.MatchAuth = Input.MatchAuth;
        rule.MatchRelayUserId = Input.MatchAuth == AuthCondition.Authenticated ? Input.MatchRelayUserId : null;
        rule.MatchSubject = CleanList(Input.MatchSubject);
        rule.Action = Input.Action;
        var changes = Input.Action is not RuleAction.Reject and not RuleAction.Drop;
        rule.AccountId = Input.Action == RuleAction.Relay ? Input.AccountId : null;
        rule.WebhookId = changes ? Input.WebhookId : null;
        rule.RewriteFrom = changes ? Clean(Input.RewriteFrom) : null;
        rule.RewriteFromName = changes ? Clean(Input.RewriteFromName) : null;
        rule.RewriteHeader = Input.RewriteHeader;
        rule.ReplyToOriginal = Input.ReplyToOriginal;
        rule.AddBcc = Input.Action is RuleAction.Relay or RuleAction.Continue ? CleanList(Input.AddBcc) : null;
        rule.RejectMessage = Input.Action == RuleAction.Reject ? Clean(Input.RejectMessage) : null;
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var rule = Id is { } id ? await rules.DeleteAsync(id, HttpContext.RequestAborted) : null;
        if (rule is null)
        {
            return RedirectToPage("/Rules/Index");
        }

        await log.LogAsync("Regel gelöscht", nameof(Rule), rule.Id, rule.Name);
        Flash("Die Regel ist gelöscht.");
        return RedirectToPage("/Rules/Index");
    }

    private void Check(string key, string? problem)
    {
        if (problem is not null)
        {
            ModelState.AddModelError(key, problem);
        }
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>A list of patterns, one per line.</summary>
    private static string? CleanList(string? text) => string.IsNullOrWhiteSpace(text) ? null : string.Join("\n", AddressPattern.Split(text));

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Item = await rules.FindAsync(id, HttpContext.RequestAborted);
            if (Item is null)
            {
                return false;
            }
        }

        Accounts = [.. (await accounts.AllAsync(HttpContext.RequestAborted))
            .Select(a => new SelectListItem(a.Name + (a.IsDefault ? " (Standard)" : "") + (a.IsEnabled ? "" : " (abgeschaltet)"), a.Id.ToString(CultureInfo.InvariantCulture)))];
        Webhooks = [.. (await webhooks.AllAsync(HttpContext.RequestAborted))
            .Select(w => new SelectListItem(w.Name + (w.IsEnabled ? "" : " (abgeschaltet)"), w.Id.ToString(CultureInfo.InvariantCulture)))];
        RelayUsers = [.. (await rules.RelayUsersAsync(HttpContext.RequestAborted))
            .Select(u => new SelectListItem(u.UserName, u.Id.ToString(CultureInfo.InvariantCulture)))];

        var title = Item?.Name ?? "Neue Regel";
        Head(title, null, ("Relay", null), ("Regeln", "/Rules"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Rules";
        return true;
    }
}
