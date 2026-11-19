using System.Net;
using System.Text;
using Erp.IntegrationTests.Tenancy;

namespace Erp.IntegrationTests.Documents;

/// <summary>
/// The file mechanism of slice S1.11c1 (Build items 17 and 18) in the real API pipeline: storing, encryption, content checks,
/// scanning, versions, access that follows the record, sensitive downloads and delete. Every refusal is tested.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class AttachmentTests(TenancyFixture fx) : IAsyncLifetime
{
    private DocumentRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await DocumentRig.StartAsync(fx, Ct);

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    private async Task<(Guid User, Guid Record)> OwnerOfRecordAsync()
    {
        var user = await _rig.UserAsync(Ct, "business_user");
        return (user, _rig.Records.Add(_rig.Company, [user], [user], [user]));
    }

    private static Guid IdOf(FileAnswer a) => a.Json.GetProperty("id").GetGuid();

    [Fact]
    public async Task S111c1_KRN_DOC_02_a_file_is_stored_encrypted_under_the_customers_prefix_and_downloads_unchanged()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var bytes = DocumentRig.PdfBytes();
        var up = await _rig.UploadAsync(user, record, "contract.pdf", bytes, Ct, documentType: "contract");
        Assert.True(up.Status == HttpStatusCode.Created, up.Text);
        var id = IdOf(up);
        Assert.Equal("clean", up.Json.GetProperty("status").GetString());
        Assert.Equal(1, up.Json.GetProperty("versionNo").GetInt32());
        Assert.Equal("application/pdf", up.Json.GetProperty("contentType").GetString());

        var down = await _rig.GetAsync(user, $"/api/doc/attachments/{id}/content", Ct);
        Assert.Equal(HttpStatusCode.OK, down.Status);
        Assert.Equal(bytes, down.Bytes);

        var key = await _rig.ScalarAsync<string>("select storage_key from doc.files where id = $1", Ct, id);
        Assert.StartsWith($"tenants/{_rig.Code}/files/", key, StringComparison.Ordinal);
        var onDisk = await File.ReadAllBytesAsync(Path.Combine(_rig.Root, key), Ct);
        Assert.DoesNotContain("Contract text", Encoding.UTF8.GetString(onDisk), StringComparison.Ordinal);
        Assert.NotEqual(bytes, onDisk);
        Assert.True(await _rig.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'doc.files' and entity_id = $1", Ct, id) >= 1); // every change is audited
        Assert.Equal(64, (await _rig.ScalarAsync<string>("select checksum from doc.files where id = $1", Ct, id)).Length);
    }

    [Fact]
    public async Task S111c1_KRN_DOC_02_a_file_whose_content_is_not_an_allowed_type_or_is_empty_or_too_large_is_refused()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var exe = await _rig.UploadAsync(user, record, "invoice.pdf", [0x4D, 0x5A, 0x90, 0x00, 0x03], Ct); // an executable named .pdf
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, exe.Status);
        var zip = await _rig.UploadAsync(user, record, "tool.zip", [0x50, 0x4B, 0x03, 0x04, 0x14], Ct);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, zip.Status);
        var empty = await _rig.UploadAsync(user, record, "empty.pdf", [], Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        var big = await _rig.UploadAsync(user, record, "big.txt", Encoding.ASCII.GetBytes(new string('a', 21 * 1024 * 1024)), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.Status);
        var bad = await _rig.UploadAsync(user, record, "x.pdf", DocumentRig.PdfBytes(), Ct, ownerType: "nothing.here");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from doc.files where owner_id = $1", Ct, record)); // nothing was stored
    }

    [Fact]
    public async Task S111c1_KRN_DOC_02_a_file_waits_until_scanned_clean_and_stays_unavailable_while_the_scanner_is_down()
    {
        var (user, record) = await OwnerOfRecordAsync();
        _rig.Scanner.Down = true;
        var up = await _rig.UploadAsync(user, record, "waiting.pdf", DocumentRig.PdfBytes("waiting"), Ct);
        Assert.Equal(HttpStatusCode.Created, up.Status);
        var id = IdOf(up);
        Assert.Equal("uploading", up.Json.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _rig.GetAsync(user, $"/api/doc/attachments/{id}/content", Ct)).Status);

        _rig.Scanner.Down = false;
        await using (var c = await _rig.OwnerAsync(Ct))
        {
            var outcome = await _rig.Scanning().ScanPendingAsync(c, 100, Ct);
            Assert.True(outcome.Clean >= 1);
        }

        Assert.Equal(HttpStatusCode.OK, (await _rig.GetAsync(user, $"/api/doc/attachments/{id}/content", Ct)).Status);
    }

    [Fact]
    public async Task S111c1_KRN_DOC_02_an_infected_file_is_quarantined_with_a_security_event_and_an_alert()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var alerts = await _rig.ScalarAsync<long>("select count(*) from job.outbox_messages where event_type = 'kernel.alert.raised' and payload::text like '%document_infected%'", Ct);
        var up = await _rig.UploadAsync(user, record, "virus.txt", Encoding.ASCII.GetBytes("X5O!P%@AP EICAR test"), Ct);
        var id = IdOf(up);
        Assert.Equal("quarantined", up.Json.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _rig.GetAsync(user, $"/api/doc/attachments/{id}/content", Ct)).Status);
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'doc.file_infected' and subject = $1", Ct, id.ToString()));
        Assert.Equal(alerts + 1, await _rig.ScalarAsync<long>("select count(*) from job.outbox_messages where event_type = 'kernel.alert.raised' and payload::text like '%document_infected%'", Ct));
    }

    [Fact]
    public async Task S111c1_KRN_DOC_03_a_new_version_keeps_the_old_ones_and_an_old_version_can_be_restored()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var one = DocumentRig.PdfBytes("one");
        var two = DocumentRig.PdfBytes("two");
        var v1 = IdOf(await _rig.UploadAsync(user, record, "letter.pdf", one, Ct));
        var v2 = IdOf(await _rig.UploadAsync(user, record, "letter.pdf", two, Ct, replaces: v1));

        var versions = await _rig.GetAsync(user, $"/api/doc/attachments/{v2}/versions", Ct);
        Assert.Equal([2, 1], versions.Json.EnumerateArray().Select(v => v.GetProperty("versionNo").GetInt32()).ToArray());
        Assert.Equal(1, (await _rig.GetAsync(user, $"/api/doc/attachments?ownerType={TestRecordBook.OwnerType}&ownerId={record}", Ct)).Json.GetArrayLength()); // only the current version is listed
        Assert.Equal(HttpStatusCode.BadRequest, (await _rig.UploadAsync(user, record, "letter.pdf", two, Ct, replaces: v1)).Status); // only the newest version is replaced

        var restored = await _rig.PostAsync(user, $"/api/doc/attachments/{v1}/restore", Ct);
        Assert.True(restored.Status == HttpStatusCode.Created, restored.Text);
        var v3 = IdOf(restored);
        Assert.Equal(3, restored.Json.GetProperty("versionNo").GetInt32());
        Assert.Equal(one, (await _rig.GetAsync(user, $"/api/doc/attachments/{v3}/content", Ct)).Bytes);
        Assert.Equal(two, (await _rig.GetAsync(user, $"/api/doc/attachments/{v2}/content", Ct)).Bytes); // history is kept
        Assert.Equal(HttpStatusCode.BadRequest, (await _rig.PostAsync(user, $"/api/doc/attachments/{v3}/restore", Ct)).Status); // already current
    }

    [Fact]
    public async Task S111c1_KRN_DOC_01_files_follow_their_record_whoever_cannot_read_it_cannot_read_its_files_and_whoever_cannot_change_it_cannot_add_or_delete()
    {
        var owner = await _rig.UserAsync(Ct, "business_user");
        var reader = await _rig.UserAsync(Ct, "business_user");
        var stranger = await _rig.UserAsync(Ct, "business_user");
        var record = _rig.Records.Add(_rig.Company, [owner, reader], [owner]);
        var id = IdOf(await _rig.UploadAsync(owner, record, "a.pdf", DocumentRig.PdfBytes(), Ct));

        Assert.Equal(HttpStatusCode.OK, (await _rig.GetAsync(reader, $"/api/doc/attachments/{id}/content", Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.UploadAsync(reader, record, "b.pdf", DocumentRig.PdfBytes("b"), Ct)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.DeleteAsync(reader, $"/api/doc/attachments/{id}", Ct)).Status);

        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(stranger, $"/api/doc/attachments/{id}/content", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(stranger, $"/api/doc/attachments/{id}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(stranger, $"/api/doc/attachments/{id}/versions", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(stranger, $"/api/doc/attachments?ownerType={TestRecordBook.OwnerType}&ownerId={record}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.UploadAsync(stranger, record, "c.pdf", DocumentRig.PdfBytes("c"), Ct)).Status);
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from doc.files where owner_id = $1", Ct, record));
    }

    [Fact]
    public async Task S111c1_KRN_DOC_01_a_sensitive_file_needs_the_sensitive_right_and_every_download_is_in_the_access_log()
    {
        var hr = await _rig.UserAsync(Ct, "business_user");
        var colleague = await _rig.UserAsync(Ct, "business_user");
        var record = _rig.Records.Add(_rig.Company, [hr, colleague], [hr], [hr]);
        var id = IdOf(await _rig.UploadAsync(hr, record, "salary-letter.pdf", DocumentRig.PdfBytes("pay"), Ct, sensitive: true));
        var open = IdOf(await _rig.UploadAsync(hr, record, "memo.pdf", DocumentRig.PdfBytes("memo"), Ct));

        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(colleague, $"/api/doc/attachments/{id}/content", Ct)).Status);
        Assert.Equal(1, (await _rig.GetAsync(colleague, $"/api/doc/attachments?ownerType={TestRecordBook.OwnerType}&ownerId={record}", Ct)).Json.GetArrayLength()); // the sensitive file is not even listed
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from aud.access_log where entity_id = $1", Ct, id));

        Assert.Equal(HttpStatusCode.OK, (await _rig.GetAsync(hr, $"/api/doc/attachments/{id}/content", Ct)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _rig.GetAsync(hr, $"/api/doc/attachments/{id}/content", Ct)).Status);
        Assert.Equal(2, await _rig.ScalarAsync<long>("select count(*) from aud.access_log where entity_type = 'doc.files' and entity_id = $1 and actor_user_id = $2", Ct, id, hr));
        Assert.Equal(HttpStatusCode.OK, (await _rig.GetAsync(colleague, $"/api/doc/attachments/{open}/content", Ct)).Status);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from aud.access_log where entity_id = $1", Ct, open)); // an ordinary file is not logged
    }

    [Fact]
    public async Task S111c1_KRN_DOC_04_delete_hides_every_version_but_keeps_the_content_and_legal_hold_blocks_delete()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var admin = await _rig.UserAsync(Ct, "tenant_admin");
        var v1 = IdOf(await _rig.UploadAsync(user, record, "a.pdf", DocumentRig.PdfBytes("1"), Ct));
        var v2 = IdOf(await _rig.UploadAsync(user, record, "a.pdf", DocumentRig.PdfBytes("2"), Ct, replaces: v1));
        var held = IdOf(await _rig.UploadAsync(user, record, "held.pdf", DocumentRig.PdfBytes("held"), Ct));

        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.PutJsonAsync(user, $"/api/doc/attachments/{held}/legal-hold", """{"hold":true,"reason":"Dispute"}""", Ct)).Status); // no right to hold
        Assert.Equal(HttpStatusCode.BadRequest, (await _rig.PutJsonAsync(admin, $"/api/doc/attachments/{held}/legal-hold", """{"hold":true}""", Ct)).Status); // a reason is needed
        Assert.Equal(HttpStatusCode.NoContent, (await _rig.PutJsonAsync(admin, $"/api/doc/attachments/{held}/legal-hold", """{"hold":true,"reason":"Dispute"}""", Ct)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await _rig.DeleteAsync(user, $"/api/doc/attachments/{held}", Ct)).Status);
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from aud.security_events where event_type = 'doc.legal_hold_set' and subject = $1", Ct, held.ToString()));

        Assert.Equal(HttpStatusCode.NoContent, (await _rig.DeleteAsync(user, $"/api/doc/attachments/{v2}", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(user, $"/api/doc/attachments/{v1}/content", Ct)).Status);
        Assert.Equal(1, (await _rig.GetAsync(user, $"/api/doc/attachments?ownerType={TestRecordBook.OwnerType}&ownerId={record}", Ct)).Json.GetArrayLength());
        var key = await _rig.ScalarAsync<string>("select storage_key from doc.files where id = $1", Ct, v1);
        Assert.True(File.Exists(Path.Combine(_rig.Root, key))); // the content stays until retention removes it

        Assert.Equal(HttpStatusCode.NoContent, (await _rig.PutJsonAsync(admin, $"/api/doc/attachments/{held}/legal-hold", """{"hold":false}""", Ct)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await _rig.DeleteAsync(user, $"/api/doc/attachments/{held}", Ct)).Status);
    }

    [Fact]
    public async Task S111c1_KRN_DOC_02_the_storage_limit_of_the_licence_refuses_a_file_that_does_not_fit()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var before = await fx.SetClaimAsync(_rig.Code, "limits,storage.gb", 0, Ct);
        try
        {
            var refused = await _rig.UploadAsync(user, record, "full.pdf", DocumentRig.PdfBytes("full"), Ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.Status);
            Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from doc.files where owner_id = $1", Ct, record));
        }
        finally
        {
            await fx.RestoreClaimsAsync(_rig.Code, before, Ct);
        }

        Assert.Equal(HttpStatusCode.Created, (await _rig.UploadAsync(user, record, "fits.pdf", DocumentRig.PdfBytes("fits"), Ct)).Status);
    }

    [Fact]
    public async Task S111c1_KRN_DOC_04_review_finding_2_a_new_or_restored_version_of_a_held_file_is_held_too()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var admin = await _rig.UserAsync(Ct, "tenant_admin");
        var v1 = IdOf(await _rig.UploadAsync(user, record, "claim.pdf", DocumentRig.PdfBytes("1"), Ct));
        Assert.Equal(HttpStatusCode.NoContent, (await _rig.PutJsonAsync(admin, $"/api/doc/attachments/{v1}/legal-hold", """{"hold":true,"reason":"Court case 12"}""", Ct)).Status);

        var v2 = await _rig.UploadAsync(user, record, "claim.pdf", DocumentRig.PdfBytes("2"), Ct, replaces: v1);
        Assert.True(v2.Status == HttpStatusCode.Created, v2.Text);
        Assert.True(v2.Json.GetProperty("legalHold").GetBoolean());
        Assert.Equal("Court case 12", v2.Json.GetProperty("legalHoldReason").GetString());

        var v3 = await _rig.PostAsync(user, $"/api/doc/attachments/{v1}/restore", Ct);
        Assert.True(v3.Status == HttpStatusCode.Created, v3.Text);
        Assert.True(v3.Json.GetProperty("legalHold").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await _rig.DeleteAsync(user, $"/api/doc/attachments/{IdOf(v3)}", Ct)).Status);

        // Releasing the hold releases every version.
        Assert.Equal(HttpStatusCode.NoContent, (await _rig.PutJsonAsync(admin, $"/api/doc/attachments/{v1}/legal-hold", """{"hold":false}""", Ct)).Status);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from doc.files where owner_id = $1 and legal_hold", Ct, record));
    }

    [Fact]
    public async Task S111c1_KRN_DOC_03_review_finding_5_two_replacements_of_the_same_version_never_both_succeed()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var v1 = IdOf(await _rig.UploadAsync(user, record, "rota.pdf", DocumentRig.PdfBytes("1"), Ct));

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => _rig.UploadAsync(user, record, "rota.pdf", DocumentRig.PdfBytes("race" + i), Ct, replaces: v1)));
        Assert.Single(answers, a => a.Status == HttpStatusCode.Created);
        Assert.All(answers.Where(a => a.Status != HttpStatusCode.Created), a => Assert.Contains(a.Status, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict }));
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from doc.files where previous_file_id = $1 and status <> 'deleted'", Ct, v1));
        Assert.Equal(1, (await _rig.GetAsync(user, $"/api/doc/attachments?ownerType={TestRecordBook.OwnerType}&ownerId={record}", Ct)).Json.GetArrayLength());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_rig.Root, "tenants", _rig.Code, "files")).Length); // the losers' content was removed

        // The database itself refuses a second live newer version (uq_files_previous_file_id, CR-051).
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => _rig.ExecAsync("""
            insert into doc.files (id, company_id, owner_type, owner_id, file_name, content_type, size_bytes, checksum, storage_key, version_no, previous_file_id, is_sensitive, status, legal_hold, key_version, created_at, created_by, updated_at, updated_by, row_version)
            select $2, company_id, owner_type, owner_id, file_name, content_type, size_bytes, checksum, 'tenants/x/files/y', 2, id, false, 'clean', false, 1, now(), created_by, now(), created_by, 1 from doc.files where id = $1
            """, Ct, v1, Guid.CreateVersion7()));
        Assert.Equal("uq_files_previous_file_id", error.ConstraintName);
    }

    [Fact]
    public async Task S111c1_KRN_DOC_01_review_finding_9_versions_and_restore_need_the_sensitive_right_for_a_sensitive_version()
    {
        var hr = await _rig.UserAsync(Ct, "business_user");
        var colleague = await _rig.UserAsync(Ct, "business_user");
        var record = _rig.Records.Add(_rig.Company, [hr, colleague], [hr, colleague], [hr]);
        var v1 = IdOf(await _rig.UploadAsync(hr, record, "offer.pdf", DocumentRig.PdfBytes("plain"), Ct));
        var v2 = IdOf(await _rig.UploadAsync(hr, record, "offer.pdf", DocumentRig.PdfBytes("with salary"), Ct, sensitive: true, replaces: v1));

        var seen = await _rig.GetAsync(colleague, $"/api/doc/attachments/{v1}/versions", Ct);
        Assert.Equal(HttpStatusCode.OK, seen.Status);
        Assert.Equal([1], seen.Json.EnumerateArray().Select(v => v.GetProperty("versionNo").GetInt32()).ToArray()); // the sensitive version 2 is not shown
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.PostAsync(colleague, $"/api/doc/attachments/{v2}/restore", Ct)).Status);
        var all = await _rig.GetAsync(hr, $"/api/doc/attachments/{v1}/versions", Ct);
        Assert.Equal([2, 1], all.Json.EnumerateArray().Select(v => v.GetProperty("versionNo").GetInt32()).ToArray());
    }

    [Fact]
    public async Task S111c1_KRN_DOC_08_review_finding_7_a_sent_reminder_is_remembered_after_the_event_clean_up_and_by_a_new_version_with_the_same_expiry()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var expires = DateOnly.FromDateTime(_rig.Clock.UtcNow.UtcDateTime).AddDays(30);
        var v1 = IdOf(await _rig.UploadAsync(user, record, "visa.pdf", DocumentRig.PdfBytes("1"), Ct, expiresOn: expires));
        Assert.Equal(1, await RaiseRemindersAsync());

        // The daily clean-up deletes published events after 30 days; the reminder must not come again.
        await _rig.ExecAsync("delete from job.outbox_messages where event_type = 'kernel.document.expiring' and aggregate_id = $1", Ct, v1);
        Assert.Equal(0, await RaiseRemindersAsync());

        // A replacement with the same expiry date keeps the progress; one with a new date starts again.
        var v2 = IdOf(await _rig.UploadAsync(user, record, "visa.pdf", DocumentRig.PdfBytes("2"), Ct, replaces: v1));
        Assert.Equal(30, await _rig.ScalarAsync<int>("select expiry_reminded_days from doc.files where id = $1", Ct, v2));
        Assert.Equal(0, await RaiseRemindersAsync());
        _ = IdOf(await _rig.UploadAsync(user, record, "visa.pdf", DocumentRig.PdfBytes("3"), Ct, expiresOn: expires.AddDays(-5), replaces: v2));
        Assert.Equal(1, await RaiseRemindersAsync());
    }

    private async Task<int> RaiseRemindersAsync()
    {
        await using var c = await _rig.OwnerAsync(Ct);
        return await Erp.Kernel.Infrastructure.Documents.DocumentExpiryJob.RaiseAsync(c, _rig.Clock.UtcNow, Ct);
    }

    [Fact]
    public async Task S111c1_KRN_DOC_04_review_finding_10_changing_only_the_reason_of_a_hold_logs_only_the_reason_with_its_earlier_value()
    {
        var (user, record) = await OwnerOfRecordAsync();
        var admin = await _rig.UserAsync(Ct, "tenant_admin");
        var id = IdOf(await _rig.UploadAsync(user, record, "evidence.pdf", DocumentRig.PdfBytes(), Ct));
        Assert.Equal(HttpStatusCode.NoContent, (await _rig.PutJsonAsync(admin, $"/api/doc/attachments/{id}/legal-hold", """{"hold":true,"reason":"First"}""", Ct)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await _rig.PutJsonAsync(admin, $"/api/doc/attachments/{id}/legal-hold", """{"hold":true,"reason":"Second"}""", Ct)).Status);
        var last = await _rig.ScalarAsync<string>("select changes::text from aud.change_log where entity_type = 'doc.files' and entity_id = $1 and action = 'update' order by occurred_at desc, id desc limit 1", Ct, id);
        Assert.DoesNotContain("legal_hold\"", last.Replace("legal_hold_reason\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\"before\":\"First\"", last.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\"after\":\"Second\"", last.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void S111c1_KRN_DOC_02_review_finding_4_the_upload_endpoint_accepts_requests_up_to_the_ceiling_of_the_size_setting()
    {
        var upload = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)_rig.App).DataSources.SelectMany(d => d.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText!.TrimEnd('/') == "/api/doc/attachments" && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        var limit = upload.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>();
        var form = upload.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IFormOptionsMetadata>();
        Assert.NotNull(limit);
        Assert.NotNull(form);
        Assert.True(limit.MaxRequestBodySize > Erp.Kernel.Api.Documents.DocumentEndpoints.MaxUploadBytes);
        Assert.True(form.MultipartBodyLengthLimit > Erp.Kernel.Api.Documents.DocumentEndpoints.MaxUploadBytes);
        Assert.Equal(200L * 1024 * 1024, Erp.Kernel.Api.Documents.DocumentEndpoints.MaxUploadBytes); // the doc.upload.max_mb ceiling (settings.yaml max 200)
    }

    [Fact]
    public async Task S111c1_KRN_DOC_01_review_finding_3_the_generated_file_list_is_gone_files_are_read_only_through_their_record()
    {
        var admin = await _rig.UserAsync(Ct, "tenant_admin");
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(admin, "/api/doc/files", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(admin, $"/api/doc/files/{Guid.CreateVersion7()}", Ct)).Status);
    }

    [Fact]
    public async Task S111c1_KRN_WFL_07_the_people_of_a_request_attach_and_read_evidence_on_a_decision_and_nobody_else_can()
    {
        var approver = await _rig.UserAsync(Ct, "business_user");
        var requester = await _rig.UserAsync(Ct, "business_user");
        var other = await _rig.UserAsync(Ct, "tenant_admin"); // even an administrator is not part of the request
        var task = await _rig.WorkflowTaskAsync(approver, requester, Ct);

        var byApprover = await _rig.UploadAsync(approver, task, "quote.pdf", DocumentRig.PdfBytes("quote"), Ct, ownerType: "wf.workflow_tasks");
        Assert.True(byApprover.Status == HttpStatusCode.Created, byApprover.Text);
        var byRequester = await _rig.UploadAsync(requester, task, "receipt.pdf", DocumentRig.PdfBytes("receipt"), Ct, ownerType: "wf.workflow_tasks");
        Assert.Equal(HttpStatusCode.Created, byRequester.Status);
        Assert.Equal(2, (await _rig.GetAsync(requester, $"/api/doc/attachments?ownerType=wf.workflow_tasks&ownerId={task}", Ct)).Json.GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await _rig.GetAsync(approver, $"/api/doc/attachments/{IdOf(byRequester)}/content", Ct)).Status);

        Assert.Equal(HttpStatusCode.NotFound, (await _rig.UploadAsync(other, task, "x.pdf", DocumentRig.PdfBytes("x"), Ct, ownerType: "wf.workflow_tasks")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(other, $"/api/doc/attachments/{IdOf(byApprover)}/content", Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _rig.GetAsync(other, $"/api/doc/attachments?ownerType=wf.workflow_tasks&ownerId={task}", Ct)).Status);

        // Once the task is decided the approver can still read but not add; the requester's request is no longer open either.
        await _rig.ExecAsync("update wf.workflow_tasks set status = 'approved', acted_by = $2 where id = $1", Ct, task, approver);
        await _rig.ExecAsync("update wf.workflow_instances set status = 'approved' where id = (select instance_id from wf.workflow_tasks where id = $1)", Ct, task);
        Assert.Equal(HttpStatusCode.Forbidden, (await _rig.UploadAsync(approver, task, "late.pdf", DocumentRig.PdfBytes("late"), Ct, ownerType: "wf.workflow_tasks")).Status);
        Assert.Equal(HttpStatusCode.OK, (await _rig.GetAsync(approver, $"/api/doc/attachments/{IdOf(byApprover)}/content", Ct)).Status);
    }
}
