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
}
