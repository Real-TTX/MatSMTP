using System.Text;
using MatSmtp.Core.Security;

namespace MatSmtp.Tests.Core;

public sealed class TotpTests
{
    // RFC 6238, appendix B: the secret "12345678901234567890" (SHA-1); the 6 digits are the last six of the 8 digit values of the table.
    private static readonly string Secret = Totp.Base32Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void The_codes_are_the_ones_of_the_standard(long seconds, string expected)
        => Assert.Equal(expected, Totp.Code(Secret, seconds / Totp.StepSeconds));

    [Fact]
    public void A_code_works_once_and_a_neighbouring_step_is_tolerated()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var step = Totp.StepOf(now);
        Assert.Equal(step, Totp.Verify(Secret, "005924", now, 0));
        Assert.Null(Totp.Verify(Secret, "005924", now, step));
        Assert.Equal(step, Totp.Verify(Secret, "005 924", now.AddSeconds(25), 0));
        Assert.Null(Totp.Verify(Secret, "005924", now.AddSeconds(120), 0));
        Assert.Null(Totp.Verify(Secret, "12345", now, 0));
        Assert.Null(Totp.Verify(Secret, null, now, 0));
    }

    [Fact]
    public void A_new_secret_is_base32_and_can_be_read_back()
    {
        var secret = Totp.NewSecret();
        Assert.Matches("^[A-Z2-7]+$", secret);
        Assert.Equal(20, Totp.Base32Decode(secret).Length);
        Assert.StartsWith("otpauth://totp/", Totp.Uri("MatSmtp", "sarah@example.test", secret));
    }
}
