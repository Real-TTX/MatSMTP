using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MatSmtp.Services.Backup;

/// <summary>
/// Encrypts a backup with a passphrase. Format: the magic "MBBENC1\n", the salt (16), the number of PBKDF2 iterations (4), a random prefix of the nonces (4),
/// then blocks of up to 1 MiB: [length (4)] [ciphertext] [tag (16)]. Every block is AES-256-GCM with the nonce prefix + the number of the block; the number
/// of the block and whether it is the last one go into the authenticated data, so blocks cannot be reordered, dropped or cut off at the end unnoticed.
/// </summary>
public static class BackupCrypto
{
    private const int BlockSize = 1024 * 1024;
    private const int Iterations = 310_000;
    private const int KeyBytes = 32;
    private const int TagBytes = 16;
    private static readonly byte[] Magic = "MBBENC1\n"u8.ToArray();

    /// <summary>True when the stream starts with the marker of an encrypted backup. The position is restored.</summary>
    public static async Task<bool> IsEncryptedAsync(Stream input, CancellationToken ct = default)
    {
        var start = input.Position;
        var head = new byte[Magic.Length];
        var read = await input.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        input.Position = start;
        return read == Magic.Length && head.AsSpan().SequenceEqual(Magic);
    }

    public static async Task EncryptAsync(Stream input, Stream output, string passphrase, CancellationToken ct = default)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var prefix = RandomNumberGenerator.GetBytes(4);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
        await output.WriteAsync(Magic, ct);
        await output.WriteAsync(salt, ct);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, Iterations);
        prefix.CopyTo(header, 4);
        await output.WriteAsync(header, ct);

        using var aes = new AesGcm(key, TagBytes);
        var current = new byte[BlockSize];
        var next = new byte[BlockSize];
        var currentLength = await input.ReadAtLeastAsync(current, BlockSize, throwOnEndOfStream: false, ct);
        long index = 0;
        while (true)
        {
            var nextLength = currentLength == BlockSize ? await input.ReadAtLeastAsync(next, BlockSize, throwOnEndOfStream: false, ct) : 0;
            var last = nextLength == 0;
            var cipher = new byte[currentLength];
            var tag = new byte[TagBytes];
            aes.Encrypt(Nonce(prefix, index), current.AsSpan(0, currentLength), cipher, tag, Aad(index, last));
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, currentLength | (last ? int.MinValue : 0));
            await output.WriteAsync(length, ct);
            await output.WriteAsync(cipher, ct);
            await output.WriteAsync(tag, ct);
            if (last)
            {
                break;
            }

            (current, next) = (next, current);
            currentLength = nextLength;
            index++;
        }
    }

    /// <summary>Decrypts into <paramref name="output"/>. A wrong passphrase or a damaged file throws <see cref="CryptographicException"/>.</summary>
    public static async Task DecryptAsync(Stream input, Stream output, string passphrase, CancellationToken ct = default)
    {
        var magic = new byte[Magic.Length];
        if (await input.ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, ct) != magic.Length || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new CryptographicException("Das ist keine verschlüsselte Sicherung.");
        }

        var salt = new byte[16];
        var header = new byte[8];
        await input.ReadExactlyAsync(salt, ct);
        await input.ReadExactlyAsync(header, ct);
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (iterations is < 10_000 or > 10_000_000)
        {
            throw new CryptographicException("Die Sicherung ist beschädigt.");
        }

        var prefix = header[4..8];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        using var aes = new AesGcm(key, TagBytes);
        var lengthBytes = new byte[4];
        long index = 0;
        while (true)
        {
            if (await input.ReadAtLeastAsync(lengthBytes, 4, throwOnEndOfStream: false, ct) != 4)
            {
                throw new CryptographicException("Die Sicherung ist unvollständig.");
            }

            var raw = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            var last = raw < 0;
            var length = raw & int.MaxValue;
            if (length > BlockSize)
            {
                throw new CryptographicException("Die Sicherung ist beschädigt.");
            }

            var cipher = new byte[length];
            var tag = new byte[TagBytes];
            await input.ReadExactlyAsync(cipher, ct);
            await input.ReadExactlyAsync(tag, ct);
            var plain = new byte[length];
            try
            {
                aes.Decrypt(Nonce(prefix, index), cipher, tag, plain, Aad(index, last));
            }
            catch (CryptographicException)
            {
                throw new CryptographicException(index == 0 ? "Das Passwort stimmt nicht, oder die Sicherung ist beschädigt." : "Die Sicherung ist beschädigt.");
            }

            await output.WriteAsync(plain, ct);
            if (last)
            {
                return;
            }

            index++;
        }
    }

    private static byte[] Nonce(byte[] prefix, long index)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    private static byte[] Aad(long index, bool last)
    {
        var aad = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(aad, index);
        aad[8] = last ? (byte)1 : (byte)0;
        return aad;
    }
}
