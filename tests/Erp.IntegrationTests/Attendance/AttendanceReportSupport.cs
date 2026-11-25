#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Globalization;
using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Employments;
using Erp.IntegrationTests.Policies;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// The company of the S1.30b tests: an HR user (every HR permission, scope all), an HR admin who approves, a pay group with its periods, a place to hire into,
/// the attendance policy (grace 10 minutes) and a way to hire, roster and punch. Dates and times come from the company's day (Asia/Karachi) on the harness clock.
/// </summary>
internal sealed class ReportOrg
{
    public const string Employments = "/api/hrm/employments";
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    private readonly AccessHarness _h;

    private ReportOrg(AccessHarness h, Guid company, Guid hr, Guid approver, Guid payGroup, (Guid OrgUnit, Guid Branch) place)
    {
        _h = h;
        Company = company;
        Hr = hr;
        Approver = approver;
        PayGroup = payGroup;
        Place = place;
    }

    public Guid Company { get; }

    public Guid Hr { get; }

    public Guid Approver { get; }

    public Guid PayGroup { get; }

    public (Guid OrgUnit, Guid Branch) Place { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The company's date on the harness clock.</summary>
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    public TimeOnly Now => TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(_h.Clock.UtcNow, Karachi).DateTime);

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Hhmm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static DateTimeOffset At(DateOnly d, string hhmm) => new(d.ToDateTime(TimeOnly.ParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture)), Karachi.GetUtcOffset(d.ToDateTime(TimeOnly.MinValue)));

    /// <summary>Keeps the test away from the minutes around midnight, where "start plus grace" and "not yet ended" would flip during the test.</summary>
    public static void AvoidMidnight(AccessHarness h)
    {
        ArgumentNullException.ThrowIfNull(h);
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(h.Clock.UtcNow, Karachi).DateTime);
        if (local < new TimeOnly(0, 20) || local > new TimeOnly(23, 50))
        {
            h.Clock.Advance(TimeSpan.FromMinutes(40));
        }
    }

    public static readonly object PolicyParameters = new
    {
        lateGraceMinutes = 10, earlyGraceMinutes = 10, absentBelowMinutes = 240, breakMode = "fixed", breakMinutes = 60, roundingMode = "none",
        shiftWindowMinutes = 120, weeklyOffWork = "record", holidayWork = "record",
    };

    public static async Task<ReportOrg> CreateAsync(AccessHarness h, bool withPolicy = true)
    {
        ArgumentNullException.ThrowIfNull(h);
        var company = await h.CompanyAsync(Ct);
        var hr = await h.UserAsync(Ct);
        var everyHr = await h.ScalarAsync<string[]>("select array_agg(code order by code) from iam.permissions where code like 'hrm.%'", Ct);
        await h.AssignAsync(hr, await h.RoleAsync(Ct, everyHr, [("*", "all")], [("*", "class:salary", "editable")]), null, h.Today.AddDays(-1), null, Ct);
        var approver = await h.UserAsync(Ct, null, "hr_admin");
        var code = "PG" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var group = await h.PostAsync(hr, "/api/hrm/pay-groups", new { companyId = company, code, nameI18n = new { en = "Group " + code, ur = "گروپ " + code }, frequency = "monthly", taxYearStartMonth = 1, cutOffDay = 25, paymentDay = 28 }, Ct);
        Assert.True(group.Status == HttpStatusCode.Created, group.Body);
        var payGroup = group.Json.GetProperty("id").GetGuid();
        var year = TimeZoneInfo.ConvertTime(h.Clock.UtcNow, Karachi).Year;
        foreach (var taxYear in new[] { year - 1, year, year + 1 })
        {
            var generated = await h.PostAsync(hr, $"/api/hrm/pay-groups/{payGroup}/periods/generate", new { taxYear }, Ct);
            Assert.True(generated.Status == HttpStatusCode.Created, generated.Body);
        }

        var org = new ReportOrg(h, company, hr, approver, payGroup, await h.PlaceAsync(company, Ct));
        if (withPolicy)
        {
            var from = org.Today.AddDays(-120);
            var (policy, _) = await h.ActivePolicyAsync(hr, company, "attendance", PolicyParameters, from, Ct);
            var set = await h.ActivateSetAsync(hr, company, "attendance", from, Ct, (policy, 1, [new { attribute = "employee_type", @operator = "in", values = new[] { "permanent" } }]));
            Assert.True(set.Status == HttpStatusCode.OK, set.Body);
        }

        return org;
    }

    /// <summary>A shift of the company from <paramref name="start"/> to <paramref name="end"/> (same day).</summary>
    public async Task<Guid> ShiftAsync(string start, string end)
    {
        var shiftCode = "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var shift = await _h.PostAsync(Hr, "/api/hrm/shifts", new
        {
            companyId = Company, code = shiftCode, nameI18n = new { en = "Shift " + shiftCode, ur = "شفٹ " + shiftCode }, shiftType = "fixed", startTime = start, endTime = end, crossesMidnight = false, minFullDayMinutes = 1,
        }, Ct);
        Assert.True(shift.Status == HttpStatusCode.Created, shift.Body);
        return shift.Json.GetProperty("id").GetGuid();
    }

    /// <summary>A permanent employment (joined 90 days ago) that belongs to a new user holding <paramref name="role"/>, under the given manager.</summary>
    public async Task<(Guid Employment, Guid User)> HireAsync(string role = "employee", Guid? manager = null, (Guid OrgUnit, Guid Branch)? place = null, Guid? company = null)
    {
        var where = place ?? Place;
        var person = (Guid)(await _h.RowAsync("hrm.persons", Guid.Empty, null, Ct))["id"]!;
        var answer = await _h.PostAsync(Hr, Employments, new
        {
            personId = person, companyId = company ?? Company, employeeType = "permanent", joiningDate = Iso(Today.AddDays(-90)),
            assignment = new { orgUnitId = where.OrgUnit, branchId = where.Branch, title = "Officer", payGroupId = PayGroup, managerEmploymentId = manager },
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Body);
        var employment = answer.Json.GetProperty("id").GetGuid();
        var user = await _h.UserAsync(Ct, null, role);
        await _h.ExecAsync("update hrm.employments set user_ref = $1 where id = $2", Ct, user, employment);
        return (employment, user);
    }

    /// <summary>Publishes a roster that makes <paramref name="employment"/> work the shift on the days.</summary>
    public async Task RosterAsync(Guid employment, Guid shift, params DateOnly[] days)
    {
        var created = await _h.PostAsync(Hr, "/api/hrm/rosters", new { companyId = Company, name = "Roster " + Guid.NewGuid().ToString("N")[..6], orgUnitId = Place.OrgUnit, periodStart = Iso(days.Min()), periodEnd = Iso(days.Max()) }, Ct);
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var roster = created.Json.GetProperty("id").GetGuid();
        var changed = await _h.PutAsync(Hr, $"/api/hrm/rosters/{roster}/days", new { changes = days.Select(d => new { employmentId = employment, workDate = Iso(d), dayType = "working", shiftId = shift, reason = (string?)null }).ToArray() }, Ct);
        Assert.True(changed.Status == HttpStatusCode.OK, changed.Body);
        var version = await _h.ScalarAsync<long>("select row_version from hrm.rosters where id = $1", Ct, roster);
        var published = await _h.PostAsync(Hr, $"/api/hrm/rosters/{roster}/publish", new { rowVersion = version }, Ct);
        Assert.True(published.Status == HttpStatusCode.OK, published.Body);
    }

    public async Task PunchAsync(Guid employment, params (DateTimeOffset At, string Direction)[] punches)
    {
        var answer = await _h.PostAsync(Hr, "/api/hrm/attendance/events", new
        {
            events = punches.Select(p => new { employmentId = employment, eventAt = p.At, direction = p.Direction, source = "hr_entry", idempotencyKey = "t:" + Guid.NewGuid().ToString("N") }).ToArray(),
        }, Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
    }

    public async Task ProcessAsync(Guid employment, DateOnly from, DateOnly to)
    {
        var answer = await _h.PostAsync(Hr, $"{Employments}/{employment}/attendance-days/process", new { from = Iso(from), to = Iso(to) }, Ct);
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body);
    }
}
