using System.Text.Json;
using Erp.IntegrationTests.Jobs;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Jobs;
using Erp.Kernel.Infrastructure.Notifications.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.IntegrationTests.Notifications;

/// <summary>
/// E-mail of slice S1.11b2 (Build items 12, 14 and 15): outbox events become queued e-mails, the sender job hands them to
/// the real SMTP adapter, which talks to a small SMTP server on this machine. Refusals, retries with back-off, the delivery
/// log, deduplication, quiet hours, the rate limit and the delivery audit are checked on a real database.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class EmailDeliveryTests(TenancyFixture fx) : IAsyncLifetime
{
    private JobRig _rig = null!;
    private TestMailServer _mail = null!;
    private EventDispatcher _dispatcher = null!;
    private JobWorker _worker = null!;

    private static readonly string[] AdminAudience = ["tenant_admin"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _rig = await JobRig.StartAsync(fx, "em", Ct);
        _mail = new TestMailServer();
        var provider = _rig.Provider(extra: s => TestMailServer.Use(s, _mail.Port, operatorEmail: "ops@operator.test"));
        _dispatcher = provider.GetRequiredService<EventDispatcher>();
        _worker = provider.GetRequiredService<JobWorker>();
    }

    public async ValueTask DisposeAsync()
    {
        await _rig.DisposeAsync();
        await _mail.DisposeAsync();
    }

    private async Task<(Guid Id, string Email)> UserAsync(string language = "en")
    {
        var id = Guid.CreateVersion7();
        var name = "eu" + Guid.NewGuid().ToString("N")[..12];
        await _rig.ExecAsync("""
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@mail.test', $2, $3, 'Asia/Karachi', false, 'active', now(), $4, now(), $4)
            """, Ct, null, id, name, language, BaselineSeed.SystemUserId);
        return (id, name + "@mail.test");
    }

    private async Task<Guid> EventAsync(string schema, string type, object payload)
    {
        var id = Guid.CreateVersion7();
#pragma warning disable CA2100 // test SQL with a fixed schema name
        await _rig.ExecAsync($"insert into {schema}.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload) values ($1, $2, $3, 'test', $1, $4::jsonb)",
            Ct, null, id, _rig.Clock.UtcNow, type, JsonSerializer.Serialize(payload));
#pragma warning restore CA2100
        return id;
    }

    private static object Assigned(Guid[] recipients, string link = "/approvals/task-1") => new
    {
        instance_id = Guid.CreateVersion7(),
        task_id = Guid.CreateVersion7(),
        document_type = "leave.request",
        document_id = Guid.CreateVersion7(),
        company_id = Guid.CreateVersion7(),
        requester_user_id = Guid.CreateVersion7(),
        recipients,
        outcome = (string?)null,
        link,
    };

    private async Task DispatchAsync()
    {
        foreach (var outcome in await _dispatcher.DispatchOnceAsync(_rig.Code, Ct))
        {
            Assert.True(outcome.Error is null, outcome.Error);
        }
    }

    private async Task SendAsync()
    {
        await _rig.QueueAsync(EmailSendJob.JobCode, ct: Ct);
        await _rig.RunUntilIdleAsync(_worker, Ct);
    }

    private Task<string> MessageAsync(Guid user, string column = "status") =>
        _rig.ScalarAsync<string>($"select coalesce({column}::text, '') from ntf.outbound_messages where user_id = $1 order by created_at desc, id desc limit 1", Ct, null, user);

    private Task SetAsync(string key, string json) => _rig.ExecAsync("""
        insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
        values ($1, $2, 'tenant', null, $3::jsonb, now(), $4, now(), $4)
        on conflict (setting_key, layer, scope_id) do update set value = excluded.value
        """, Ct, null, Guid.CreateVersion7(), key, json, BaselineSeed.SystemUserId);

    [Fact]
    public async Task S111b2_KRN_NTF_01_HRM_NTF_03_KRN_WFL_08_a_task_for_an_urdu_manager_gives_an_in_app_item_and_an_urdu_email_with_the_full_link()
    {
        var (manager, address) = await UserAsync("ur");
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([manager]));
        await DispatchAsync();
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from ntf.in_app_items where user_id = $1", Ct, null, manager));
        Assert.Equal("queued", await MessageAsync(manager));

        await SendAsync();

        var mail = Assert.Single(_mail.To(address));
        Assert.Equal("ایک درخواست آپ کی منظوری کی منتظر ہے", mail.Subject);
        var link = "https://" + TenancyFixture.HostOf(_rig.Code) + "/approvals/task-1";
        Assert.Contains(link, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("dir=\"rtl\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("href=\"" + link + "\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Equal("no-reply@erp.test", mail.From.Mailboxes.Single().Address);
        Assert.Equal("sent", await MessageAsync(manager));
        Assert.Equal("workflow.task_assigned", await MessageAsync(manager, "event_code"));
    }

    [Fact]
    public async Task S111b2_KRN_NTF_05_an_email_refused_for_now_is_retried_with_back_off_then_failed_with_the_reason_and_resend_makes_a_new_message()
    {
        var (person, address) = await UserAsync();
        _mail.Refuse[address] = "451 4.3.0 mailbox busy, try later";
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([person]));
        await DispatchAsync();

        var waits = new List<double>();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            await SendAsync();
            Assert.Equal(attempt, int.Parse(await MessageAsync(person, "attempts"), System.Globalization.CultureInfo.InvariantCulture));
            if (attempt < 6)
            {
                Assert.Equal("queued", await MessageAsync(person));
                var next = await _rig.ScalarAsync<DateTimeOffset>("select next_attempt_at from ntf.outbound_messages where user_id = $1", Ct, null, person);
                waits.Add(Math.Round((next - _rig.Clock.UtcNow).TotalMinutes));

                // Not due yet: a run now sends nothing.
                await SendAsync();
                Assert.Equal(attempt, int.Parse(await MessageAsync(person, "attempts"), System.Globalization.CultureInfo.InvariantCulture));
                _rig.Clock.Advance(next - _rig.Clock.UtcNow + TimeSpan.FromSeconds(1));
            }
        }

        Assert.Equal([1d, 5d, 15d, 60d, 240d], waits);
        Assert.Equal("failed", await MessageAsync(person));
        Assert.StartsWith("451", await MessageAsync(person, "error"), StringComparison.Ordinal);
        Assert.Empty(_mail.To(address));

        // The delivery log's resend makes a new message; the failed one stays as it was.
        var failed = Guid.Parse(await MessageAsync(person, "id"));
        _mail.Refuse.Clear();
        Guid copy;
        await using (var owner = await _rig.OwnerAsync(ct: Ct))
        {
            copy = (await DeliveryLog.ResendAsync(owner, failed, BaselineSeed.SystemUserId, _rig.Clock.UtcNow, Ct))!.Value;
        }

        Assert.NotEqual(failed, copy);
        await SendAsync();
        Assert.Single(_mail.To(address));
        Assert.Equal("failed", await _rig.ScalarAsync<string>("select status from ntf.outbound_messages where id = $1", Ct, null, failed));
        Assert.Equal("sent", await _rig.ScalarAsync<string>("select status from ntf.outbound_messages where id = $1", Ct, null, copy));
    }

    [Fact]
    public async Task S111b2_KRN_NTF_07_HRM_NTF_16_a_permanent_refusal_fails_at_once_and_each_result_of_an_audited_event_is_in_the_change_log()
    {
        var (refused, refusedAddress) = await UserAsync();
        var (accepted, _) = await UserAsync();
        _mail.Refuse[refusedAddress] = "550 5.1.1 no such user";
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([refused, accepted]));
        await DispatchAsync();
        await SendAsync();

        Assert.Equal("failed", await MessageAsync(refused));
        Assert.Equal("1", await MessageAsync(refused, "attempts"));
        Assert.StartsWith("550", await MessageAsync(refused, "error"), StringComparison.Ordinal);
        Assert.Equal("sent", await MessageAsync(accepted));

        // Delivery audit (HRM-NTF-16): one change-log entry per result, with time and result, written by the job.
        var audit = await _rig.ScalarAsync<string>("""
            select string_agg(c->>'after', ',' order by l.occurred_at) from aud.change_log l, jsonb_array_elements(l.changes) c
            where l.entity_type = 'ntf.outbound_messages' and l.source = 'job' and c->>'field' = 'status'
            """, Ct);
        Assert.Equal(2, audit.Split(',').Length);
        Assert.Contains("failed", audit, StringComparison.Ordinal);
        Assert.Contains("sent", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S111b2_KRN_NTF_05_one_event_never_gives_the_same_person_two_emails_even_when_it_is_handled_again()
    {
        var (person, address) = await UserAsync();
        var e = await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([person, person]));
        await DispatchAsync();
        await _rig.ExecAsync("delete from wf.inbox_messages where message_id = $1", Ct, null, e);
        await _rig.ExecAsync("update wf.outbox_messages set published_at = null where id = $1", Ct, null, e);
        await DispatchAsync();
        await SendAsync();

        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from ntf.outbound_messages where user_id = $1", Ct, null, person));
        Assert.Single(_mail.To(address));
    }

    [Fact]
    public async Task S111b2_KRN_NTF_04_quiet_hours_hold_an_email_to_their_end_a_mandatory_one_goes_at_once_and_a_switched_off_group_gets_no_email_but_the_item()
    {
        var (quiet, quietAddress) = await UserAsync();
        var (switchedOff, offAddress) = await UserAsync();

        // Quiet the whole day except the next minute (in Asia/Karachi): the e-mail is held to the end of the quiet period.
        var local = TimeZoneInfo.ConvertTime(_rig.Clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi"));
        var from = TimeOnly.FromDateTime(local.DateTime).AddMinutes(-1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var to = TimeOnly.FromDateTime(local.DateTime).AddHours(2).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        await _rig.ExecAsync("insert into ntf.preferences (user_id, event_group, channel, enabled, quiet_hours) values ($1, '*', 'email', true, $2::jsonb)", Ct, null,
            quiet, JsonSerializer.Serialize(new { enabled = true, from, to }));
        await _rig.ExecAsync("insert into ntf.preferences (user_id, event_group, channel, enabled) values ($1, 'approvals', 'email', false)", Ct, null, switchedOff);

        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([quiet, switchedOff]));
        // The licence turning read-only is mandatory (ruling Q11): quiet hours do not hold it.
        await _rig.ExecAsync("""
            insert into iam.user_roles (id, user_id, role_id, company_id, reason, valid_from, created_at, created_by, updated_at, updated_by)
            select $1, $2, r.id, null, 'test', current_date - 1, now(), $3, now(), $3 from iam.roles r where r.code = 'tenant_admin'
            """, Ct, null, Guid.CreateVersion7(), quiet, BaselineSeed.SystemUserId);
        await EventAsync("meta", "kernel.licence.state_changed", new { version = 2, to = "read_only", from = "grace", expires_at = "2026-11-30", next_state = "locked", next_change = "2026-12-15" });
        await DispatchAsync();
        await SendAsync();

        var quietMails = _mail.To(quietAddress);
        Assert.Single(quietMails);
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from ntf.outbound_messages where user_id = $1 and event_code = 'workflow.task_assigned' and status = 'queued' and next_attempt_at > $2",
            Ct, null, quiet, _rig.Clock.UtcNow.AddMinutes(100)));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from ntf.outbound_messages where user_id = $1", Ct, null, switchedOff));
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from ntf.in_app_items where user_id = $1", Ct, null, switchedOff));
        Assert.Empty(_mail.To(offAddress));

        _rig.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        await SendAsync();
        Assert.Equal(2, _mail.To(quietAddress).Count);
    }

    [Fact]
    public async Task S111b2_KRN_NTF_05_at_most_the_set_number_of_emails_leave_in_one_minute()
    {
        await SetAsync("ntf.email.per_minute", "2");
        var people = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            people.Add((await UserAsync()).Id);
        }

        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([.. people]));
        await DispatchAsync();
        await SendAsync();
        Assert.Equal(2, _mail.Received.Count);
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from ntf.outbound_messages where status = 'queued'", Ct));

        _rig.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await SendAsync();
        Assert.Equal(3, _mail.Received.Count);
    }

    [Fact]
    public async Task S111b2_KRN_JOB_06_a_stopped_worker_alert_is_also_emailed_to_the_operator_address()
    {
        await EventAsync("job", "kernel.alert.raised", new { kind = "worker_stopped", key = "k1", severity = "critical", audience = AdminAudience, owner_user_id = (Guid?)null, mandatory = true,
            details = new { code = "kernel.purge", run_id = Guid.CreateVersion7().ToString(), error = "lease expired" } });
        await DispatchAsync();
        await SendAsync();

        var mail = Assert.Single(_mail.To("ops@operator.test"));
        Assert.Contains("/admin/jobs/health", mail.TextBody, StringComparison.Ordinal);
        Assert.Equal("sent", await _rig.ScalarAsync<string>("select status from ntf.outbound_messages where recipient = 'ops@operator.test'", Ct));
    }
    internal static string Dsn(string messageId, string action = "failed", string status = "5.1.1", string diagnostic = "smtp; 550 5.1.1 user unknown") => $"""
        From: MAILER-DAEMON@relay.test
        To: bounces@erp.test
        Subject: Undelivered Mail Returned to Sender
        Message-ID: <{Guid.NewGuid():N}@relay.test>
        MIME-Version: 1.0
        Content-Type: multipart/report; report-type=delivery-status; boundary="b1"

        --b1
        Content-Type: text/plain

        Your message could not be delivered.

        --b1
        Content-Type: message/delivery-status

        Reporting-MTA: dns; relay.test

        Final-Recipient: rfc822; someone@mail.test
        Action: {action}
        Status: {status}
        Diagnostic-Code: {diagnostic}

        --b1
        Content-Type: text/rfc822-headers

        Message-ID: <{messageId}>
        Subject: Approval

        --b1--

        """;

    [Fact]
    public async Task S111b2_KRN_NTF_07_HRM_NTF_16_a_bounce_report_in_the_bounce_mailbox_marks_the_sent_email_failed_and_leaves_other_customers_reports_unread()
    {
        var (person, _) = await UserAsync();
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([person]));
        await DispatchAsync();
        await SendAsync();
        Assert.Equal("sent", await MessageAsync(person));
        var id = Guid.Parse(await MessageAsync(person, "id"));

        await using var imap = new TestImapServer();
        var ours = imap.Add(Dsn(EmailSendJob.MessageId(id, _rig.Code, "no-reply@erp.test")));
        var delayed = imap.Add(Dsn(EmailSendJob.MessageId(id, _rig.Code, "no-reply@erp.test"), action: "delayed", status: "4.4.7"));
        var theirs = imap.Add(Dsn(EmailSendJob.MessageId(Guid.CreateVersion7(), "othercustomer", "no-reply@erp.test")));
        var worker = _rig.Worker(extra: s =>
        {
            s.RemoveAll<MailOptions>();
            s.AddSingleton(MailOptions.None with
            {
                SmtpHost = "127.0.0.1", SmtpPort = _mail.Port, SmtpSecurity = "none", FromAddress = "no-reply@erp.test",
                BounceHost = "127.0.0.1", BouncePort = imap.Port, BounceSecurity = "none", BounceUsername = "bounces",
            });
        });
        await _rig.QueueAsync(EmailBounceJob.JobCode, ct: Ct);
        await _rig.RunUntilIdleAsync(worker, Ct);

        Assert.Equal("failed", await MessageAsync(person));
        Assert.Equal("bounced: 5.1.1 smtp; 550 5.1.1 user unknown", await MessageAsync(person, "error"));
        Assert.True(imap.Messages[ours].Seen);
        Assert.False(imap.Messages[delayed].Seen);
        Assert.False(imap.Messages[theirs].Seen);
        Assert.Equal(1, await _rig.ScalarAsync<long>("""
            select count(*) from aud.change_log l, jsonb_array_elements(l.changes) c
            where l.entity_type = 'ntf.outbound_messages' and l.entity_id = $1 and c->>'field' = 'status' and c->>'before' = 'sent' and c->>'after' = 'failed'
            """, Ct, null, id));
    }

    [Fact]
    public async Task S111b2_KRN_NTF_05_the_daily_clean_up_removes_finished_emails_after_30_days_and_keeps_waiting_ones()
    {
        var (sent, _) = await UserAsync();
        var (waiting, waitingAddress) = await UserAsync();
        _mail.Refuse[waitingAddress] = "451 4.3.0 later";
        await EventAsync("wf", "kernel.workflow.task_assigned", Assigned([sent, waiting]));
        await DispatchAsync();
        await SendAsync();
        Assert.Equal("sent", await MessageAsync(sent));
        Assert.Equal("queued", await MessageAsync(waiting));

        _rig.Clock.Advance(TimeSpan.FromDays(31));
        await _rig.QueueAsync(KernelJobs.Purge, ct: Ct);
        await _rig.RunUntilIdleAsync(_worker, Ct);
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from ntf.outbound_messages where user_id = $1", Ct, null, sent));
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from ntf.outbound_messages where user_id = $1", Ct, null, waiting));
    }
}
