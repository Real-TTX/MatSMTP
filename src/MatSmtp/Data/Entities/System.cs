namespace MatSmtp.Data.Entities;

/// <summary>The operational log: who did what. Shown to administrators, pruned by the maintenance service.</summary>
public class ActivityLog : AuditedEntity
{
    public DateTime OccurredDate { get; set; }

    public long? UserId { get; set; }

    public string? UserName { get; set; }

    public string Action { get; set; } = "";

    public string? EntityType { get; set; }

    public long? EntityId { get; set; }

    public string? Summary { get; set; }

    public string? IpAddress { get; set; }
}

public enum BackupTargetKind
{
    Local,
    Smb,
}

/// <summary>Where backups go: a folder on the server (outside the data volume) or a share on a NAS.</summary>
public class BackupTarget : AuditedEntity
{
    public string Name { get; set; } = "";

    public BackupTargetKind Kind { get; set; } = BackupTargetKind.Smb;

    /// <summary>Local: the folder. SMB: the folder inside the share.</summary>
    public string Path { get; set; } = "";

    public string? Host { get; set; }

    public int Port { get; set; } = 445;

    public string? Share { get; set; }

    public string? Domain { get; set; }

    public string? UserName { get; set; }

    /// <summary>Protected with the key ring of the application.</summary>
    public string? PasswordProtected { get; set; }
}

/// <summary>What a backup holds: the system (database, settings, keys, certificates), or the system and the content of the messages in the spool folder.</summary>
public enum BackupScope
{
    SystemOnly,
    SystemAndFiles,
}

public enum ScheduleKind
{
    Hourly,
    Daily,
    Weekly,
    Monthly,
}

public class BackupPlan : AuditedEntity
{
    public string Name { get; set; } = "";

    public long TargetId { get; set; }

    public BackupTarget? Target { get; set; }

    public BackupScope Scope { get; set; } = BackupScope.SystemOnly;

    public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;

    /// <summary>Hourly: every N hours.</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>Daily, weekly, monthly: the time of day in the time zone of the installation.</summary>
    public TimeOnly TimeOfDay { get; set; } = new(2, 30);

    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Sunday;

    public int DayOfMonth { get; set; } = 1;

    /// <summary>How many of the newest backups of this plan are kept (0 = no limit).</summary>
    public int KeepLast { get; set; } = 7;

    public int KeepDaily { get; set; }

    public int KeepWeekly { get; set; }

    public int KeepMonthly { get; set; }

    /// <summary>Optional: the backup file is encrypted. Protected with the key ring, so that scheduled backups can run.</summary>
    public string? PassphraseProtected { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime? NextRunDate { get; set; }

    public DateTime? LastRunDate { get; set; }

    public string? LastStatus { get; set; }

    public int FailedAttempts { get; set; }
}

public enum BackupRunStatus
{
    Success,
    Failed,
}

public class BackupRun : AuditedEntity
{
    public long? PlanId { get; set; }

    public BackupPlan? Plan { get; set; }

    public string? PlanName { get; set; }

    public string? TargetName { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public BackupRunStatus Status { get; set; }

    public bool IsManual { get; set; }

    public BackupScope Scope { get; set; }

    public string? FileName { get; set; }

    public long SizeBytes { get; set; }

    public string? Message { get; set; }
}
