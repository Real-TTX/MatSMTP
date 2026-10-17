using MatSmtp.Data.Entities;
using MatSmtp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatSmtp.Data;

/// <summary>Lets <c>dotnet ef migrations add</c> create the context without starting the application.</summary>
public sealed class MsDbContextFactory : IDesignTimeDbContextFactory<MsDbContext>
{
    public MsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MsDbContext>()
            .UseSqlite("Data Source=matsmtp-design.db")
            .Options;
        return new MsDbContext(options, new DesignTimeUser(), TimeProvider.System);
    }

    private sealed class DesignTimeUser : ICurrentUser
    {
        public long? UserId => null;

        public string? Name => null;

        public UserRole? Role => null;

        public bool IsAuthenticated => false;

        public bool IsAdmin => false;

        public string? IpAddress => null;

        public IDisposable RunAs(long? userId, string? name, UserRole role = UserRole.Admin) => new NoOp();

        private sealed class NoOp : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
