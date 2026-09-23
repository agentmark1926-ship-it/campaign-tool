# Progress

Maintained by Claude Code. One entry per phase.

| Phase | Status | Verified in Azure | Waiting on owner |
| --- | --- | --- | --- |
| 1 Foundation | code done, tests green (13) | no | Azure resources, Entra app, GitHub secrets (BLOCKERS 1–5) |
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

Not yet verified in Azure (acceptance lines still open): Entra sign-in in Azure, Bicep deployment, GitHub Actions deploy, `/health` 200 in Azure.

Where the code lives: branch `claude/campaign-tool-setup-7gh7yp`. It is not on `main` yet because a push to `main` runs the deploy, which fails until the GitHub secrets exist. Merge once BLOCKERS 1–5 are cleared.
