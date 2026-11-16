using System.Text;
using Erp.Kernel.Domain.Identity;

namespace Erp.UnitTests.Identity;

/// <summary>Slice S1.05: the pure identity rules (passwords, lockout, statuses, TOTP).</summary>
public sealed class IdentityDomainTests
{
    private static readonly PasswordPolicy Defaults = new(MinLength: 10, MinCharacterKinds: 3, HistoryCount: 5, ExpiryDays: 0, LockoutThreshold: 5, LockoutMinutes: 15);

    [Theory]
    [InlineData("Short1!", new[] { PasswordRules.TooShort })]
    [InlineData("alllowercaseletters", new[] { PasswordRules.TooSimple })]
    [InlineData("lowercase123456", new[] { PasswordRules.TooSimple })]
    [InlineData("Lowercase123456", new string[0])]
    [InlineData("lower case 12345", new string[0])]
    [InlineData("", new[] { PasswordRules.Empty })]
    public void FR_ADM_USR_20_password_length_and_complexity_follow_the_policy(string password, string[] expected) =>
        Assert.Equal(expected, PasswordRules.Check(password, Defaults));

    [Fact]
    public void FR_ADM_USR_20_password_expiry_zero_days_means_never()
    {
        var changed = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.False(PasswordRules.IsExpired(changed, changed.AddYears(5), Defaults));
        Assert.True(PasswordRules.IsExpired(changed, changed.AddDays(90), Defaults with { ExpiryDays = 90 }));
        Assert.False(PasswordRules.IsExpired(changed, changed.AddDays(89), Defaults with { ExpiryDays = 90 }));
    }

    [Fact]
    public void FR_KRN_IAM_01_lockout_starts_at_the_threshold_for_the_configured_minutes()
    {
        var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        Assert.Null(Lockout.AfterFailure(4, now, Defaults));
        Assert.Equal(now.AddMinutes(15), Lockout.AfterFailure(5, now, Defaults));
        Assert.Null(Lockout.AfterFailure(50, now, Defaults with { LockoutThreshold = 0 }));
    }

    [Theory]
    [InlineData("invited", "archived", true)]
    [InlineData("invited", "active", false)] // only through activation
    [InlineData("active", "locked", true)]
    [InlineData("locked", "active", true)]
    [InlineData("active", "disabled", true)]
    [InlineData("locked", "disabled", true)]
    [InlineData("disabled", "active", true)]
    [InlineData("disabled", "archived", true)]
    [InlineData("active", "archived", false)]
    [InlineData("archived", "active", false)]
    [InlineData("archived", "disabled", false)]
    [InlineData("disabled", "locked", false)]
    public void FR_ADM_USR_03_admin_status_transitions_follow_ruling_P2(string from, string to, bool allowed) =>
        Assert.Equal(allowed, UserStatuses.AdminMayChange(from, to));

    [Fact]
    public void FR_ADM_USR_03_activation_is_the_only_way_from_invited_to_active_and_seats_count_active_and_locked()
    {
        Assert.True(UserStatuses.IsAllowed("invited", "active"));
        Assert.True(UserStatuses.RevokesSessions("disabled"));
        Assert.True(UserStatuses.RevokesSessions("archived"));
        Assert.False(UserStatuses.RevokesSessions("active"));
        Assert.True(UserStatuses.CountsAgainstLimit("active"));
        Assert.True(UserStatuses.CountsAgainstLimit("locked"));
        Assert.False(UserStatuses.CountsAgainstLimit("invited"));
        Assert.False(UserStatuses.CountsAgainstLimit("disabled"));
    }

    // RFC 6238 appendix B test vectors (SHA-1, 8 digits; the last 6 digits are the 6-digit code).
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void FR_KRN_IAM_02_totp_matches_the_RFC_6238_test_vectors(long unixSeconds, string expected)
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, Totp.Code(secret, unixSeconds / Totp.StepSeconds, digits: 8));
        Assert.Equal(expected[2..], Totp.Code(secret, unixSeconds / Totp.StepSeconds));
    }

    [Fact]
    public void FR_KRN_IAM_02_totp_accepts_one_step_of_drift_and_refuses_a_replayed_code()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);

        Assert.Equal(step, Totp.Verify(secret, Totp.Code(secret, step), now, lastUsedStep: null));
        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Code(secret, step - 1), now, null));
        Assert.Equal(step + 1, Totp.Verify(secret, Totp.Code(secret, step + 1), now, null));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step - 2), now, null));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step), now, lastUsedStep: step)); // replay
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step - 1), now, lastUsedStep: step - 1));
        Assert.Null(Totp.Verify(secret, "12345", now, null));
        Assert.Null(Totp.Verify(secret, "abcdef", now, null));
    }

    [Fact]
    public void FR_KRN_IAM_02_base32_round_trips_and_the_enrolment_link_has_the_authenticator_parameters()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(secret));
        Assert.Equal(secret, Base32.Decode("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"));
        var uri = Totp.EnrolmentUri("Acme ERP", "hr.admin@acme.example", secret);
        Assert.StartsWith("otpauth://totp/Acme%20ERP:hr.admin%40acme.example?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", uri, StringComparison.Ordinal);
        Assert.Contains("&digits=6&period=30", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void FR_ADM_USR_21_mfa_grace_runs_from_the_later_of_activation_and_the_requirement()
    {
        var activated = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var policy = new MfaPolicy(true, activated.AddDays(3), 7, 10, ["totp"]);
        Assert.Equal(activated.AddDays(10), policy.GraceEndsAt(activated));
        Assert.Equal(activated.AddDays(7), (policy with { RequiredSince = activated.AddDays(-30) }).GraceEndsAt(activated));
    }
}
