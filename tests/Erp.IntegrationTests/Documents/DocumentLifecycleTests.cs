using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Documents;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests.Documents;

/// <summary>
/// Retention, legal hold and expiry reminders of slice S1.11c1 (Build item 19; KRN-DOC-04, KRN-DOC-08, rulings Q24 and Q25):
/// the daily rules run as of a day the test chooses, over stored files.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class DocumentLifecycleTests(TenancyFixture fx) : IAsyncLifetime
{
    private JobRig _rig = null!;
    private string _root = null!;
    private LocalFolderFileStore _store = null!;
    private Guid _company;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _rig = await JobRig.StartAsync(fx, "dl", Ct);
        _root = Path.Combine(Path.GetTempPath(), "erp-life-" + Guid.NewGuid().ToString("N"));
        _store = new LocalFolderFileStore(_root);
        _company = await _rig.ScalarAsync<Guid>("select id from org.companies where not is_deleted order by created_at limit 1", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        await _rig.DisposeAsync();
    }

    private async Task<Guid> UserAsync()
    {
        var id = Guid.CreateVersion7();
        var name = "dl" + Guid.NewGuid().ToString("N")[..12];
        await _rig.ExecAsync("""
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@life.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3)
            """, Ct, null, id, name, BaselineSeed.SystemUserId);
        return id;
    }

    /// <summary>A stored file row (and its content in the folder store), uploaded <paramref name="monthsAgo"/> months before the test's today.</summary>
    private async Task<Guid> FileAsync(Guid uploader, string? type, int monthsAgo = 0, DateOnly? expires = null, bool hold = false, DateOnly? retentionUntil = null, string status = "clean", Guid? previous = null, int version = 1)
    {
        var id = Guid.CreateVersion7();
        var key = $"tenants/{_rig.Code}/files/{id:N}";
        await _store.PutAsync(key, [1, 2, 3], Ct);
        await _rig.ExecAsync("""
            insert into doc.files (id, company_id, owner_type, owner_id, file_name, content_type, size_bytes, checksum, storage_key, version_no, previous_file_id, document_type, expires_on, retention_until,
                is_sensitive, status, legal_hold, key_version, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, 'test.records', $3, 'doc.pdf', 'application/pdf', 3, 'abc', $4, $5, $6, $7, $8, $9, false, $10, $11, 1, $12, $13, $12, $13, 1)
            """, Ct, null, id, _company, Guid.CreateVersion7(), key, version, previous, type, expires, retentionUntil, status, hold, _rig.Clock.UtcNow.AddMonths(-monthsAgo), uploader);
        return id;
    }

    private Task RuleAsync(string type, int months, string action, int? legalMinimum = null) => _rig.ExecAsync("""
        insert into doc.retention_rules (id, document_type, keep_months, action_after, legal_minimum_months, created_at, created_by, updated_at, updated_by, row_version)
        values ($1, $2, $3, $4, $5, now(), $6, now(), $6, 1)
        """, Ct, null, Guid.CreateVersion7(), type, months, action, legalMinimum, BaselineSeed.SystemUserId);

    private async Task<(int Archived, int Deleted)> ApplyAsync()
    {
        await using var c = await _rig.OwnerAsync(ct: Ct);
        return await DocumentRetentionJob.ApplyAsync(c, _store, _rig.Clock.UtcNow, null, Ct);
    }

    private Task<string> StateAsync(Guid file) => _rig.ScalarAsync<string>("select status || '|' || (storage_key like '%purged')::text || '|' || (archived_at is not null)::text from doc.files where id = $1", Ct, null, file);

    [Fact]
    public async Task S111c1_KRN_DOC_04_a_delete_rule_destroys_the_content_of_files_past_retention_but_never_a_file_under_legal_hold_or_of_a_type_without_a_rule()
    {
        var user = await UserAsync();
        await RuleAsync("cv", 12, "delete");
        var old = await FileAsync(user, "cv", monthsAgo: 13);
        var young = await FileAsync(user, "cv", monthsAgo: 3);
        var held = await FileAsync(user, "cv", monthsAgo: 30, hold: true);
        var noRule = await FileAsync(user, "photo", monthsAgo: 120);
        var untyped = await FileAsync(user, null, monthsAgo: 120);

        Assert.Equal((0, 1), await ApplyAsync());
        Assert.Equal("deleted|true|false", await StateAsync(old));
        Assert.Null(await _store.GetAsync($"tenants/{_rig.Code}/files/{old:N}", Ct));
        foreach (var kept in new[] { young, held, noRule, untyped })
        {
            Assert.Equal("clean|false|false", await StateAsync(kept));
            Assert.NotNull(await _store.GetAsync($"tenants/{_rig.Code}/files/{kept:N}", Ct));
        }

        Assert.Equal((0, 0), await ApplyAsync()); // nothing is done twice
        Assert.True(await _rig.ScalarAsync<long>("select count(*) from aud.change_log where entity_type = 'doc.files' and entity_id = $1 and source = 'job'", Ct, null, old) >= 1);

        // The hold is released: the next run applies the rule.
        await _rig.ExecAsync("update doc.files set legal_hold = false where id = $1", Ct, null, held);
        Assert.Equal((0, 1), await ApplyAsync());
        Assert.Equal("deleted|true|false", await StateAsync(held));
    }

    [Fact]
    public async Task S111c1_KRN_DOC_04_review_finding_2_no_version_of_an_attachment_with_a_held_version_is_archived_or_destroyed()
    {
        var user = await UserAsync();
        await RuleAsync("contract", 12, "delete");
        await RuleAsync("memo", 12, "archive");
        var heldOld = await FileAsync(user, "contract", monthsAgo: 30, hold: true);
        var newer = await FileAsync(user, "contract", monthsAgo: 20, previous: heldOld, version: 2); // added before the hold rule copied holds
        var memo = await FileAsync(user, "memo", monthsAgo: 30);
        var heldMemo = await FileAsync(user, "memo", monthsAgo: 20, previous: memo, version: 2, hold: true);

        Assert.Equal((0, 0), await ApplyAsync());
        foreach (var kept in new[] { heldOld, newer, memo, heldMemo })
        {
            Assert.Equal("clean|false|false", await StateAsync(kept));
        }
    }

    [Fact]
    public async Task S111c1_KRN_DOC_04_review_finding_8_content_is_removed_only_after_the_destroyed_state_is_committed_and_a_failed_removal_is_reported()
    {
        var user = await UserAsync();
        await RuleAsync("cv", 12, "delete");
        var first = await FileAsync(user, "cv", monthsAgo: 13);
        var second = await FileAsync(user, "cv", monthsAgo: 14);
        var seen = new List<string>();
        var store = new WatchingStore(_store, async key =>
        {
            // Read from another connection: only committed data is visible there.
            var id = Guid.ParseExact(key[(key.LastIndexOf('/') + 1)..], "N");
            seen.Add(await _rig.ScalarAsync<string>("select storage_key from doc.files where id = $1", Ct, null, id));
            if (id == second)
            {
                throw new IOException("share not reachable");
            }
        });
        var warnings = new List<string>();
        await using (var c = await _rig.OwnerAsync(ct: Ct))
        {
            Assert.Equal((0, 2), await DocumentRetentionJob.ApplyAsync(c, store, _rig.Clock.UtcNow, null, Ct, w =>
            {
                warnings.Add(w);
                return Task.CompletedTask;
            }));
        }

        Assert.Equal(2, seen.Count);
        Assert.All(seen, k => Assert.Equal(FileStatuses.PurgedKey, k));
        Assert.Equal("deleted|true|false", await StateAsync(first));
        Assert.Equal("deleted|true|false", await StateAsync(second));
        Assert.Contains(second.ToString(), Assert.Single(warnings), StringComparison.Ordinal);
    }

    /// <summary>A folder store that calls <c>onDelete</c> before it removes a content.</summary>
    private sealed class WatchingStore(IFileStore inner, Func<string, Task> onDelete) : IFileStore
    {
        public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken) => inner.PutAsync(key, content, cancellationToken);

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => inner.GetAsync(key, cancellationToken);

        public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            await onDelete(key);
            await inner.DeleteAsync(key, cancellationToken);
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => inner.ExistsAsync(key, cancellationToken);
    }

    [Fact]
    public async Task S111c1_KRN_DOC_04_an_archive_rule_hides_the_file_and_a_review_rule_only_lists_it_and_the_date_the_owner_set_wins_over_the_months()
    {
        var user = await UserAsync();
        await RuleAsync("cert", 6, "archive");
        await RuleAsync("note", 6, "review");
        var archive = await FileAsync(user, "cert", monthsAgo: 7);
        var review = await FileAsync(user, "note", monthsAgo: 7);
        var later = await FileAsync(user, "cert", monthsAgo: 7, retentionUntil: DateOnly.FromDateTime(_rig.Clock.UtcNow.UtcDateTime).AddDays(30)); // the owner says: keep 30 more days
        var earlier = await FileAsync(user, "cert", monthsAgo: 1, retentionUntil: DateOnly.FromDateTime(_rig.Clock.UtcNow.UtcDateTime).AddDays(-1)); // the owner says: ended yesterday

        Assert.Equal((2, 0), await ApplyAsync());
        Assert.Equal("clean|false|true", await StateAsync(archive));
        Assert.Equal("clean|false|true", await StateAsync(earlier));
        Assert.Equal("clean|false|false", await StateAsync(later));
        Assert.Equal("clean|false|false", await StateAsync(review));

        await using var c = await _rig.OwnerAsync(ct: Ct);
        var due = await Retention.DueAsync(c, "review", _rig.Clock.UtcNow, Ct);
        Assert.Equal([review], due.Select(d => d.File.Id).ToArray());
    }

    [Fact]
    public async Task S111c1_KRN_DOC_04_a_country_packs_minimum_cannot_be_shortened_by_a_shorter_rule()
    {
        var user = await UserAsync();
        await RuleAsync("tax", 1, "delete", legalMinimum: 12);
        var file = await FileAsync(user, "tax", monthsAgo: 2);
        Assert.Equal((0, 0), await ApplyAsync());
        Assert.Equal("clean|false|false", await StateAsync(file));

        _rig.Clock.Advance(TimeSpan.FromDays(31 * 11));
        Assert.Equal((0, 1), await ApplyAsync());
        Assert.Equal("deleted|true|false", await StateAsync(file));
    }

    [Fact]
    public async Task S111c1_KRN_DOC_08_a_document_that_expires_reminds_its_uploader_30_7_and_0_days_before_once_each_and_never_for_old_versions()
    {
        var user = await UserAsync();
        var today = DateOnly.FromDateTime(_rig.Clock.UtcNow.UtcDateTime);
        var licence = await FileAsync(user, "licence", expires: today.AddDays(30));
        var oldVersion = await FileAsync(user, "licence", expires: today.AddDays(30), version: 1);
        var newer = await FileAsync(user, "licence", expires: today.AddDays(30), previous: oldVersion, version: 2);
        var far = await FileAsync(user, "licence", expires: today.AddDays(90));
        var deleted = await FileAsync(user, "licence", expires: today.AddDays(30), status: "deleted");

        async Task<int> RaiseAsync()
        {
            await using var c = await _rig.OwnerAsync(ct: Ct);
            return await DocumentExpiryJob.RaiseAsync(c, _rig.Clock.UtcNow, Ct);
        }

        Assert.Equal(2, await RaiseAsync()); // the licence and the newer version, not the old version, the far one or the deleted one
        Assert.Equal(0, await RaiseAsync()); // once
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from job.outbox_messages where event_type = 'kernel.document.expiring' and aggregate_id = any($1)", Ct, null, new[] { oldVersion, far, deleted }));

        _rig.Clock.Advance(TimeSpan.FromDays(23)); // 7 days left
        Assert.Equal(2, await RaiseAsync());
        _rig.Clock.Advance(TimeSpan.FromDays(7)); // the day of expiry
        Assert.Equal(2, await RaiseAsync());
        Assert.Equal(0, await RaiseAsync());
        Assert.Equal(3, await _rig.ScalarAsync<long>("select count(*) from job.outbox_messages where event_type = 'kernel.document.expiring' and aggregate_id = $1", Ct, null, licence));
        Assert.Equal(["0", "30", "7"], (await _rig.ScalarAsync<string[]>("select array_agg(payload ->> 'threshold' order by payload ->> 'threshold') from job.outbox_messages where event_type = 'kernel.document.expiring' and aggregate_id = $1", Ct, null, licence)));
        Assert.Contains(user.ToString(), await _rig.ScalarAsync<string>("select payload::text from job.outbox_messages where event_type = 'kernel.document.expiring' and aggregate_id = $1 limit 1", Ct, null, licence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S111c1_KRN_DOC_08_the_reminder_reaches_the_uploader_as_an_in_app_item_in_their_language_once()
    {
        var user = await UserAsync();
        var today = DateOnly.FromDateTime(_rig.Clock.UtcNow.UtcDateTime);
        await FileAsync(user, "licence", expires: today.AddDays(7));
        await using (var c = await _rig.OwnerAsync(ct: Ct))
        {
            Assert.Equal(1, await DocumentExpiryJob.RaiseAsync(c, _rig.Clock.UtcNow, Ct));
        }

        var dispatcher = _rig.Provider().GetRequiredService<EventDispatcher>();
        foreach (var outcome in await dispatcher.DispatchOnceAsync(_rig.Code, Ct))
        {
            Assert.True(outcome.Error is null, outcome.Error);
        }

        var item = await _rig.ScalarAsync<string>("select title || '|' || body || '|' || event_group || '|' || coalesce(link, '') from ntf.in_app_items where user_id = $1", Ct, null, user);
        Assert.StartsWith("A document is about to expire|doc.pdf expires in 7 days.", item, StringComparison.Ordinal);
        Assert.Contains("|documents|/documents/", item, StringComparison.Ordinal);
    }
}
