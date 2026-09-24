# Progress

Maintained by Claude Code. One entry per phase.

| Phase | Status | Verified in Azure | Waiting on owner |
| --- | --- | --- | --- |
| 1 Foundation | done | deployed, `/health` 200, owner signed in and saw the dashboard (2026-09-23) | Azure resources, Entra app, GitHub secrets (BLOCKERS 1–5) |
| 2 ACS and events | done | test send from Settings reached the owner's inbox; Delivered report stored and shown (2026-09-23) | |
| 3 Contacts | done in code; deployed (run 35931498833) | deployed | import the real subscriber file and confirm counts |
| 4 Templates and editor | done (owner's HTML / plain text + AI writer design) | AI draft and test email verified in the owner's Outlook inbox (2026-09-24) | |
| 5 Campaigns and sending | done | deployed (run 35968052750) | seed campaign to your own addresses |
| 6 Results and unsubscribe | done | deployed (run 35969339165) | unsubscribe from a seed inbox; check Delivered counts |
| 7 Hardening | done | deployed (run 35971561367); infra workflow run 35981697117 created the alerts and verified point-in-time restore (2026-09-24) | |

## Phase 1 — Foundation

Done and verified locally:

- Solution `CampaignTool.slnx`: `src/CampaignTool.Web` (.NET 10, Blazor Interactive Server, MudBlazor 9) and `tests/CampaignTool.Tests` (xUnit).
- MudBlazor shell (app bar, nav drawer) and a dashboard showing contacts by status. Nav items are added as their pages land.
- `Phase1_Initial` migration creates all ten tables from the data model with the spec's unique keys and indexes; enums are stored as strings so the raw claim query matches the spec. Migrations apply at startup.
- `AllowedUsersMiddleware` reads the UPN App Service Authentication injects (`X-MS-CLIENT-PRINCIPAL-NAME`) and returns 403 for anyone not in `Auth:AllowedUsers`; 401 if no user reached the app (auth not configured). `/health`, `/unsubscribe/*`, `/webhooks/*` are anonymous. In Development with no header the check is skipped so the app runs locally.
- `/health` returns 200 when the database is reachable, 503 otherwise (also used as the App Service health check).
- All configuration sections from the spec bound through `IOptions<T>`.
- Application Insights is registered only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set (the Bicep sets it); App Insights 3.x refuses to start without it.
- `infra/main.bicep` and `main.bicepparam` compile with the Bicep CLI (`bicep build`). Not deployed: this cloud session has no route to `management.azure.com`.
- `deploy.yml`: added a step that starts a throwaway SQL Server container (random per-run password, masked) so the database tests run in CI. Build → test → publish verified locally in Release.

Tests: health anonymous 200; no user 401; allowed users (case-insensitive) 200; other user 403; public paths skip the check; migration creates every table; allowed-user list parsing.

Azure deployment (owner ran `infra/main.bicep` from Cloud Shell, 2026-09-23):

| Output | Value |
| --- | --- |
| Resource group | `rg-ashiwaju-app` (Central US) |
| Web App | `ashiwaju-web` — https://ashiwaju-web.azurewebsites.net |
| SQL server | `ashiwaju-sql-7llp2n6niyjp4.database.windows.net` (db `campaigns`) |
| Storage | `ashiwaju7llp2n6niyjp4` |
| ACS / Email service | `ashiwaju-acs` / `ashiwaju-email` |
| Sender domain | `self-storagedevelopers.com` (root, build phase only) |

DNS records from the `dnsRecords` output: TXT `ms-domain-verification=36207cd6-f843-4774-8f3e-6dcfbfde3e24` at the root; SPF `v=spf1 include:spf.protection.outlook.com -all` (same include as Microsoft 365, so the existing SPF record already covers it — do not add a second SPF record); CNAME `selector1-azurecomm-prod-net._domainkey` → `selector1-azurecomm-prod-net._domainkey.azurecomm.net`; CNAME `selector2-azurecomm-prod-net._domainkey` → `selector2-azurecomm-prod-net._domainkey.azurecomm.net`.

GitHub Actions deploy: run 35923743337 (attempt 2) built, ran 13 tests against a SQL container, deployed to `ashiwaju-web`, and `/health` returned 200 after ~2.5 minutes of first start (migrations). Attempt 1 failed at `azure/login`: GitHub now sends an ID-based OIDC subject (`repo:agentmark1926-ship-it@329644780/campaign-tool@1383551704:ref:refs/heads/main`), so a second federated credential `github-main-ids` with that subject was added to the `ashiwaju-deploy` app registration.

Owner signed in at https://ashiwaju-web.azurewebsites.net and saw the dashboard. Phase 1 complete.

Where the code lives: branch `claude/campaign-tool-setup-7gh7yp`. It is not on `main` yet because a push to `main` runs the deploy, which fails until the GitHub secrets exist. Merge once BLOCKERS 1–5 are cleared.

## Phase 2 — ACS and events

Done and verified locally:

- `IEmailSender` with `AcsEmailSender` (Azure.Communication.Email 1.1.0, `SendAsync(WaitUntil.Started)`, one message per recipient; 429/5xx/timeouts reported as transient, other 4xx as permanent) and `InMemoryEmailSender` (used when `Acs:ConnectionString` is empty).
- Settings page (`/settings`): reply-to, mailing address, time zone, per-minute and per-hour limits; saved to the `Settings` table and overriding App Service configuration; validation messages in one sentence. The From address is shown read-only from `Acs:SenderAddress` because ACS only sends from a MailFrom address provisioned on the domain (it has no per-message display name), so an editable From would only produce failed sends.
- Test send from Settings: up to five addresses, `[TEST]` subject, mailing-address footer, no recipient rows, never counted.
- `POST /webhooks/acs?key=…`: constant-time key check, Event Grid validation handshake, every delivery/engagement event stored once in `EmailEvents` (unique `EventId`, duplicates acknowledged), events for unknown messages stored and logged, matched to a recipient by `AcsMessageId` when one exists. Recipient status and campaign counter updates are Phase 6.
- Settings page shows the 10 most recent delivery reports so the owner can see the Delivered event arrive.
- `EmailRules` (normalize + syntax-only validation per spec) added now because test sends need it; Phase 3 import reuses it.
- Bicep: free Azure-managed test domain (`AzureManagedDomain`, `DoNotReply@<id>.azurecomm.net`) linked to ACS and used as the sender until `linkDomain=true`, so sending can be tested before the SiteGround DNS records exist.
- Fixed: error responses from the webhook now carry a body so the status-code page middleware doesn't rewrite 401 into a 400.

Tests (27 new): email normalization/validation; webhook key rejection, handshake, stored-once delivery event, click URL, ignored event types; settings defaults/save/reload/validation; test email fan-out, footer, and rejection of >5, invalid, and empty input.

Deployed (run 35928080845). Owner ran the Cloud Shell block 2026-09-23: test domain linked, sender `DoNotReply@e93c6f02-275f-4e7b-ae0c-b5cd4cd5540f.azurecomm.net`, Event Grid subscription `email-events` created (the validation handshake against `/webhooks/acs` passed). Owner then sent a test from Settings: it landed in the Outlook inbox (not junk) and the Delivered report appeared under Recent delivery reports. Phase 2 complete. (Earlier note: the Cloud Shell block in BLOCKERS "Phase 2" (test domain, sender setting, Event Grid subscription), then a test send from Settings to a seed inbox with the Delivered report visible within 2 minutes — done.)

Not built yet (Settings items the spec puts under deliverability): "Check DNS" button — planned with the domain switch.

## Phase 3 — Contacts

Done and verified locally (tests plus a browser walk-through of the import wizard, grid, lists and CSV downloads):

- Contacts grid (`/contacts`): search by email or name, filter by status and list, server-side paging, bulk add-to-list / unsubscribe / delete with one confirmation, CSV export of the current filter.
- Contact page (`/contacts/{id}`): name fields, custom fields, lists (add/remove), status with reason and date, suppression flag, Unsubscribe and the deliberate Re-subscribe (refused while the address is suppressed), campaign history.
- Lists (`/lists`): create, rename, delete (refused while an unfinished campaign targets the list); members and sendable count (Subscribed and not suppressed).
- Suppressions (`/suppressions`): search, add with reason, remove; removing never re-subscribes.
- Import wizard (`/contacts/import`): Upload (.csv/.txt, 50 MB) → Map (skipped for one-column files) → Preview counts → optional add-to-list (existing or new) → worker import with a progress bar polled every 2 s → Summary with rejects CSV.
- Detection: comma/semicolon/tab, header row, UTF-8 BOM, quoted fields; email column by the 90% rule, falling back to a header named Email/E-mail (the browser walk-through caught a small file whose 80% valid email column wasn't recognised).
- Import rules: normalize, syntax-only validation, in-file duplicates skipped, existing contacts get non-empty fields merged (`Import:UpdateExisting`), Status never changed, suppressed addresses imported with the matching non-subscribed status, batches of 1,000 with one SaveChanges per batch.
- Imports run in `CampaignWorker` (the spec's single worker; campaign sending joins it in Phase 5), claimed atomically so a second process can't double-run one.
- Uploaded files go to the private `imports` blob container (local temp folder when `ConnectionStrings:Storage` is empty). The 30-day deletion is the retention job (Phase 7).
- CSV exports prefix `=`, `+`, `-`, `@` cells with an apostrophe.
- Migration `Phase3_Imports` (Delimiter, HasHeader, MappingJson, index on Status).

Tests (16 new): CSV detection (one column with/without header, semicolon, tab, quoted fields, Excel BOM + CRLF, header fallback, default mapping), export injection, 10,000-row one-column import under 2 minutes with exact Imported/Skipped/Invalid counts, re-import updates fields but never status, suppressed address stays unsubscribed, multi-column mapping with custom fields and a new list, rejects with row numbers, unsubscribe/resubscribe/suppression rules, sendable count.

Waiting on the owner: merge to `main`, then import the real subscriber file and check the counts.

## Phase 4 — Templates and editor

Done and verified locally:

- Templates page (`/templates`): card grid with live thumbnails (the HTML rendered as the sample contact in a sandboxed, scaled iframe), new, duplicate, delete (campaigns keep their own copy, so deleting a template never changes a campaign).
- Template editor (`/templates/{id}`): Unlayer embedded through `wwwroot/js/editor.js` (init, load design, export HTML — the only JavaScript in the app) in a reusable `EmailEditor` component that Phase 5's campaign editor will reuse; design JSON and exported HTML saved together; Merge Tags menu offers first name (with the "there" fallback), last name and email.
- Image uploads from the editor go to `POST /assets/images` → public `email-assets` blob container with a one-year immutable cache header (local temp folder + `/assets/` route when no storage is configured).
- `TemplateRenderer`: Scriban in Liquid syntax, `{{ first_name | default: "there" }}`; every contact value HTML-encoded in HTML (subject rendered as plain text); unknown fields render empty; custom fields exposed as snake_case (`Unit Size` → `unit_size`); `&quot;` inside tags (how visual editors store quotes) is decoded before parsing; parse errors are reported on save.
- Preview tab: desktop (640 px) and mobile (375 px) widths, as the named sample, the email-only sample, or any contact by email.
- Send test from the editor: up to five addresses; merge fields rendered per address with that contact's data (email-only when the address isn't a contact); mailing-address footer; never counted.
- Tabs keep the editor mounted (`KeepPanelsAlive`) so switching to Preview or Send test never loses unsaved edits.

Verification note: this build environment cannot reach editor.unlayer.com (outbound policy), so the browser walk-through ran with a stand-in for the Unlayer script that implements the same calls (createEditor, loadDesign, exportHtml, registerCallback). It confirmed: create → edit → save → preview shows "Hi Ann" for the named sample and "Hi there" for the email-only sample → test send accepted → reload restores the saved design, with no circuit errors; and that a blocked editor script shows a one-sentence error instead of a blank page. **The real Unlayer editor loading in the owner's browser is the open acceptance check.** If Unlayer's free embed turns out to be unavailable, the spec's fallback (GrapesJS newsletter preset) replaces only `editor.js`.

Tests (8 new): name renders, fallback for email-only, HTML encoding of every value, plain-text subject, unknown field empty, custom field in snake_case, editor-encoded quotes, broken tag reported.

### Phase 4 change (owner decision, 2026-09-23): no drag-and-drop editor

The owner reviewed the Unlayer editor and asked instead for templates that are either raw HTML or plain text, plus an AI that can write either. This replaces SPEC "Decisions already made → Editor" (Unlayer / GrapesJS):

- `Templates.Format` (`Html` | `Text`) and `Templates.Text` added (migration `Phase4_TemplateFormat`; existing rows default to `Html`). `DesignJson` is no longer written.
- Editor page: format toggle, a monospace HTML box or a plain-text box, live desktop/mobile preview with merge fields, image upload that returns a public URL to paste into `<img src>`, and the test send.
- Plain-text letters are sent with both parts: the text as `PlainText` and a simple, encoded HTML rendition (paragraphs, line breaks, clickable links). Footer added to both.
- AI writer (`AiEmailWriter`): Anthropic C# SDK 12.50, model `claude-opus-5`, server-side refusal fallback (`fallbacks: "default"`, beta `server-side-fallback-2026-07-01`). Writes a new email or revises the current one from a description; the system prompt enforces email-safe HTML (table layout, inline CSS, no scripts), Liquid merge fields with the "there" fallback, no footer/unsubscribe (added at send), and bracketed placeholders instead of invented facts. Roughly $0.05–0.10 per draft at $5/$25 per million tokens. Disabled with a one-line explanation when `Ai:AnthropicApiKey` is empty.
- Removed: `wwwroot/js/editor.js`, the `EmailEditor` component, the editor's image-upload endpoint. The app now has no custom JavaScript.
- Bicep: `anthropicApiKey` secure parameter → `Ai__AnthropicApiKey` app setting (from `ANTHROPIC_API_KEY` in `main.bicepparam`), so a later infrastructure redeploy keeps the key.

Tests (7 new): the AI request (model, fallbacks, HTML vs text rules, revise includes the current body), code-fence stripping, refusal message, missing-key message; plain-text rendering and the two-part test send; template format save/validate.

## Phase 5 — Campaigns and sending

Done and verified locally (tests, plus a browser run: import 40 contacts into a list → new campaign → stepper → Send now → the monitor went 0% → 62.5% without a reload → Pause):

- Campaigns list (`/campaigns`): name, status, list, scheduled/sent date, recipients, delivered %, clicks; duplicate.
- Campaign editor (`/campaigns/{id}/edit`), drafts only: 1 Setup (name, subject with merge fields, preheader, From shown read-only, reply-to) → 2 Content (start from a template — copied, not linked — then the shared HTML/plain-text editor with the AI writer and preview) → 3 Recipients (list, exclude lists, live sendable count) → 4 Test (up to five addresses) → 5 Send now (confirmation with the count) or Schedule (date/time in `App:TimeZone`). Readiness is checked before either: mailing address, unsubscribe configuration, subject, content, reply-to, list, at least one sendable contact.
- Materialization (`CampaignService.MaterializeAsync`): one INSERT…SELECT — in the list, not in an excluded list (`OPENJSON`), Subscribed, not suppressed, not already a recipient; the unique (CampaignId, ContactId) index backs it up.
- Worker (`CampaignSender`, run by `CampaignWorker`): stale-claim recovery and the scheduler every 15 s; the spec's `UPDATE TOP (n) … OUTPUT` claim; sliding-window rate limiter (never more than MaxPerMinute in any 60 s or MaxPerHour in any hour, every attempt counted, seeded from the last hour of sends at startup); one campaign at a time, oldest start first; pause/cancel checked before every message (a pause returns claimed rows to the queue, a cancel cancels them); consent re-checked at send time (unsubscribed or suppressed since scheduling → Cancelled); counters updated with atomic increments; `CampaignProgress` raised after every batch.
- Retries: 429/5xx/timeouts retry after 1 min, 5 min, 30 min, 2 h, 6 h — the first send plus five retries; the sixth failure is Failed. A 4xx rejection is Failed at once and the contact becomes Invalid. (The spec lists five waits and also says "Failed after five attempts"; this implementation uses all five waits.)
- Crash recovery: Claimed rows older than 10 minutes become Unknown and are never resent; the monitor lists them with an explicit Retry.
- Each email: merge fields per contact, hidden preheader, footer with the mailing address and an HMAC-signed unsubscribe link, `List-Unsubscribe` and `List-Unsubscribe-Post: List-Unsubscribe=One-Click` headers, reply-to; plain-text campaigns send a text part plus an HTML rendition. (The /unsubscribe page itself is Phase 6.)
- Monitor (`/campaigns/{id}`): progress bar and counters that re-render on each batch without polling, Pause / Resume / Cancel (confirmed), Unschedule-and-edit for scheduled campaigns, Unknown rows with Retry, latest 50 events.
- Dashboard: campaigns sending now with live progress, the last five campaigns, contacts by status.
- Migration `Phase5_Campaigns` (Format, Text, LastBatchAtUtc on Campaigns; existing rows default to Html).
- Template editor: the test subject now follows the template name until you edit it (owner saw "Untitled template" in a test subject).

Tests (14 new): every allowed state transition passes and every other throws; retry schedule; rate limiter never exceeds either window over three simulated hours and uses the full allowance; next-slot calculation; unsubscribe token round trip and tamper rejection; materialization exclusions and the unique index; 60 recipients sent once each at ≤25/min with footer, unsubscribe link and headers; pause/resume/cancel mid-send; a crashed claim becomes Unknown and is never resent; transient backoff then Failed, permanent rejection → Invalid; unsubscribe after scheduling → not sent; send refused without a subject.

## Phase 6 — Results and unsubscribe

Done and verified locally (tests, plus a browser run: 40-recipient campaign → the public unsubscribe page → "You're unsubscribed" → results show Unsubscribed 1 and the send times show the 25/minute limit):

- Webhook now applies each event once, in the same transaction as storing it: Delivered → recipient Delivered + counter (and resets soft bounces); Bounced → Bounced + counter, contact Bounced, address suppressed (HardBounce); ACS `Suppressed` → Failed and treated as a hard bounce; `Failed` → Failed + counter and a soft bounce, the third in a row becoming a hard bounce; `FilteredSpam` / `Quarantined` → Failed, and FilteredSpam above 0.5% of Sent pauses the campaign (the spec says "stop"; pausing keeps the choice with the owner). Only rows still `Sent` move, so a late or duplicate event can't undo an outcome. Clicks: every click stored with its URL, `ClickCount` on every click, `Clicked` once per recipient. Opens stored only. The monitor and dashboard re-render when events arrive.
- Unsubscribe (`/unsubscribe/{token}`): public, plain server-rendered HTML (no app layout, no sign-in, no interactive circuit — App Service Authentication only exempts this path), GET shows one button, POST (the button or a mail client's RFC 8058 one-click request) sets Unsubscribed, adds an Unsubscribe suppression and increments the campaign's `Unsubscribed` once; idempotent; works if the campaign was deleted; a tampered token gets a plain error page (400).
- Results page (`/campaigns/{id}/results`, opened from the list for completed/cancelled campaigns and from the monitor): Sent, Delivered, Bounced, Failed, Clicked, Unsubscribed with % of Sent; clicks by link (people and clicks); recipients filterable by status with paging; recipient CSV export (`/campaigns/{id}/recipients.csv`, behind sign-in, formula-safe).
- The browser run caught a per-link query EF Core couldn't translate; fixed, and a test now renders the results page with click data.

Tests (7 new): each delivery status → recipient/counter/contact/suppression, duplicate EventId ignored, late contradictory event ignored; third soft bounce → hard; click counted once per recipient with every click kept, and the results page lists the links; FilteredSpam pause; unsubscribe page, button, one-click POST, no double count, tampered token, next campaign excludes the unsubscribed; unsubscribe after the campaign is deleted; recipient export is behind sign-in and has every row.

## Phase 7 — Hardening

Done and verified locally:

- `RetentionWorker` (the spec's nightly worker): at 3 AM in `App:TimeZone` deletes raw `EmailEvents` older than `Retention:EventMonths` (in batches of 5,000) and uploaded import files older than 30 days (blob or local); campaign counters untouched. Every 5 minutes it also runs the alert checks.
- `AlertMonitor`: writes an `ALERT …` warning for recipient Failed above 2% of Recipients (campaigns sending, paused, or finished in the last 7 days), a campaign in Sending with no batch for 30 minutes **while work is due and the rate limit allows sending** (waiting out the hourly cap is normal and doesn't alert), and Unknown rows above 0.
- Azure Monitor in `infra/main.bicep`: action group emailing `alertEmail` (default: the first allowed user); log alert on App Insights traces containing "ALERT"; log alert on webhook 5xx above 5 in 5 minutes; metric alerts on App Service CPU above 80% for 15 minutes and SQL storage above 80%.
- `.github/workflows/infra.yml` (manual): redeploys the Bicep reading the existing secrets from the Web App settings (so the unsubscribe key, webhook key, sign-in secret, SQL password and AI key never rotate by accident), with switches for the custom domain and the extra MailFrom address, and an optional point-in-time restore check (restore to a scratch copy 15 minutes back, confirm Online, delete). This is a second workflow beyond the spec's one: the Cloud Shell route proved fragile (ephemeral sessions, token expiry, secrets regenerated on every run).
- Settings → **Check DNS**: resolves the verification TXT, SPF (exactly one record, including spf.protection.outlook.com), both DKIM CNAMEs, DMARC and MX for a domain, with pass/fail per row (DnsClient 1.8). Not exercised here: this build environment has no outbound DNS; the owner runs it in the app.
- Bicep: click (engagement) tracking on the custom domain follows `createSenderUsername` (both need the quota increase), so a redeploy can't switch off tracking turned on elsewhere.
- README runbook: redeploy without rotating secrets, rotate each secret (and why not to rotate the unsubscribe key), raise sending limits, switch to the custom domain and add a sender address, verify and perform a database restore, what each alert means, the stale sign-in fix, retention, reply-based removals, warm-up.

Tests (3 new): retention deletes only old events and old import files and keeps counters; alerts fire for failed share and Unknown rows but not healthy campaigns; the stall alert fires only when work is due and the limiter allows sending.

Waiting on the owner: merge; then run GitHub → Actions → **infra** once with *Redeploy* and *verify restore* ticked (or ask Claude Code to start it) — that creates the alerts and proves the restore.

Verified in Azure: the **infra** workflow (run 35981697117, started from chat on the owner's request) redeployed `infra/main.bicep` with the secrets read from the Web App — the action group, both log alerts and both metric alerts now exist and the Event Grid subscription is managed by the Bicep — then restored `campaigns` to `campaigns-restore-check` as of 15 minutes earlier, confirmed it Online, and deleted it (3.5 minutes).

## After Phase 7 — choose the sending domain in the app

The owner asked to switch sending domains without Azure work each time (they own several domains at Namecheap).

- Bicep: `senderDomain` / `linkDomain` replaced by `senderDomains` (registered in Azure; `dnsRecords` output now lists each domain's records) and `verifiedDomains` (linked alongside the always-linked test domain). `Acs__SenderAddresses` lists every usable MailFrom address; the default is the first verified domain, else the test domain. Verified domains get the display name `senderDisplayName` ("Self Storage Developers") on DoNotReply. The infra workflow's *custom domain* switch is gone: the domain lists live in `main.bicepparam`.
- App: Settings → **From address** is a dropdown of those addresses (saved choice ignored if its domain is later unlinked); each campaign's Setup tab has its own From dropdown, validated on save and again before sending; test sends and campaign sends pass the chosen address to ACS.
- README: "Add a sending domain" with Namecheap Advanced DNS steps.

Tests (2 new, 97 total): switching the Settings sender to a linked domain (and rejecting an unknown one) with the test email sent from it; a campaign sends from the address picked on its Setup tab and rejects one not in Azure.

## MVP status

All seven phases are built, tested (95 tests) and deployed. The spec's MVP definition also needs one real campaign to the day-2 warm-up slice with bounces under 2%, which waits on the owner items in BLOCKERS.md: seed test, subscriber import, custom (sub)domain, ACS quota.
