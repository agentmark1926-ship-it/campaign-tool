# Progress

Maintained by Claude Code. One entry per phase.

| Phase | Status | Verified in Azure | Waiting on owner |
| --- | --- | --- | --- |
| 1 Foundation | done | deployed, `/health` 200, owner signed in and saw the dashboard (2026-09-23) | Azure resources, Entra app, GitHub secrets (BLOCKERS 1–5) |
| 2 ACS and events | code done, tests green (40) | deployed; test domain linked; Event Grid handshake passed | test send to an inbox + Delivered report |
| 3 Contacts | not started | | |
| 4 Templates and editor | not started | | |
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

Deployed (run 35928080845). Owner ran the Cloud Shell block 2026-09-23: test domain linked, sender `DoNotReply@e93c6f02-275f-4e7b-ae0c-b5cd4cd5540f.azurecomm.net`, Event Grid subscription `email-events` created (the validation handshake against `/webhooks/acs` passed). Remaining: the Cloud Shell block in BLOCKERS "Phase 2" (test domain, sender setting, Event Grid subscription), then a test send from Settings to a seed inbox with the Delivered report visible within 2 minutes.

Not built yet (Settings items the spec puts under deliverability): "Check DNS" button — planned with the domain switch.
