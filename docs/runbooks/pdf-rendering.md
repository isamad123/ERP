# Runbook: document templates and PDF output (slice S1.11c2)

Letters, certificates and similar documents are produced from templates (`/api/doc/templates`). A template is Scriban text that
makes HTML; the server's headless Chromium prints the HTML to PDF (rulings Q17, Q27). Chromium is a server tool, installed like
`pg_dump`; it is not part of the product code. Both the API host and the worker host need it (single documents are made in the
request, batches in the worker).

## 1. Install Chromium (every API and worker server)
Any current stable Chromium or Google Chrome build works.
- Debian or Ubuntu: `sudo apt-get install -y chromium` (package name varies: `chromium-browser` on some releases).
- Container images: install the same package in the image of both hosts.
- GitHub Actions runners (`ubuntu-latest`) already ship Google Chrome at `/usr/bin/google-chrome`; the integration tests use it.

## 2. Settings (environment variables or configuration, same values for both hosts)
| Setting | Meaning | Default |
| --- | --- | --- |
| `Pdf:ChromiumPath` | Full path of the Chromium program | searched in `/usr/bin/chromium`, `/usr/bin/chromium-browser`, `/usr/bin/google-chrome`, `/usr/bin/google-chrome-stable`, `/snap/bin/chromium`, `/opt/pw-browsers/chromium` |
| `Pdf:NoSandbox` | `true` when the host runs as root inside a container (Chromium refuses to start its sandbox there) | `false` |
| `Pdf:TimeoutSeconds` | Longest time one document may take | 60 |
| `Pdf:Parallel` | Browsers one host runs at the same time | 2 |

Customer settings (changed through the settings screen, not here): `doc.template.max_kb` (largest template text, default 200 KB)
and `doc.pdf.max_pages` (most pages in one document, default 50).

## 3. What happens when Chromium is missing
Producing a PDF answers `503` with the title `errors.template.pdf_unavailable`. Previews and documents in HTML or text form do not need
Chromium. Nothing is stored for a failed document.

## 4. Safety of the printing step
- Each document uses a fresh browser process that is closed afterwards.
- The page runs no script and fetches nothing from outside: every request except embedded data is refused, and the page carries a
  content policy that allows only embedded images, fonts and styles. A template cannot read an internal address.
- Template text is sandboxed (Scriban): no file or include access, no .NET objects, no machine clock (`now` and `today` come from the
  system clock of the product), limits on loops, recursion, output size and time.

## 5. Fonts
Noto Sans and Noto Nastaliq Urdu (SIL Open Font Licence 1.1, allowed for fonts only, ruling Q18) are embedded in the product and put
into every page as data, so Urdu text is joined, right to left and looks the same on every server. No font needs installing.

## 6. Letterhead
A template of kind `part` with the code `letterhead` is the letterhead of a company and language (a template without a company serves
all companies; the company's own is preferred). Its text wraps the letter and shows it where `{{ content }}` stands. If no letterhead
exists for the language, the letter is printed without one.

## 7. Batches
`POST /api/jobs` for the job `kernel.doc_generate_batch` with the parameters `templateId` and `ownerIds` (up to 5000 records) makes one
document per record with the rights of the person who asked. A record that cannot be done is listed in the run log; the run ends
failed if any record failed. The run is never retried, so no document is made twice.
