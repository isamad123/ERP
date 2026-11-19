# Runbook: file store and virus scanning (slice S1.11c1)

Attachments (`/api/doc/attachments`) keep their content in one file store per deployment. Content is encrypted with the
customer's own file key before it is stored, so the store itself never holds readable content. Ruling Q20, Q21.

## 1. Choose the store (every API and worker host, same values)
| Setting | Meaning | Default |
| --- | --- | --- |
| `Files:Store` | `local` (a folder, also a mounted network share) or `s3` (any S3-compatible store) | `local` |
| `Files:Root` | The folder, for `local`. Must be the same folder for the API and worker hosts. | none (required) |
| `Files:S3:Bucket` | The bucket, for `s3` | none |
| `Files:S3:ServiceUrl` | Address of a compatible store such as MinIO; leave out for Amazon S3 | none |
| `Files:S3:Region` | Region name, for example `eu-central-1` | none |
| `Files:S3:AccessKeyId` | The access key id | none |
| `Files:S3:SecretKeyRef` | Reference of the secret key **in the secret store**, never the key itself | none |

Without a valid choice the API answers uploads and downloads with a server error and logs the missing setting; nothing is lost.
Every customer's files sit under `tenants/<customer code>/files/`. Back up the folder or bucket together with the database
and the secret store: **without the customer's file key (`tenants/<code>/file-key-v1` in the secret store) the files cannot be read.**

## 2. Virus scanning
ClamAV runs as its own service (clamd). Set on every host: `Files:Scan:Host` and `Files:Scan:Port` (default 3310).
The customer's setting `doc.scan.mode` is `required` (default: a file cannot be downloaded until scanned clean; while the
scanner is down files wait, and the job `kernel.doc_scan` checks them every ten minutes) or `off` (an on-premises
customer may choose it; files are available at once and are recorded as not scanned).
An infected file is quarantined with the security event `doc.file_infected` and a critical alert to the tenant administrators.

## 3. Daily jobs (worker host)
- `kernel.doc_scan` every 10 minutes: checks files waiting for the scanner.
- `kernel.doc_retention` daily 03:45: applies the customer's retention rules (`doc.retention_rules`): archive hides a file,
  delete destroys the content and keeps the record, review only lists the file for an administrator
  (`GET /api/doc/attachments/retention-review`). No rule ships by default, so nothing is deleted until a customer or a country pack sets one.
  A file under legal hold is never touched; a country pack's minimum months can never be shortened.
- `kernel.doc_expiry` daily 07:00: reminds the uploader 30, 7 and 0 days before a document's expiry date (`doc.expiry.reminder_days`).

## 4. Check it works
1. Upload a PDF through the API; the answer shows status `clean` (or `uploading` when the scanner is down).
2. Look in the store folder: the file under `tenants/<code>/files/` must not be readable as a PDF.
3. Upload the EICAR test string as a `.txt` file: the file must be `quarantined` and the administrators get an alert.
4. Real S3 and real ClamAV are checked at deployment (the automated tests use a folder store and a stand-in scanner).
