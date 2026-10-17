using System.Net;
using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace MatSmtp.Storage;

/// <summary>A problem with the storage, with a message that says what to do (shown on the page).</summary>
public sealed class StorageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where and how to reach a share. The password is in the clear here (the stored one is unprotected by the caller).</summary>
public sealed record SmbOptions(string Host, string Share, string Folder, string? Domain, string? UserName, string? Password);

public sealed record RemoteFile(string Name, long Size, DateTime ModifiedUtc);

public sealed record ShareCheck(bool Ok, string Message, long? FreeBytes);

/// <summary>
/// A folder on an SMB share, reached by the program itself over SMB 2/3 (port 445, SMBLibrary): no mount and no extra rights in the container.
/// A connection is made per operation. A file is written under a name ending in <c>.partial</c> and renamed when it is complete, so a file that is
/// not whole never has its real name.
/// </summary>
public sealed class SmbShare(SmbOptions options)
{
    private const int SmbPort = 445;
    private const int ResponseTimeoutMilliseconds = 60_000;
    private const int MaxChunkBytes = 1024 * 1024;

    public Task<ShareCheck> CheckAsync(CancellationToken ct = default) => Task.Run(
        () =>
        {
            try
            {
                using var session = Connect();
                EnsureFolder(session, string.Empty);

                var probe = Remote(".ms-probe-" + Guid.NewGuid().ToString("N")[..8]);
                CheckStatus(session.Store.CreateFile(out var handle, out _, probe, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null), "eine Testdatei schreiben");
                try
                {
                    session.Store.WriteFile(out _, handle, 0, "probe"u8.ToArray());
                    session.Store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
                }
                finally
                {
                    session.CloseQuietly(handle);
                }

                return new ShareCheck(true, $"Verbunden mit {options.Host}, Freigabe „{options.Share}“, Ordner „{FolderPath()}“: Dateien können geschrieben werden.", FreeSpace(session));
            }
            catch (StorageException ex)
            {
                return new ShareCheck(false, ex.Message, null);
            }
        },
        ct);

    /// <summary>The shares of a server (without the administrative ones), for the choice in a form.</summary>
    public static Task<IReadOnlyList<string>> ListSharesAsync(string host, string? domain, string? userName, string? password, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<string>>(
            () =>
            {
                var client = Login(new SmbOptions(host, string.Empty, string.Empty, domain, userName, password));
                try
                {
                    var shares = client.ListShares(out var status);
                    if (status != NTStatus.STATUS_SUCCESS || shares is null)
                    {
                        throw new StorageException($"Die Freigaben von {host} können nicht aufgelistet werden: {Describe(status)}.");
                    }

                    return shares.Where(s => !s.EndsWith('$')).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                }
                finally
                {
                    Close(client);
                }
            },
            ct);

    /// <summary>The files directly in a folder (relative to the configured folder).</summary>
    public Task<IReadOnlyList<RemoteFile>> ListAsync(string subFolder, CancellationToken ct = default) => Task.Run<IReadOnlyList<RemoteFile>>(
        () =>
        {
            using var session = Connect();
            var folder = RemoteFolder(subFolder);
            var opened = session.Store.CreateFile(out var handle, out _, folder, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            if (opened is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
            {
                return [];
            }

            CheckStatus(opened, "den Ordner öffnen");
            try
            {
                var status = session.Store.QueryDirectory(out var entries, handle, "*", FileInformationClass.FileDirectoryInformation);
                if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_NO_MORE_FILES))
                {
                    CheckStatus(status, "den Ordner auflisten");
                }

                var files = new List<RemoteFile>();
                foreach (var entry in (entries ?? []).OfType<FileDirectoryInformation>())
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.FileName is "." or ".." || entry.FileAttributes.HasFlag(FileAttributes.Directory) || entry.FileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    files.Add(new RemoteFile(entry.FileName, entry.EndOfFile, DateTime.SpecifyKind(entry.LastWriteTime, DateTimeKind.Utc)));
                }

                return files;
            }
            finally
            {
                session.CloseQuietly(handle);
            }
        },
        ct);

    /// <summary>Writes a stream to <paramref name="relativePath"/> (below the configured folder); folders on the way are made.</summary>
    public Task WriteAsync(string relativePath, Stream content, CancellationToken ct = default) => Task.Run(
        () =>
        {
            using var session = Connect();
            var parts = SplitPath(relativePath);
            var directory = string.Join('\\', parts.Take(parts.Count - 1));
            EnsureFolder(session, directory);

            var final = Remote(string.Join('\\', parts));
            var partial = final + ".partial";
            CheckStatus(
                session.Store.CreateFile(out var handle, out _, partial, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null),
                "die Datei anlegen");

            var complete = false;
            try
            {
                var chunk = (int)Math.Clamp(session.Client.MaxWriteSize, 4096, MaxChunkBytes);
                var buffer = new byte[chunk];
                long offset = 0;
                int read;
                while ((read = content.Read(buffer, 0, chunk)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    offset = Write(session, handle, offset, buffer, read);
                }

                CheckStatus(session.Store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = final, ReplaceIfExists = true }), "die Datei benennen");
                complete = true;
            }
            finally
            {
                if (!complete)
                {
                    try
                    {
                        session.Store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
                    }
                    catch (Exception)
                    {
                        // the connection is gone with the handle: the share removes what is not closed
                    }
                }

                session.CloseQuietly(handle);
            }
        },
        ct);

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default) => Task.Run<Stream>(
        () =>
        {
            var session = Connect();
            try
            {
                var opened = session.Store.CreateFile(out var handle, out _, Remote(string.Join('\\', SplitPath(relativePath))), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                if (opened is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
                {
                    throw new StorageException($"„{relativePath}“ liegt nicht auf der Freigabe.");
                }

                CheckStatus(opened, "die Datei öffnen");
                session.Store.GetFileInformation(out var info, handle, FileInformationClass.FileStandardInformation);
                var length = info is FileStandardInformation standard ? standard.EndOfFile : 0;
                return new SmbReadStream(session, handle, length, (int)Math.Clamp(session.Client.MaxReadSize, 4096, MaxChunkBytes));
            }
            catch
            {
                session.Dispose();
                throw;
            }
        },
        ct);

    public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.Run(
        () =>
        {
            using var session = Connect();
            var status = DeleteExisting(session, Remote(string.Join('\\', SplitPath(relativePath))));
            if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND))
            {
                CheckStatus(status, "die Datei löschen");
            }
        },
        ct);

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default) => Task.Run(
        () =>
        {
            using var session = Connect();
            return Exists(session, Remote(string.Join('\\', SplitPath(relativePath))));
        },
        ct);

    // ------------------------------------------------------------------------------------------ helpers

    private static NTStatus DeleteExisting(Session session, string remote)
    {
        var opened = session.Store.CreateFile(out var handle, out _, remote, AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (opened != NTStatus.STATUS_SUCCESS)
        {
            return opened;
        }

        try
        {
            return session.Store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
        }
        finally
        {
            session.CloseQuietly(handle);
        }
    }

    /// <summary>Writes the bytes at the offset; a server may take less than it is given, so it goes on until all is written.</summary>
    private static long Write(Session session, object handle, long offset, byte[] buffer, int count)
    {
        var written = 0;
        while (written < count)
        {
            var slice = new byte[count - written];
            Buffer.BlockCopy(buffer, written, slice, 0, slice.Length);
            CheckStatus(session.Store.WriteFile(out var bytes, handle, offset + written, slice), "auf die Freigabe schreiben");
            if (bytes <= 0)
            {
                throw new StorageException("Die Freigabe nimmt keine Daten an.");
            }

            written += bytes;
        }

        return offset + count;
    }

    private sealed class SmbReadStream(Session session, object handle, long length, int chunk) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0 || count == 0)
            {
                return 0;
            }

            var wanted = (int)Math.Min(Math.Min(count, chunk), remaining);
            var status = session.Store.ReadFile(out var data, handle, _position, wanted);
            if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
            {
                return 0;
            }

            CheckStatus(status, "von der Freigabe lesen");
            Buffer.BlockCopy(data, 0, buffer, offset, data.Length);
            _position += data.Length;
            return data.Length;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                session.CloseQuietly(handle);
                session.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static List<string> SplitPath(string path)
    {
        var parts = path.Split('/', '\\').Where(s => s.Length > 0).ToList();
        if (parts.Count == 0 || parts.Any(p => p is "." or ".."))
        {
            throw new StorageException("Ungültiger Pfad.");
        }

        return parts;
    }

    private string FolderPath() => string.Join('\\', options.Folder.Split('/', '\\').Where(s => s.Length > 0));

    private string RemoteFolder(string sub)
    {
        var folder = FolderPath();
        var subPath = string.Join('\\', sub.Split('/', '\\').Where(s => s.Length > 0));
        return string.Join('\\', new[] { folder, subPath }.Where(s => s.Length > 0));
    }

    private string Remote(string name)
    {
        var folder = FolderPath();
        return folder.Length == 0 ? name : folder + "\\" + name;
    }

    private void EnsureFolder(Session session, string subFolder)
    {
        var current = string.Empty;
        foreach (var segment in RemoteFolder(subFolder).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new StorageException("Der Ordner darf kein „.“ oder „..“ enthalten.");
            }

            current = current.Length == 0 ? segment : current + "\\" + segment;
            if (Exists(session, current))
            {
                continue;
            }

            CheckStatus(session.Store.CreateFile(out var handle, out _, current, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Directory, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, null), $"den Ordner „{current}“ anlegen");
            session.CloseQuietly(handle);
        }
    }

    private static bool Exists(Session session, string remote)
    {
        var status = session.Store.CreateFile(out var handle, out _, remote, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            return false;
        }

        session.CloseQuietly(handle);
        return true;
    }

    private static long? FreeSpace(Session session)
    {
        var status = session.Store.GetFileSystemInformation(out var info, FileSystemInformationClass.FileFsFullSizeInformation);
        if (status != NTStatus.STATUS_SUCCESS || info is not FileFsFullSizeInformation size)
        {
            return null;
        }

        return size.CallerAvailableAllocationUnits * size.SectorsPerAllocationUnit * size.BytesPerSector;
    }

    // ------------------------------------------------------------------------------------------ connection

    private sealed class Session(SMB2Client client, ISMBFileStore store) : IDisposable
    {
        public SMB2Client Client { get; } = client;

        public ISMBFileStore Store { get; } = store;

        public void CloseQuietly(object handle)
        {
            try
            {
                Store.CloseFile(handle);
            }
            catch (Exception)
            {
                // the connection is gone, so is the handle
            }
        }

        public void Dispose()
        {
            try
            {
                Store.Disconnect();
            }
            catch (Exception)
            {
                // the connection is at its end anyway
            }

            Close(Client);
        }
    }

    private Session Connect()
    {
        if (string.IsNullOrWhiteSpace(options.Share))
        {
            throw new StorageException("Es fehlt die Freigabe.");
        }

        var client = Login(options);
        var store = client.TreeConnect(options.Share, out var status);
        if (status != NTStatus.STATUS_SUCCESS || store is null)
        {
            Close(client);
            throw new StorageException($"Die Freigabe „{options.Share}“ auf {options.Host} lässt sich nicht öffnen: {Describe(status)}.");
        }

        return new Session(client, store);
    }

    private static SMB2Client Login(SmbOptions target)
    {
        if (string.IsNullOrWhiteSpace(target.Host))
        {
            throw new StorageException("Es fehlt der Server.");
        }

        IPAddress address;
        try
        {
            address = IPAddress.TryParse(target.Host, out var parsed)
                ? parsed
                : Dns.GetHostAddresses(target.Host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                  ?? throw new StorageException($"Der Server {target.Host} ist nicht bekannt.");
        }
        catch (SocketException ex)
        {
            throw new StorageException($"Der Server {target.Host} ist nicht bekannt: {ex.Message}", ex);
        }

        var client = new SMB2Client(ResponseTimeoutMilliseconds);
        bool connected;
        try
        {
            connected = client.Connect(address, SMBTransportType.DirectTCPTransport);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
            throw new StorageException($"Keine Verbindung zu {target.Host} (SMB, Port 445): {ex.Message}", ex);
        }

        if (!connected)
        {
            throw new StorageException(PortIsOpen(address)
                ? $"{target.Host} antwortet auf Port 445, aber es wurde keine SMB-Version vereinbart: Unterstützt werden SMB 2.0.2 bis 3.0.2. Ein Server, der nur SMB 1 oder nur SMB 3.1.1 anbietet, geht nicht."
                : $"Keine Verbindung zu {target.Host} (SMB, Port 445): Ist der Server an und der Port offen?");
        }

        var login = client.Login(target.Domain ?? string.Empty, target.UserName ?? string.Empty, target.Password ?? string.Empty);
        if (login != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new StorageException($"Die Anmeldung bei {target.Host} ist fehlgeschlagen: {Describe(login)}.");
        }

        return client;
    }

    private static bool PortIsOpen(IPAddress address)
    {
        try
        {
            using var probe = new TcpClient(address.AddressFamily);
            return probe.ConnectAsync(address, SmbPort).Wait(TimeSpan.FromSeconds(3)) && probe.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Close(SMB2Client client)
    {
        try
        {
            client.Logoff();
        }
        catch (Exception)
        {
            // the connection is at its end anyway
        }

        try
        {
            client.Disconnect();
        }
        catch (Exception)
        {
            // dito
        }
    }

    private static void CheckStatus(NTStatus status, string doing)
    {
        if (status is NTStatus.STATUS_SUCCESS or NTStatus.STATUS_END_OF_FILE)
        {
            return;
        }

        throw new StorageException($"Die Freigabe konnte nicht: {doing}: {Describe(status)}.");
    }

    private static string Describe(NTStatus status) => status switch
    {
        NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_WRONG_PASSWORD => "der Benutzername oder das Passwort stimmt nicht",
        NTStatus.STATUS_ACCOUNT_DISABLED => "das Konto ist deaktiviert",
        NTStatus.STATUS_ACCOUNT_LOCKED_OUT => "das Konto ist gesperrt",
        NTStatus.STATUS_PASSWORD_EXPIRED => "das Passwort ist abgelaufen",
        NTStatus.STATUS_ACCESS_DENIED => "Zugriff verweigert: Der Benutzer darf das dort nicht",
        NTStatus.STATUS_BAD_NETWORK_NAME => "es gibt keine Freigabe mit diesem Namen",
        NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE => "nicht gefunden",
        NTStatus.STATUS_OBJECT_NAME_COLLISION => "es gibt sie schon",
        NTStatus.STATUS_DISK_FULL => "die Freigabe ist voll",
        NTStatus.STATUS_SHARING_VIOLATION => "die Datei ist in Benutzung",
        NTStatus.STATUS_NOT_SUPPORTED => "der Server unterstützt das nicht",
        _ => status.ToString(),
    };
}
