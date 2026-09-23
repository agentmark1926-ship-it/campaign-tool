# CLAUDE.md — Email Campaign Tool

Single-user email campaign app: Blazor (Interactive Server) + MudBlazor, EF Core on Azure SQL Basic, Azure Communication Services Email, one in-process background worker. Max 20,000 emails/month. Not a SaaS.

**SPEC.md is the source of truth. Read it in full before writing code.** Every row in its "Decisions already made" table is final; do not ask about stack, structure, or scope. "Out of scope" means do not scaffold, stub, or leave TODOs for it.

## How to work

- Build Phases 1–7 from SPEC.md in order. A phase is done only when every acceptance line under it is true.
- Commit at the end of each phase with tests green: `Phase N: <what works now>`.
- After each phase, update `PROGRESS.md` (done / verified in Azure / waiting on the owner).
- Anything only the owner can provide (secrets, DNS, ACS quota, seed inboxes, Azure resources) goes in `BLOCKERS.md` as an exact ask. Do not stop: use the in-memory `IEmailSender` and a local SQL database and continue with the next unblocked task.
- If `az` is logged in, deploy `infra/main.bicep` yourself (`az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam`, secrets from env vars). Otherwise list what the owner must create in `BLOCKERS.md`.
- Prefer the simplest implementation that meets the acceptance criteria. No extra abstractions. `IEmailSender` is the one deliberate seam.
- If a named library is unavailable or broken, use the fallback named in SPEC.md and note it in `PROGRESS.md`.
- Never put a secret in the repo, a log line, or a test fixture. Local secrets: `dotnet user-secrets`. Azure: App Service settings (the Bicep sets them).

## Repository

```
src/CampaignTool.Web        Blazor app, endpoints, workers (see SPEC.md "Repository layout")
tests/CampaignTool.Tests    xUnit
infra/main.bicep            all Azure resources; main.bicepparam has the parameters
.github/workflows/deploy.yml
SPEC.md                     the spec
KICKOFF.md                  the first prompt to paste into Claude Code
PROGRESS.md / BLOCKERS.md   maintained by you
```

## Commands

```
dotnet build
dotnet test
dotnet run --project src/CampaignTool.Web
dotnet ef migrations add <PhaseN_Name> --project src/CampaignTool.Web
```

## Conventions

- .NET current LTS; nullable on; `TreatWarningsAsErrors` off.
- Configuration via `IOptions<T>` bound to the keys in SPEC.md "Configuration keys". Azure app settings use `__` for `:` (e.g. `Sending__MaxPerHour`).
- Entities are plain classes; services take `AppDbContext` directly; no repository layer.
- Store UTC; display in `App:TimeZone`.
- One structured log line for every state change on campaigns and contacts.
- Migrations named by phase: `Phase1_Initial`, `Phase3_Imports`, …

## Azure bootstrap order (owner + you)

1. Resource group; Entra app registration (redirect URI `https://<appName>-web.azurewebsites.net/.auth/login/aad/callback`); OIDC federated credential for GitHub Actions.
2. `az deployment group create … infra/main.bicep` with all flags false. Read the `dnsRecords` output → owner adds DNS records.
3. Owner files the ACS email quota request (portal → Help + support → quotas → "Azure Communication Services Email: Sending Limits").
4. Push to `main` → workflow deploys the app → `/health` is 200.
5. Redeploy with `linkDomain=true` once the domain shows Verified; then `createEventSubscription=true` (needs the app up to answer the handshake); then `createSenderUsername=true` after the quota is approved. Until then the sender is `DoNotReply@<domain>`.
6. Enable engagement (click) tracking on the ACS domain in the portal after the quota is approved.
