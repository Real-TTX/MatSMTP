using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Backup;

/// <summary>A target for a select: its id and its name.</summary>
public sealed record BackupTargetChoice(long Id, string Name);

/// <summary>What the overview of the backup shows: the plans, how many targets, the last run and one page of the runs.</summary>
public sealed record BackupOverview(IReadOnlyList<BackupPlan> Plans, int TargetCount, BackupRun? LastRun, IReadOnlyList<BackupRun> Runs, int RunCount, int Page);

/// <summary>
/// The plans, targets and runs of the backup as the pages read and write them: the lists, one record, saving and deleting, and starting a plan by hand.
/// The work of a backup itself is <see cref="BackupService"/>.
/// </summary>
public sealed class BackupAdminService(MsDbContext db, IServiceScopeFactory scopes, TimeProvider clock)
{
    /// <summary>The scope of a plan in the words of the list.</summary>
    public static string ScopeLabel(BackupScope s) => s == BackupScope.SystemOnly ? "Nur das System" : "System und Dateien";

    // ------------------------------------------------------------------------------------------------ overview

    /// <summary>The plans, the number of targets, the last run and page <paramref name="page"/> of the runs (<paramref name="pageSize"/> each, newest first).</summary>
    public async Task<BackupOverview> OverviewAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var plans = await db.BackupPlan.AsNoTracking().Include(p => p.Target).OrderBy(p => p.Name).ToListAsync(ct);
        var targets = await db.BackupTarget.CountAsync(ct);
        var last = await db.BackupRun.AsNoTracking().OrderByDescending(r => r.StartDate).FirstOrDefaultAsync(ct);
        var total = await db.BackupRun.CountAsync(ct);
        page = Math.Min(Math.Max(1, page), Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var runs = await db.BackupRun.AsNoTracking().OrderByDescending(r => r.StartDate).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new BackupOverview(plans, targets, last, runs, total, page);
    }

    // ------------------------------------------------------------------------------------------------ plans

    /// <summary>The list of plans: search, filter, sort and page of the request.</summary>
    public Task<ListResult<BackupPlan>> ListPlansAsync(HttpRequest request, CancellationToken ct)
    {
        var spec = new ListSpec<BackupPlan>
        {
            Id = "plans",
            Action = "/Admin/Backup/Plans",
            Label = "Pläne",
            Noun = "Pläne",
            SearchPlaceholder = "Name ...",
            Search = (q, text) => q.Where(p => EF.Functions.Like(p.Name, Like.Contains(text), Like.Escape)),
            Filters =
            [
                new FilterSpec<BackupPlan>("umfang", "Umfang", ListBuilder.EnumOptions<BackupScope>(ScopeLabel), (q, v) => q.Where(p => p.Scope == Enum.Parse<BackupScope>(v))),
                new FilterSpec<BackupPlan>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Ausgeschaltet")], (q, v) => v == "aus" ? q.Where(p => !p.IsEnabled) : q.Where(p => p.IsEnabled)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<BackupPlan>, bool, IOrderedQueryable<BackupPlan>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name)),
                ["next"] = ("Nächste Sicherung", (q, d) => d ? q.OrderByDescending(p => p.NextRunDate) : q.OrderBy(p => p.NextRunDate)),
            },
            DefaultSort = "name",
        };

        return ListBuilder.RunAsync(db.BackupPlan.AsNoTracking().Include(p => p.Target), request, spec, ct);
    }

    public Task<BackupPlan?> FindPlanAsync(long id, CancellationToken ct = default) => db.BackupPlan.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>Creates (<paramref name="id"/> null) or changes a plan with <paramref name="apply"/> and computes its next run.</summary>
    public async Task<BackupPlan> SavePlanAsync(long? id, Action<BackupPlan> apply, CancellationToken ct = default)
    {
        var plan = id is { } existing ? await db.BackupPlan.FirstAsync(p => p.Id == existing, ct) : db.BackupPlan.Add(new BackupPlan()).Entity;
        apply(plan);
        plan.NextRunDate = BackupSchedule.Next(plan, clock.GetUtcNow().UtcDateTime, Fmt.Zone);
        await db.SaveChangesAsync(ct);
        return plan;
    }

    /// <summary>Deletes a plan; the backups at the target stay. Null when there is none.</summary>
    public async Task<BackupPlan?> DeletePlanAsync(long id, CancellationToken ct = default)
    {
        var plan = await db.BackupPlan.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null)
        {
            return null;
        }

        db.BackupPlan.Remove(plan);
        await db.SaveChangesAsync(ct);
        return plan;
    }

    /// <summary>
    /// Starts the plan now in the background with a scope of its own (a backup of many files takes a while; the request does not wait). False when there is
    /// no such plan.
    /// </summary>
    public async Task<bool> StartPlanAsync(long id, CancellationToken ct = default)
    {
        if (!await db.BackupPlan.AnyAsync(p => p.Id == id, ct))
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            var scoped = scope.ServiceProvider;
            var plan = await scoped.GetRequiredService<MsDbContext>().BackupPlan.Include(x => x.Target).FirstAsync(x => x.Id == id);
            try
            {
                await scoped.GetRequiredService<BackupService>().RunPlanAsync(plan, manual: true);
            }
            catch (BackupException)
            {
                // somebody else started a backup in the meantime
            }
        }, CancellationToken.None);
        return true;
    }

    // ------------------------------------------------------------------------------------------------ targets

    /// <summary>The list of targets: search, sort and page of the request.</summary>
    public Task<ListResult<BackupTarget>> ListTargetsAsync(HttpRequest request, CancellationToken ct)
    {
        var spec = new ListSpec<BackupTarget>
        {
            Id = "targets",
            Action = "/Admin/Backup/Targets",
            Label = "Ziele",
            Noun = "Ziele",
            SearchPlaceholder = "Name oder Ort ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(t => EF.Functions.Like(t.Name, like, Like.Escape) || EF.Functions.Like(t.Path, like, Like.Escape) || EF.Functions.Like(t.Host ?? "", like, Like.Escape));
            },
            Sorts = new Dictionary<string, (string, Func<IQueryable<BackupTarget>, bool, IOrderedQueryable<BackupTarget>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(t => t.Name) : q.OrderBy(t => t.Name)),
            },
            DefaultSort = "name",
        };

        return ListBuilder.RunAsync(db.BackupTarget.AsNoTracking(), request, spec, ct);
    }

    /// <summary>The targets for a select, by name.</summary>
    public Task<List<BackupTargetChoice>> TargetChoicesAsync(CancellationToken ct = default)
        => db.BackupTarget.AsNoTracking().OrderBy(t => t.Name).Select(t => new BackupTargetChoice(t.Id, t.Name)).ToListAsync(ct);

    public Task<BackupTarget?> FindTargetAsync(long id, CancellationToken ct = default) => db.BackupTarget.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>Creates (<paramref name="id"/> null) or changes a target with <paramref name="apply"/>.</summary>
    public async Task<BackupTarget> SaveTargetAsync(long? id, Action<BackupTarget> apply, CancellationToken ct = default)
    {
        var target = id is { } existing ? await db.BackupTarget.FirstAsync(t => t.Id == existing, ct) : db.BackupTarget.Add(new BackupTarget()).Entity;
        apply(target);
        await db.SaveChangesAsync(ct);
        return target;
    }

    /// <summary>Whether a plan writes to this target (then it cannot be deleted).</summary>
    public Task<bool> TargetInUseAsync(long id, CancellationToken ct = default) => db.BackupPlan.AnyAsync(p => p.TargetId == id, ct);

    /// <summary>Deletes a target; the backups there stay. Null when there is none.</summary>
    public async Task<BackupTarget?> DeleteTargetAsync(long id, CancellationToken ct = default)
    {
        var target = await db.BackupTarget.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (target is null)
        {
            return null;
        }

        db.BackupTarget.Remove(target);
        await db.SaveChangesAsync(ct);
        return target;
    }
}
