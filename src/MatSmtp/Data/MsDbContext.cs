using System.Linq.Expressions;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Data;

/// <summary>
/// The database of MatSMTP (SQLite, <c>{data}/db/matsmtp.db</c>): tables in PascalCase named like the entity, a bigint <c>Id</c>, the four audit columns filled on save,
/// <c>UpdateState</c> for the records that are deleted softly. Enums are stored as text, so the file stays readable with any SQLite tool.
/// </summary>
public class MsDbContext(DbContextOptions<MsDbContext> options, ICurrentUser currentUser, TimeProvider clock) : DbContext(options)
{
    public DbSet<User> User => Set<User>();

    public DbSet<UserTotp> UserTotp => Set<UserTotp>();

    public DbSet<UserRecoveryCode> UserRecoveryCode => Set<UserRecoveryCode>();

    public DbSet<UserSession> UserSession => Set<UserSession>();

    public DbSet<SmtpAccount> SmtpAccount => Set<SmtpAccount>();

    public DbSet<RelayUser> RelayUser => Set<RelayUser>();

    public DbSet<Webhook> Webhook => Set<Webhook>();

    public DbSet<Rule> Rule => Set<Rule>();

    public DbSet<Message> Message => Set<Message>();

    public DbSet<MessageEvent> MessageEvent => Set<MessageEvent>();

    public DbSet<WebhookDelivery> WebhookDelivery => Set<WebhookDelivery>();

    public DbSet<ShareLink> ShareLink => Set<ShareLink>();

    public DbSet<ActivityLog> ActivityLog => Set<ActivityLog>();

    public DbSet<BackupTarget> BackupTarget => Set<BackupTarget>();

    public DbSet<BackupPlan> BackupPlan => Set<BackupPlan>();

    public DbSet<BackupRun> BackupRun => Set<BackupRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            entityType.SetTableName(entityType.ClrType.Name);

            foreach (var property in entityType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                // UpdateState is a number on purpose (0 = deleted, 1 = created, 2 = updated); DayOfWeek is one too.
                if (type.IsEnum && type != typeof(DayOfWeek) && type != typeof(UpdateState))
                {
                    property.SetProviderClrType(typeof(string));
                }
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                b.Entity(entityType.ClrType).HasQueryFilter(NotDeleted(entityType.ClrType));
            }
        }

        b.Entity<User>().Ignore(x => x.Initials);
        b.Entity<Message>().Ignore(x => x.IsFinal);

        // Names and addresses compare without case, like the sign-in does.
        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).UseCollation("NOCASE");
            e.HasIndex(x => x.Email).IsUnique().HasFilter("\"UpdateState\" <> 0");
            // Also over deleted users: a subject stays with its account, so a deleted account does not come back through MatIdentity.
            e.HasIndex(x => x.ExternalSubject).IsUnique().HasFilter("\"ExternalSubject\" IS NOT NULL");
            e.HasOne(x => x.Totp).WithOne(x => x.User).HasForeignKey<UserTotp>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserTotp>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<UserRecoveryCode>().HasOne(x => x.UserTotp).WithMany(x => x.RecoveryCodes).HasForeignKey(x => x.UserTotpId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserSession>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SmtpAccount>().HasIndex(x => x.Name);
        b.Entity<RelayUser>(e =>
        {
            e.Property(x => x.UserName).UseCollation("NOCASE");
            e.HasIndex(x => x.UserName).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasIndex(x => x.ApiId).IsUnique().HasFilter("\"UpdateState\" <> 0 AND \"ApiId\" IS NOT NULL");
        });
        b.Entity<Webhook>().HasIndex(x => x.Name);
        b.Entity<Rule>(e =>
        {
            e.HasIndex(x => x.Position);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Webhook).WithMany().HasForeignKey(x => x.WebhookId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.MatchRelayUser).WithMany().HasForeignKey(x => x.MatchRelayUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Message>(e =>
        {
            e.HasIndex(x => x.ReceivedDate);
            e.HasIndex(x => new { x.Status, x.NextAttemptDate });
            e.HasIndex(x => new { x.AccountId, x.SentDate });
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<MessageEvent>(e =>
        {
            e.HasIndex(x => new { x.MessageId, x.OccurredDate });
            e.HasOne(x => x.Message).WithMany(x => x.Events).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WebhookDelivery>(e =>
        {
            e.HasIndex(x => new { x.Status, x.NextAttemptDate });
            e.HasOne(x => x.Message).WithMany(x => x.WebhookDeliveries).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Webhook).WithMany().HasForeignKey(x => x.WebhookId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ShareLink>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActivityLog>().HasIndex(x => x.OccurredDate);
        b.Entity<BackupPlan>().HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BackupRun>().HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.SetNull);
    }

    private static LambdaExpression NotDeleted(Type clrType)
    {
        var parameter = Expression.Parameter(clrType, "e");
        var state = Expression.Property(parameter, nameof(ISoftDeletable.UpdateState));
        var body = Expression.NotEqual(state, Expression.Constant(UpdateState.Deleted));
        return Expression.Lambda(body, parameter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns and turns a delete of a soft-deletable record into a state change.</summary>
    private void Stamp()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var userId = currentUser.UserId;

        foreach (var entry in ChangeTracker.Entries<AuditedEntity>().ToList())
        {
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = UpdateState.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreateDate == default)
                    {
                        entry.Entity.CreateDate = now;
                    }

                    entry.Entity.CreateUserId ??= userId;
                    entry.Entity.UpdateDate = entry.Entity.CreateDate;
                    entry.Entity.UpdateUserId = entry.Entity.CreateUserId;
                    if (entry.Entity is ISoftDeletable added && added.UpdateState == UpdateState.Deleted)
                    {
                        added.UpdateState = UpdateState.Created;
                    }

                    break;

                case EntityState.Modified:
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    entry.Property(nameof(AuditedEntity.CreateDate)).IsModified = false;
                    entry.Property(nameof(AuditedEntity.CreateUserId)).IsModified = false;
                    if (entry.Entity is ISoftDeletable modified && modified.UpdateState == UpdateState.Created)
                    {
                        modified.UpdateState = UpdateState.Updated;
                    }

                    break;
            }
        }
    }
}
