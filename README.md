# Email Campaign Tool

A single-user email campaign app on Azure: Blazor + MudBlazor, Azure SQL Basic, Azure Communication Services Email, one in-process worker. About $25/month at 20,000 emails.

- `SPEC.md` — the full specification (architecture, data model, pipeline, phases, acceptance criteria)
- `CLAUDE.md` — standing instructions for Claude Code
- `KICKOFF.md` — the first prompt to paste into Claude Code
- `infra/main.bicep` — every Azure resource; `infra/main.bicepparam` — parameters
- `.github/workflows/deploy.yml` — build, test, deploy on push to `main`

## Run locally

```
dotnet user-secrets set "ConnectionStrings:Sql" "<local SQL connection string>" --project src/CampaignTool.Web
dotnet run --project src/CampaignTool.Web
```

Without ACS settings the app uses the in-memory email sender; sends are logged, not delivered.

## Deploy

See the bootstrap order in `CLAUDE.md`. Runbook (rotate secrets, raise sending limits, add a sender, restore the database) is written by Claude Code in Phase 7.
