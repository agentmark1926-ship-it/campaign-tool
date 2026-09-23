# Progress

Maintained by Claude Code. One entry per phase.

| Phase | Status | Verified in Azure | Waiting on owner |
| --- | --- | --- | --- |
| 1 Foundation | done | deployed, `/health` 200, owner signed in and saw the dashboard (2026-09-23) | Azure resources, Entra app, GitHub secrets (BLOCKERS 1–5) |
| 2 ACS and events | done | test send from Settings reached the owner's inbox; Delivered report stored and shown (2026-09-23) | |
| 3 Contacts | done in code; deployed (run 35931498833) | deployed | import the real subscriber file and confirm counts |
| 4 Templates and editor | reworked per owner: HTML / plain text + AI writer; tests green (71) | not yet | merge; add the Anthropic API key; test send from the editor |
| 5 Campaigns and sending | not started | | |
| 6 Results and unsubscribe | not started | | |
| 7 Hardening | not started | | |

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
