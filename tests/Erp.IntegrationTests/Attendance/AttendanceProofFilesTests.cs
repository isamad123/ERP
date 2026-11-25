#pragma warning disable CA1861 // literal test data arrays, each used once
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests.Attendance;

/// <summary>
/// Proof files of attendance requests (slice S1.30b, R-161; FR-KRN-DOC-01 and DOC-03, FR-HRM-ATT-11 and ATT-14) through the real attachment API, the real
/// attachment owners of regularization and duty requests, the real workflow engine and a real PostgreSQL: the files follow the request (the person it is
/// for, the person who made it, the approver with an open task, readers in their data scope); another employee and another company cannot read, add or
/// delete them; the request keeps the ids of its files when it is decided; nothing is added to a decided request.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttendanceProofFilesTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Attachments = "/api/doc/attachments";
    private const string RegularizationType = "hrm.regularization_requests";
    private const string DutyType = "hrm.duty_requests";
    private AccessHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
        await ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static byte[] Pdf(string extra = "") => Encoding.UTF8.GetBytes("%PDF-1.7\nA proof of the request. " + extra + "\n");

    private async Task<(HttpStatusCode Status, JsonElement Body)> UploadAsync(Guid user, string type, Guid owner, string extra = "")
    {
        using var client = TenancyFixture.Client(_h.App, TenancyFixture.HostOf(_h.Code), _h.Token(user));
        using var form = new MultipartFormDataContent { { new StringContent(type), "OwnerType" }, { new StringContent(owner.ToString()), "OwnerId" } };
        var file = new ByteArrayContent(Pdf(extra));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "File", "proof-" + extra + ".pdf");
        using var response = await client.PostAsync(new Uri(Attachments, UriKind.Relative), form, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private sealed record Rig(ReportOrg Org, Guid Employment, Guid User, Guid Stranger, Guid Other, DateOnly Day, Guid Exception);

    /// <summary>An employee who forgot to punch out three days ago (an open exception), a colleague with no part in it, and a reader of another company.</summary>
    private async Task<Rig> SetUpAsync()
    {
        var o = await ReportOrg.CreateAsync(_h);
        var shift = await o.ShiftAsync("09:00", "18:00");
        var day = o.Today.AddDays(-3);
        var (employment, user) = await o.HireAsync();
        var (_, stranger) = await o.HireAsync();
        await o.RosterAsync(employment, shift, day);
        await o.PunchAsync(employment, (ReportOrg.At(day, "09:02"), "in"));
        await o.ProcessAsync(employment, day, day);
        var exception = await _h.ScalarAsync<Guid>("select id from hrm.attendance_exceptions where employment_id = $1 and status = 'open'", Ct, employment);

        var elsewhere = await _h.CompanyAsync(Ct);
        var reader = await _h.UserAsync(Ct);
        await _h.AssignAsync(reader, await _h.RoleAsync(Ct, ["hrm.regularization_requests.read", "hrm.duty_requests.read", "hrm.regularization_requests.create", "hrm.duty_requests.create", "hrm.employments.read"], [("*", "all")]), elsewhere, _h.Today.AddDays(-1), null, Ct);
        return new Rig(o, employment, user, stranger, reader, day, exception);
    }

    private async Task<Guid> TaskOfAsync(Guid instance) =>
        await _h.ScalarAsync<Guid>("select id from wf.workflow_tasks where instance_id = $1 and status = 'pending' order by created_at limit 1", Ct, instance);

    private async Task DispatchAsync()
    {
        await using var rig = await JobRig.ForExistingAsync(fx, _h.Code, _h.Clock, Ct);
        var dispatcher = rig.Provider().GetRequiredService<EventDispatcher>();
        for (var pass = 0; pass < 5; pass++)
        {
            await dispatcher.DispatchOnceAsync(_h.Code, Ct);
        }
    }

    [Fact]
    public async Task S130b_FR_KRN_DOC_01_FR_KRN_DOC_03_FR_HRM_ATT_11_proof_of_a_regularization_follows_the_request_and_is_kept_with_it_when_decided()
    {
        var r = await SetUpAsync();
        var asked = await _h.PostAsync(r.User, "/api/hrm/attendance/regularization-requests", new { exceptionId = r.Exception, requestedOut = ReportOrg.At(r.Day, "18:00"), reason = "Forgot to punch out" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.Status);
        var request = asked.Json.GetProperty("id").GetGuid();

        // The person it is for adds proof; the other colleague and the other company can neither add nor see anything.
        var up = await UploadAsync(r.User, RegularizationType, request, "gate-log");
        Assert.True(up.Status == HttpStatusCode.Created, up.Body.ToString());
        var file = up.Body.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(r.Stranger, RegularizationType, request, "x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(r.Other, RegularizationType, request, "y")).Status);
        foreach (var outsider in new[] { r.Stranger, r.Other })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(outsider, $"{Attachments}?ownerType={RegularizationType}&ownerId={request}", Ct)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(outsider, $"{Attachments}/{file}", Ct)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(outsider, $"{Attachments}/{file}/content", Ct)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await _h.DeleteAsync(outsider, $"{Attachments}/{file}", Ct)).Status);
        }

        // The requester reads their own file; HR (the approver with an open task, and a reader with scope all) reads it too.
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(r.User, $"{Attachments}/{file}/content", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(r.Org.Approver, $"{Attachments}/{file}/content", Ct)).Status);
        Assert.Equal(1, (await _h.GetAsync(r.Org.Approver, $"{Attachments}?ownerType={RegularizationType}&ownerId={request}", Ct)).Json.GetArrayLength());

        // The request keeps the ids of its files when it is decided, and a decided request takes no more files.
        var task = await TaskOfAsync(asked.Json.GetProperty("instanceId").GetGuid());
        var approved = await _h.PostAsync(r.Org.Approver, $"/api/wf/tasks/{task}/approve", new { comment = "seen the gate log" }, Ct);
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Body);
        Assert.Equal(file, await _h.ScalarAsync<Guid>("select file_ids[1] from hrm.regularization_requests where id = $1", Ct, request));
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(r.User, RegularizationType, request, "late")).Status);
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(r.User, $"{Attachments}/{file}/content", Ct)).Status); // the proof stays readable for the person
        await DispatchAsync();
    }

    [Fact]
    public async Task S130b_FR_KRN_DOC_01_FR_KRN_DOC_03_FR_HRM_ATT_14_proof_of_a_duty_request_follows_the_request_a_party_may_delete_while_it_waits_and_a_rejection_keeps_the_ids()
    {
        var r = await SetUpAsync();
        var start = r.Org.Today.AddDays(2);
        var asked = await _h.PostAsync(r.User, "/api/hrm/attendance/duty-requests", new { kind = "official_visit", startDate = ReportOrg.Iso(start), endDate = ReportOrg.Iso(start), purpose = "Supplier audit" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.Status);
        var request = asked.Json.GetProperty("id").GetGuid();

        var first = await UploadAsync(r.User, DutyType, request, "invitation");
        Assert.True(first.Status == HttpStatusCode.Created, first.Body.ToString());
        var second = await UploadAsync(r.User, DutyType, request, "letter");
        Assert.Equal(HttpStatusCode.Created, second.Status);
        var wrong = second.Body.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(r.Stranger, DutyType, request, "x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(r.Other, DutyType, request, "y")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(r.Stranger, $"{Attachments}/{first.Body.GetProperty("id").GetGuid()}/content", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(r.Other, $"{Attachments}?ownerType={DutyType}&ownerId={request}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.DeleteAsync(r.Stranger, $"{Attachments}/{wrong}", Ct)).Status);

        // The person withdraws the wrong file while the request waits; the approver sees one file and rejects the request.
        Assert.Equal(HttpStatusCode.NoContent, (await _h.DeleteAsync(r.User, $"{Attachments}/{wrong}", Ct)).Status);
        Assert.Equal(1, (await _h.GetAsync(r.Org.Approver, $"{Attachments}?ownerType={DutyType}&ownerId={request}", Ct)).Json.GetArrayLength());
        var task = await TaskOfAsync(asked.Json.GetProperty("instanceId").GetGuid());
        var rejected = await _h.PostAsync(r.Org.Approver, $"/api/wf/tasks/{task}/reject", new { comment = "not needed" }, Ct);
        Assert.True(rejected.Status == HttpStatusCode.OK, rejected.Body);
        Assert.Equal(1, await _h.ScalarAsync<int>("select coalesce(array_length(file_ids, 1), 0) from hrm.duty_requests where id = $1 and status = 'rejected'", Ct, request));
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.DeleteAsync(r.User, $"{Attachments}/{first.Body.GetProperty("id").GetGuid()}", Ct)).Status);
    }
}
