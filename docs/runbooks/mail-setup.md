# Runbook: e-mail sending (slice S1.11b2)

Notification e-mails, sign-in codes, password reset links and invitations leave through one SMTP connection per run
of the job `kernel.email_send` (every minute in the worker host). Ruling Q10 and plan point P2 decide where they go.

## 1. Operator relay (SaaS)
Set these environment variables on every **worker** host (the API host does not send e-mail):

| Setting | Meaning | Default |
| --- | --- | --- |
| `Mail:Smtp:Host` | The relay's host name. Without it no e-mail leaves; messages wait in the queue and the job log says so. | none |
| `Mail:Smtp:Port` | Port | 587 |
| `Mail:Smtp:Security` | `starttls`, `ssl` or `none` (only for a relay on the same machine) | starttls |
| `Mail:Smtp:Username` | Login, if the relay needs one | none |
| `Mail:Smtp:PasswordRef` | Reference of the password **in the secret store** (`ERP_SECRETS_FILE`), never the password itself | none |
| `Mail:From` | Sender address of the relay, for example `no-reply@mail.example.com` | none |
| `Alerts:OperatorEmail` | Also e-mail the "background server stopped" alert to this address | none |

The customer sets the **sender name** (`ntf.email.sender_name`) and the **reply-to address** (`ntf.email.reply_to`) in
their settings. The rate is `ntf.email.per_minute` (default 60 a minute per customer).

## 2. A sender address on the customer's own domain
Only after the customer's domain is checked:
1. Ask the customer's DNS administrator to publish an SPF record that includes the relay, for example
   `v=spf1 include:mail.example.com ~all` on `acme.com.pk`.
2. Generate a DKIM key for the domain in the relay and have the customer publish the selector record
   (`selector._domainkey.acme.com.pk TXT "v=DKIM1; k=rsa; p=..."`).
3. Optionally a DMARC record (`_dmarc.acme.com.pk TXT "v=DMARC1; p=quarantine; rua=mailto:..."`).
4. Send a test message from the relay and check that the receiving server reports `spf=pass` and `dkim=pass`.
5. Then set the address:

```bash
dotnet run --project src/Host/Erp.Host.Migrator -- mail sender --tenant acme --from notices@acme.com.pk
```

The change is in the customer's audit log (entity `itg.connectors`, with the operator's e-mail).

## 3. The customer's own mail server (on-premises)
The password is read from `ERP_MAIL_PASSWORD` and stored in the secret store (`tenants/<code>/mail-smtp-password`),
never in the database:

```bash
ERP_MAIL_PASSWORD='...' dotnet run --project src/Host/Erp.Host.Migrator -- mail server --tenant acme --host smtp.acme.local --port 587 --security starttls --username erp --from erp@acme.local
```

Check and undo:

```bash
dotnet run --project src/Host/Erp.Host.Migrator -- mail show --tenant acme
```

```bash
dotnet run --project src/Host/Erp.Host.Migrator -- mail clear --tenant acme
```

`mail clear` switches the connector off; the operator relay and `Mail:From` are used again.

## 4. Bounces
A refusal while sending (SMTP 5xx) fails the message at once. A temporary refusal (4xx, a broken connection) is tried
again after 1, 5, 15, 60 and 240 minutes, then fails with the last reason. Later bounce reports are read every 15 minutes
(job `kernel.email_bounces`) from an IMAP mailbox when it is set on the worker:

| Setting | Default |
| --- | --- |
| `Mail:Bounce:Host`, `Mail:Bounce:Username` | none (no bounce reading) |
| `Mail:Bounce:Port` | 993 |
| `Mail:Bounce:Security` | ssl |
| `Mail:Bounce:PasswordRef` | secret-store reference |
| `Mail:Bounce:Folder` | INBOX |

Point the relay's bounce (return-path) address at that mailbox. Every message carries the Message-ID
`<message id>.<customer code>@<sender domain>`; each customer's run marks only its own reports read, so one mailbox can
serve every customer. Reports that only say "delayed" stay unread.

## 5. Checking
- Delivery log: `GET /api/ntf/deliveries?status=failed` (reason in `error`), numbers at `GET /api/ntf/deliveries/summary`.
- Job log: `GET /api/jobs/runs?code=kernel.email_send`.
- Resend: `POST /api/ntf/deliveries/{id}/resend` (not for sign-in links and codes: send a new one instead).
- Finished messages are removed by the daily clean-up after 30 days.
