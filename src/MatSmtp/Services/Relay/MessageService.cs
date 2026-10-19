using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Relay;

/// <summary>The messages of the relay as the pages read them: one message, its history, its webhooks.</summary>
public sealed class MessageService(MsDbContext db)
{
    /// <summary>A message without its history; null when there is none.</summary>
    public Task<Message?> FindAsync(long id, CancellationToken ct = default) => db.Message.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
}
