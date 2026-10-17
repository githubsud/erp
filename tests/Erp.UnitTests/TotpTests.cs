using System.Text;
using Erp.Modules.Core.Auth;

namespace Erp.UnitTests;

public sealed class TotpTests
{
    // RFC 6238 Appendix B test vectors (SHA-1 seed "12345678901234567890"), last 6 digits.
    private static readonly string Secret = Totp.Base32Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Matches_rfc6238_vectors(long unixSeconds, string expected) =>
        Totp.Code(Secret, unixSeconds / Totp.StepSeconds).ShouldBe(expected);

    [Fact]
    public void Base32_round_trips()
    {
        Secret.ShouldBe("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        Encoding.ASCII.GetString(Totp.Base32Decode(Secret)).ShouldBe("12345678901234567890");
    }

    [Fact]
    public void Verification_allows_one_step_of_drift_and_rejects_replays()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.CurrentStep(now);
        Totp.Verify(Secret, Totp.Code(Secret, step), now, 0).ShouldBe(step);
        Totp.Verify(Secret, Totp.Code(Secret, step - 1), now, 0).ShouldBe(step - 1);
        Totp.Verify(Secret, Totp.Code(Secret, step - 2), now, 0).ShouldBeNull();
        Totp.Verify(Secret, Totp.Code(Secret, step), now, lastUsedStep: step).ShouldBeNull();
        Totp.Verify(Secret, "12345", now, 0).ShouldBeNull();
    }

    [Fact]
    public void Provisioning_uri_is_authenticator_compatible() =>
        Totp.ProvisioningUri("ABC", "owner@x.qa", "Qatar SME ERP")
            .ShouldBe("otpauth://totp/Qatar%20SME%20ERP:owner%40x.qa?secret=ABC&issuer=Qatar%20SME%20ERP&digits=6&period=30");
}
