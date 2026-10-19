using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services;

/// <summary>Reads the activity log (<see cref="ActivityLogger"/> writes it).</summary>
public sealed class ActivityService(MsDbContext db, TimeProvider clock)
{
    /// <summary>The list of the activity page: search, period, area, sort and page of the request.</summary>
    public Task<ListResult<ActivityLog>> ListAsync(HttpRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var spec = new ListSpec<ActivityLog>
        {
            Id = "activity",
            Action = "/Admin/Activity",
            Label = "Aktivität",
            Noun = "Einträge",
            SearchPlaceholder = "Aktion, Person oder Inhalt ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(a => EF.Functions.Like(a.Action, like, Like.Escape) || EF.Functions.Like(a.UserName ?? "", like, Like.Escape) || EF.Functions.Like(a.Summary ?? "", like, Like.Escape) || EF.Functions.Like(a.EntityType ?? "", like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<ActivityLog>("zeit", "Zeitraum", [new("heute", "Heute"), new("woche", "Letzte 7 Tage"), new("monat", "Letzte 30 Tage")],
                    (q, v) =>
                    {
                        var from = v switch { "heute" => now.AddDays(-1), "woche" => now.AddDays(-7), _ => now.AddDays(-30) };
                        return q.Where(a => a.OccurredDate >= from);
                    }),
                new FilterSpec<ActivityLog>("art", "Bereich", [new("login", "Anmeldung"), new("user", "Benutzer"), new("work", "Regeln, Konten, Zugänge, Webhooks, Nachrichten"), new("share", "Freigaben"), new("backup", "Sicherung")],
                    (q, v) => v switch
                    {
                        "login" => q.Where(a => a.Action.StartsWith("Anmeldung") || a.Action.StartsWith("Abmeldung") || a.Action.Contains("Zwei-Faktor") || a.Action.Contains("Passwort")),
                        "user" => q.Where(a => a.EntityType == "User"),
                        "work" => q.Where(a => a.EntityType == "Rule" || a.EntityType == "SmtpAccount" || a.EntityType == "RelayUser" || a.EntityType == "Webhook" || a.EntityType == "Message" || a.EntityType == "Settings"),
                        "share" => q.Where(a => a.EntityType == "ShareLink" || a.Action.StartsWith("Freigabe")),
                        _ => q.Where(a => a.EntityType != null && a.EntityType.StartsWith("Backup")),
                    }),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ActivityLog>, bool, IOrderedQueryable<ActivityLog>>)>
            {
                ["date"] = ("Zeit", (q, d) => d ? q.OrderByDescending(a => a.OccurredDate).ThenByDescending(a => a.Id) : q.OrderBy(a => a.OccurredDate).ThenBy(a => a.Id)),
                ["user"] = ("Person", (q, d) => d ? q.OrderByDescending(a => a.UserName) : q.OrderBy(a => a.UserName)),
                ["action"] = ("Aktion", (q, d) => d ? q.OrderByDescending(a => a.Action) : q.OrderBy(a => a.Action)),
            },
            DefaultSort = "date",
            DefaultDescending = true,
            PageSize = 30,
        };

        return ListBuilder.RunAsync(db.ActivityLog.AsNoTracking(), request, spec, ct);
    }
}
