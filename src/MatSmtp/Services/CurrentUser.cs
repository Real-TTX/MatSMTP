using System.Security.Claims;
using MatSmtp.Data.Entities;

namespace MatSmtp.Services;

/// <summary>Who is acting: the signed-in person of the request, or an explicit actor for background work (<see cref="RunAs"/>).</summary>
public interface ICurrentUser
{
    long? UserId { get; }

    string? Name { get; }

    UserRole? Role { get; }

    bool IsAuthenticated { get; }

    bool IsAdmin { get; }

    /// <summary>The address of the client, for the logs; null outside a request.</summary>
    string? IpAddress { get; }

    /// <summary>Acts as somebody else until disposed (background services, tests).</summary>
    IDisposable RunAs(long? userId, string? name, UserRole role = UserRole.Admin);
}

/// <summary>Claim types the application puts into the sign-in cookie.</summary>
public static class AppClaims
{
    public const string Session = "ms:session";

    public const string Theme = "ms:theme";
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private sealed record Actor(long? UserId, string? Name, UserRole Role);

    private static readonly AsyncLocal<Actor?> Override = new();

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public long? UserId
    {
        get
        {
            if (Override.Value is { } actor)
            {
                return actor.UserId;
            }

            var raw = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return long.TryParse(raw, out var id) ? id : null;
        }
    }

    public string? Name => Override.Value is { } actor ? actor.Name : Principal?.Identity?.Name;

    public UserRole? Role
    {
        get
        {
            if (Override.Value is { } actor)
            {
                return actor.Role;
            }

            return Enum.TryParse<UserRole>(Principal?.FindFirstValue(ClaimTypes.Role), out var role) ? role : null;
        }
    }

    public bool IsAuthenticated => Override.Value is not null || Principal?.Identity?.IsAuthenticated == true;

    public bool IsAdmin => Role == UserRole.Admin;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public IDisposable RunAs(long? userId, string? name, UserRole role = UserRole.Admin)
    {
        var previous = Override.Value;
        Override.Value = new Actor(userId, name, role);
        return new Restore(previous);
    }

    private sealed class Restore(Actor? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
