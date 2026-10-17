using System.Security.Cryptography;
using System.Text;

namespace MatSmtp.Core.Security;

/// <summary>Time-based one-time passwords (RFC 6238, HMAC-SHA1, 30 seconds, six digits): what an authenticator app computes.</summary>
public static class Totp
{
    public const int StepSeconds = 30;

    public const int Digits = 6;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new random secret (160 bits) as Base32, the form the apps expect.</summary>
    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static long StepOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(string base32Secret, long step)
    {
        var key = Base32Decode(base32Secret);
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var code = binary % (int)Math.Pow(10, Digits);
        return code.ToString(new string('0', Digits));
    }

    /// <summary>
    /// Checks a code against the current step and the one before and after (the clock of a phone is rarely exact). Returns the step that matched,
    /// or null. A step at or below <paramref name="lastUsedStep"/> is refused: a code works once.
    /// </summary>
    public static long? Verify(string base32Secret, string? input, DateTimeOffset now, long lastUsedStep)
    {
        var digits = new string((input ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length != Digits)
        {
            return null;
        }

        var current = StepOf(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step <= lastUsedStep)
            {
                continue;
            }

            var expected = Encoding.ASCII.GetBytes(Code(base32Secret, step));
            if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(digits)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>The address an authenticator app reads from the QR code.</summary>
    public static string Uri(string issuer, string account, string base32Secret)
        => $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}"
           + $"?secret={base32Secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static byte[] Base32Decode(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant().Where(c => c != '=' && !char.IsWhiteSpace(c) && c != '-'))
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException($"'{c}' is not a Base32 character.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
