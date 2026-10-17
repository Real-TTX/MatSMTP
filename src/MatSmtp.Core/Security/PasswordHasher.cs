using System.Security.Cryptography;

namespace MatSmtp.Core.Security;

/// <summary>PBKDF2-SHA512 with a random salt. The stored text names the algorithm and the cost, so the cost can be raised later.</summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, KeySize);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True when the hash was made with a lower cost than today's: the caller can store a new one after a successful sign-in.</summary>
    public static bool NeedsRehash(string stored)
        => !(stored.Split('.') is ["v1", var it, _, _] && int.TryParse(it, out var n) && n >= Iterations);

    /// <summary>A hash for one-time secrets that are looked at by their hash only (recovery codes, e-mail codes): fast and salted by the context.</summary>
    public static string HashToken(string value, string context)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(context + "\0" + value.Trim().ToUpperInvariant())));

    /// <summary>A random code of <paramref name="digits"/> digits, without modulo bias.</summary>
    public static string RandomDigits(int digits)
    {
        var chars = new char[digits];
        for (var i = 0; i < digits; i++)
        {
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return new string(chars);
    }

    /// <summary>A random code from letters and digits that cannot be mixed up (no 0/O, 1/I), for recovery codes and app passwords.</summary>
    public static string RandomCode(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }
}
