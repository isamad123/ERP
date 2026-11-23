using System.Net;
using Erp.IntegrationTests.Access;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Drafts;

/// <summary>
/// Slice S1.21a (CR-077, ruling Q7; FR-ADM-UXA-05) through the real API pipeline and the real access engine: autosaved drafts of long
/// forms are kept for the person who started them, encrypted, replaced by the next save, renewed on every save and never seen by
/// anyone else. Other customers: the tenant-isolation suite (IdentityProbe).
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class FormDraftTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Drafts = "/api/cfg/drafts";
    private AccessHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await AccessHarness.ForAsync(fx);
        _h.Clock.Reset();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Form() => "hrm.hire_" + Guid.NewGuid().ToString("N")[..6];

    private async Task<Guid> PersonAsync()
    {
        var user = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["iam.self.manage"]);
        await _h.AssignAsync(user, role, null, _h.Today.AddDays(-1), null, Ct);
        return user;
    }

    [Fact]
    [Trait("Requirement", "FR-ADM-UXA-05")]
    public async Task S121a_FR_ADM_UXA_05_a_draft_is_saved_encrypted_listed_replaced_by_the_next_save_and_deleted()
    {
        var form = Form();
        var me = await PersonAsync();
        var first = await _h.PutAsync(me, Drafts, new { formKey = form, data = new { step = "identifiers", person = new { fullName = "Ayesha Khan" } } }, Ct);
        Assert.True(first.Status == HttpStatusCode.Created, first.Body);
        var id = first.Json.GetProperty("id").GetGuid();
        Assert.Equal("identifiers", first.Json.GetProperty("data").GetProperty("step").GetString());

        // The stored content is ciphertext: the person's name is nowhere in the table.
        await using (var c = await _h.OwnerAsync(ct: Ct))
        {
            await using var command = new Npgsql.NpgsqlCommand("select data from cfg.form_drafts where id = $1", c);
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = id });
            var stored = (byte[])(await command.ExecuteScalarAsync(Ct))!;
            Assert.DoesNotContain("Ayesha", System.Text.Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
        }

        var second = await _h.PutAsync(me, Drafts, new { formKey = form, data = new { step = "contacts" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, second.Status); // the same form: replaced, not added
        Assert.Equal(id, second.Json.GetProperty("id").GetGuid());
        Assert.Equal(2, second.Json.GetProperty("rowVersion").GetInt64());
        var listed = (await _h.GetAsync(me, $"{Drafts}?form={form}", Ct)).Json;
        Assert.Equal("contacts", Assert.Single(listed.EnumerateArray()).GetProperty("data").GetProperty("step").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _h.GetAsync(me, $"{Drafts}/{id}", Ct)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await _h.DeleteAsync(me, $"{Drafts}/{id}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(me, $"{Drafts}/{id}", Ct)).Status);
        Assert.Empty((await _h.GetAsync(me, $"{Drafts}?form={form}", Ct)).Json.EnumerateArray());
    }

    [Fact]
    [Trait("Requirement", "FR-ADM-UXA-05")]
    public async Task S121a_FR_ADM_UXA_05_another_person_never_sees_changes_or_deletes_a_draft_and_the_generated_list_shows_only_the_owners()
    {
        var form = Form();
        var me = await PersonAsync();
        var other = await PersonAsync();
        var mine = await _h.PutAsync(me, Drafts, new { formKey = form, data = new { step = "person" } }, Ct);
        var id = mine.Json.GetProperty("id").GetGuid();

        Assert.Empty((await _h.GetAsync(other, $"{Drafts}?form={form}", Ct)).Json.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(other, $"{Drafts}/{id}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.DeleteAsync(other, $"{Drafts}/{id}", Ct)).Status);
        var theirs = await _h.PutAsync(other, Drafts, new { formKey = form, data = new { step = "theirs" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, theirs.Status); // the same form: their own draft, not mine replaced
        Assert.NotEqual(id, theirs.Json.GetProperty("id").GetGuid());
        Assert.Equal("person", (await _h.GetAsync(me, $"{Drafts}/{id}", Ct)).Json.GetProperty("data").GetProperty("step").GetString());

        // The generated endpoint only reads: no write reaches the table around the service.
        Assert.True((await _h.PostAsync(me, "/api/cfg/form-drafts", new { formKey = form }, Ct)).Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Requirement", "FR-ADM-UXA-05")]
    public async Task S121a_FR_ADM_UXA_05_a_draft_of_a_record_is_kept_apart_from_the_new_record_draft_and_bad_input_is_refused()
    {
        var form = Form();
        var me = await PersonAsync();
        var record = Guid.NewGuid();
        var created = await _h.PutAsync(me, Drafts, new { formKey = form, data = new { step = "new" } }, Ct);
        var edit = await _h.PutAsync(me, Drafts, new { formKey = form, recordId = record, data = new { step = "edit" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, edit.Status);
        Assert.NotEqual(created.Json.GetProperty("id").GetGuid(), edit.Json.GetProperty("id").GetGuid());
        Assert.Equal(2, (await _h.GetAsync(me, $"{Drafts}?form={form}", Ct)).Json.GetArrayLength());

        var badForm = await _h.PutAsync(me, Drafts, new { formKey = "Hire form!", data = new { step = "x" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badForm.Status);
        Assert.Equal("errors.drafts.invalid_form", badForm.Json.GetProperty("errors").GetProperty("formKey")[0].GetString());
        var notObject = await _h.PutAsync(me, Drafts, new { formKey = form, data = "text" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notObject.Status);
        var tooBig = await _h.PutAsync(me, Drafts, new { formKey = form, data = new { note = new string('x', 70_000) } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.Status);
        Assert.Equal("errors.drafts.invalid_data", tooBig.Json.GetProperty("errors").GetProperty("data")[0].GetString());
    }

    [Fact]
    [Trait("Requirement", "FR-ADM-UXA-05")]
    public async Task S121a_FR_ADM_UXA_05_a_draft_past_its_keeping_time_is_gone_and_every_save_starts_the_time_again()
    {
        var form = Form();
        var me = await PersonAsync();
        var saved = await _h.PutAsync(me, Drafts, new { formKey = form, data = new { step = "person" } }, Ct);
        var id = saved.Json.GetProperty("id").GetGuid();
        var firstExpiry = saved.Json.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.Equal(30, Math.Round((firstExpiry - _h.Clock.UtcNow).TotalDays)); // cfg.drafts.keep_days default

        _h.Clock.Advance(TimeSpan.FromDays(20));
        var renewed = await _h.PutAsync(me, Drafts, new { formKey = form, data = new { step = "contacts" } }, Ct);
        Assert.True(renewed.Json.GetProperty("expiresAt").GetDateTimeOffset() > firstExpiry.AddDays(15)); // a save at day 20 keeps it to day 50

        _h.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(HttpStatusCode.NotFound, (await _h.GetAsync(me, $"{Drafts}/{id}", Ct)).Status);
        Assert.Empty((await _h.GetAsync(me, $"{Drafts}?form={form}", Ct)).Json.EnumerateArray());
    }

    [Fact]
    [Trait("Requirement", "FR-ADM-UXA-05")]
    public async Task S121a_FR_ADM_UXA_05_a_person_without_the_self_service_right_cannot_use_drafts()
    {
        var user = await _h.UserAsync(Ct);
        var role = await _h.RoleAsync(Ct, ["org.companies.read"]);
        await _h.AssignAsync(user, role, null, _h.Today.AddDays(-1), null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.PutAsync(user, Drafts, new { formKey = Form(), data = new { step = "x" } }, Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.GetAsync(user, Drafts, Ct)).Status);
    }
}
