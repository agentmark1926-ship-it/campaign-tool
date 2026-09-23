# Progress

Maintained by Claude Code. One entry per phase.

| Phase | Status | Verified in Azure | Waiting on owner |
| --- | --- | --- | --- |
| 1 Foundation | code done, tests green (13) | infra deployed; app not yet deployed | Azure resources, Entra app, GitHub secrets (BLOCKERS 1–5) |
| 2 ACS and events | not started | | |
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

Still open for Phase 1: GitHub Actions deploy identity and secrets, first deploy, `/health` 200 in Azure, Entra sign-in verified in Azure.

Where the code lives: branch `claude/campaign-tool-setup-7gh7yp`. It is not on `main` yet because a push to `main` runs the deploy, which fails until the GitHub secrets exist. Merge once BLOCKERS 1–5 are cleared.
