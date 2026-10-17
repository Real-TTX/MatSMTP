using MatSmtp.Services.Backup;
using MatSmtp.Storage;

namespace MatSmtp.Tests.Integration;

/// <summary>A test that needs a Samba server: <c>MATSMTP_TEST_SMB</c> is <c>host;share=name;user=name;password=secret</c> (the CI starts one on port 445).</summary>
public sealed class SmbFactAttribute : FactAttribute
{
    public const string Variable = "MATSMTP_TEST_SMB";

    public SmbFactAttribute()
    {
        if (Settings is null)
        {
            Skip = $"{Variable} is not set: no SMB server to talk to.";
        }
    }

    public static (string Host, string Share, string User, string Password)? Settings
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split(';');
            string Part(string key) => parts.Skip(1).Select(p => p.Split('=', 2)).First(p => p[0] == key)[1];
            return (parts[0], Part("share"), Part("user"), Part("password"));
        }
    }
}

public sealed class SmbBackupTests
{
    [SmbFact]
    public async Task A_share_of_a_nas_takes_backups_lists_them_returns_them_and_deletes_them()
    {
        var s = SmbFactAttribute.Settings!.Value;
        var folder = "ms-test-" + Guid.NewGuid().ToString("N")[..8];
        IBackupStore store = new SmbBackupStore(new SmbShare(new SmbOptions(s.Host, s.Share, folder, null, s.User, s.Password)));

        var (ok, message) = await store.CheckAsync();
        Assert.True(ok, message);

        var name = BackupNames.Make("smb", new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc), encrypted: false);
        var content = new byte[3 * 1024 * 1024 + 7];
        Random.Shared.NextBytes(content);
        await store.WriteAsync(name, new MemoryStream(content));

        var listed = await store.ListAsync();
        Assert.Contains(listed, f => f.Name == name && f.Size == content.Length);

        await using (var read = await store.OpenReadAsync(name))
        {
            using var buffer = new MemoryStream();
            await read.CopyToAsync(buffer);
            Assert.Equal(content, buffer.ToArray());
        }

        await store.DeleteAsync(name);
        Assert.DoesNotContain(await store.ListAsync(), f => f.Name == name);
    }

    [SmbFact]
    public async Task A_wrong_password_is_reported_in_words()
    {
        var s = SmbFactAttribute.Settings!.Value;
        IBackupStore store = new SmbBackupStore(new SmbShare(new SmbOptions(s.Host, s.Share, "ms-x", null, s.User, "nicht-das-passwort")));

        var (ok, message) = await store.CheckAsync();
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }
}

