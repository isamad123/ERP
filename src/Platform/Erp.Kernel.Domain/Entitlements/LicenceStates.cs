namespace Erp.Kernel.Domain.Entitlements;

/// <summary>
/// The licence state machine (FR-KRN-ENT-04, spec 01 section 7.3; slice S1.09 rulings Q4, Q6 and B1). The state follows
/// from the end date, the contract's periods and the customer's day, every time it is read, so it is right even when
/// nothing has run. <c>expires_at</c> is the last licensed day; a day ends at midnight in the customer's time zone. With
/// E the end date: Active until E − warning days, Warning for the last warning days up to and including E, Grace for the
/// grace days after E, then Read-only for the read-only days, then Locked. No end date means perpetual: always Active.
/// Read-only always comes before Locked (spec 02 section 20 BR); no state deletes or hides data.
/// </summary>
public static class LicenceStates
{
    public const string Active = "active";
    public const string Warning = "warning";
    public const string Grace = "grace";
    public const string ReadOnly = "read_only";
    public const string Locked = "locked";

    /// <summary>The states in the order the end date moves a licence through them.</summary>
    public static IReadOnlyList<string> All { get; } = [Active, Warning, Grace, ReadOnly, Locked];

    /// <summary>The state on <paramref name="today"/> (the customer's date).</summary>
    public static string Decide(DateOnly? expiresAt, LicenceTerm term, DateOnly today) => At(expiresAt, term, today).State;

    /// <summary>The state on <paramref name="today"/> and the first day of the next state (null when none follows).</summary>
    public static (string State, DateOnly? NextChange, string? NextState) At(DateOnly? expiresAt, LicenceTerm term, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (expiresAt is not { } end)
        {
            return (Active, null, null);
        }

        // First day of each state after Active; a state with zero days starts and ends on the same first day, so it is skipped.
        var starts = new (string State, DateOnly From)[]
        {
            (Warning, end.AddDays(1 - term.WarningDays)),
            (Grace, end.AddDays(1)),
            (ReadOnly, end.AddDays(1 + term.GraceDays)),
            (Locked, end.AddDays(1 + term.GraceDays + term.ReadOnlyDays)),
        };

        var state = Active;
        foreach (var (candidate, from) in starts)
        {
            if (today >= from)
            {
                state = candidate;
            }
        }

        foreach (var (candidate, from) in starts)
        {
            if (from > today && !starts.Any(s => s.State != candidate && s.From == from && Order(s.State) > Order(candidate)))
            {
                return (state, from, candidate);
            }
        }

        return (state, null, null);
    }

    /// <summary>The customer's calendar day at <paramref name="now"/> (midnight in <paramref name="timeZone"/>; ruling Q4).</summary>
    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
    }

    /// <summary>The customer's time zone by IANA id; an unknown id falls back to <paramref name="fallbackId"/> (never fails a request).</summary>
    public static TimeZoneInfo Zone(string? id, string fallbackId = "Asia/Karachi") =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.FindSystemTimeZoneById(fallbackId);

    private static int Order(string state) => All.ToList().IndexOf(state);
}
