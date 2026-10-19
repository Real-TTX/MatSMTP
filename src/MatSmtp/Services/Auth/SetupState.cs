using MatSmtp.Configuration;

namespace MatSmtp.Services.Auth;

/// <summary>
/// Whether the first start is done: an administrator exists, or the management API said so (<see cref="SecurityConfig.SetupCompleted"/>, an
/// installation whose people come through MatIdentity). The question is asked on every request until the answer is yes, then never again.
/// </summary>
public sealed class SetupState(ConfigStore config)
{
    private volatile bool _done;

    public async Task<bool> IsDoneAsync(IServiceProvider services, CancellationToken ct = default)
    {
        if (_done)
        {
            return true;
        }

        _done = config.Current.Security.SetupCompleted || await services.GetRequiredService<UserService>().AnyAdminAsync(ct);
        return _done;
    }

    public void MarkDone() => _done = true;

    /// <summary>Marks the first start as done (or not) in the configuration: <c>PUT /api/v1/settings</c>.</summary>
    public void SetCompleted(bool completed)
    {
        config.Update(c => c.Security.SetupCompleted = completed);

        // Without the mark an administrator still counts: the next request asks again.
        _done = completed;
    }
}
