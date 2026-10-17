namespace MatSmtp.Data;

/// <summary>State of a record that can be deleted "softly": the row stays, the application no longer sees it.</summary>
public enum UpdateState : short
{
    Deleted = 0,
    Created = 1,
    Updated = 2,
}

/// <summary>Every table has a bigint <c>Id</c> and the four audit columns, which <see cref="MsDbContext"/> fills on save.</summary>
public abstract class AuditedEntity
{
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    public long? UpdateUserId { get; set; }
}

/// <summary>
/// A record with <see cref="UpdateState"/>: 0 = deleted, 1 = created, 2 = updated. A delete through the context only sets the state;
/// a global query filter hides deleted rows.
/// </summary>
public interface ISoftDeletable
{
    UpdateState UpdateState { get; set; }
}
