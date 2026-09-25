# Email Campaign Tool

A single-user email campaign app on Azure: Blazor + MudBlazor, Azure SQL Basic, Azure Communication Services Email, one in-process worker. About $25/month at 20,000 emails.

- `SPEC.md` — the full specification (architecture, data model, pipeline, phases, acceptance criteria)
- `CLAUDE.md` — standing instructions for Claude Code
- `KICKOFF.md` — the first prompt to paste into Claude Code
- `infra/main.bicep` — every Azure resource; `infra/main.bicepparam` — parameters
- `.github/workflows/deploy.yml` — build, test, deploy on push to `main`
- `.github/workflows/infra.yml` — redeploy the Bicep with the current secrets, or verify a database restore (run by hand)
- `PROGRESS.md` / `BLOCKERS.md` — what's built and what's waiting on the owner

## Run locally

```
dotnet user-secrets set "ConnectionStrings:Sql" "<local SQL connection string>" --project src/CampaignTool.Web
dotnet run --project src/CampaignTool.Web
```

Without ACS settings the app uses the in-memory email sender; sends are logged, not delivered. In Development the sign-in check is skipped when no App Service Authentication header is present.

## Test

Database tests need a SQL Server (LocalDB or a container). Point them at it without a database name; each test creates and drops its own:

```
export CAMPAIGNTOOL_TEST_SQL="Server=localhost,1433;User ID=sa;Password=<local password>;TrustServerCertificate=True"
dotnet test
```

## Deploy

A push to `main` builds, runs the tests against a throwaway SQL Server, publishes, deploys to `ashiwaju2-web` and waits for `/health`. Rollback: GitHub → Actions → deploy → open the last good run → **Re-run all jobs**.

Azure (resource group `rg-ashiwaju2-app`, Central US): Web App `ashiwaju2-web` (B1), SQL `ashiwaju-sql-…/campaigns` (Basic), Storage `ashiwaju…`, ACS `ashiwaju2-acs` + Email service `ashiwaju2-email`, Event Grid `ashiwaju2-acs-events`, App Insights `ashiwaju-ai`, alerts to the first allowed user.

# Runbook

Everything below is done from a browser: the Azure portal, GitHub, or the app. Nothing needs a local install.

## Redeploy the infrastructure without changing secrets

GitHub → **Actions** → **infra** → **Run workflow** (branch `main`, leave *Redeploy* ticked) → **Run workflow**. It reads the current secrets from the Web App's settings and reapplies `infra/main.bicep`, so no key rotates by accident. Use it after editing `infra/main.bicep` or `main.bicepparam`, and for the domain steps below.

## Rotate secrets

All live only in the Web App's settings (portal → `ashiwaju2-web` → **Settings → Environment variables**). Change one, click **Apply**; the app restarts in about 30 seconds.

| Setting | How to rotate | Side effects |
| --- | --- | --- |
| `Webhooks__AcsSecret` | Set a new random value (32+ characters), Apply, then run the **infra** workflow so the Event Grid subscription URL gets the same key. | Delivery reports arriving between the two steps are rejected with 401 and retried by Event Grid for 24 hours, so none are lost. |
| `Auth__UnsubscribeKey` | **Avoid.** Every unsubscribe link in every email already sent is signed with it; a new key makes those links show "Link not valid". Rotate only if it leaked, and expect replies from people who can't unsubscribe from old emails. | |
| `MICROSOFT_PROVIDER_AUTHENTICATION_SECRET` (sign-in) | Portal → **App registrations** → `ashiwaju` → **Certificates & secrets** → **New client secret**; paste the value into this setting; Apply; delete the old secret. | Everyone signs in again. |
| `Ai__AnthropicApiKey` | console.anthropic.com → **API keys** → create, paste here, Apply, then disable the old key. | |
| SQL admin password (inside `ConnectionStrings__Sql`) | Portal → SQL server `ashiwaju-sql-…` → **Reset password**; put the same password in the `Password=` part of `ConnectionStrings__Sql`; Apply. | A few seconds of failed requests while the two disagree. |
| GitHub deploy access | Nothing to rotate: GitHub signs in to Azure with a short-lived OIDC token (federated credentials `github-main` / `github-main-ids` on app `ashiwaju-deploy`). | |

## Raise the sending limits

1. Wait for Microsoft to approve the ACS email quota request, and note the granted per-minute and per-hour numbers.
2. App → **Settings** → **Sending limits**: set per-minute and per-hour at or below the granted numbers → **Save**. Takes effect on the next batch; no restart.
3. Click tracking: once you send from your own domain, once your domain is in `verifiedDomains`, run the **infra** workflow with **Create the extra MailFrom address** ticked; that also switches on engagement (click) tracking for the domain. (Turning it on in the portal instead would be switched off again by the next infra run.)

Never set the limits above what Microsoft granted: ACS returns 429s and the worker retries them on the 1 min / 5 min / 30 min / 2 h / 6 h backoff.

## Add a sending domain (and switch the From address)

The app can send from any domain that is verified in Azure; each verified domain appears in the **From** dropdown in Settings (the default) and on each campaign's Setup tab. The free Azure test domain always stays available.

1. In `infra/main.bicepparam`, add the domain to `senderDomains` (e.g. `'news.example.com'` or a separate domain you own) and push to `main`.
2. GitHub → Actions → **infra** → Run workflow. The run's output (`dnsRecords`) lists that domain's records: a verification TXT, an SPF TXT, two DKIM CNAMEs and a DMARC TXT.
3. Add them at the domain's DNS host. In Namecheap: Domain List → **Manage** → **Advanced DNS** → **Add new record**. The *Host* is the part before the domain (`@` for the domain itself, `selector1-azurecomm-prod-net._domainkey` for DKIM, `_dmarc` for DMARC). There must be exactly one SPF (`v=spf1`) record per host: merge with any existing one. Add an MX record (Namecheap's free email forwarding creates one) so replies and bounce checks work.
4. App → **Settings** → **Check DNS** with the domain: all rows green.
5. Portal → `ashiwaju2-email` → **Provision domains** → the domain → **Verify** until it shows **Verified**.
6. Add the domain to `verifiedDomains` in `infra/main.bicepparam`, push, and run **infra** again. It links the domain and sets the display name (`senderDisplayName`, "Self Storage Developers").
7. App → **Settings** → **From address** → pick `DoNotReply@<domain>` → **Save**. New campaigns and tests use it; a draft keeps its own choice on its Setup tab.
8. After the quota increase is approved, run **infra** with **Create the extra MailFrom address** ticked: `updates@<domain>` appears in the dropdown and click tracking switches on.
9. Send a test to your seed inboxes and check it isn't in spam before any campaign. A new domain has no reputation: warm it up (a few hundred a day, roughly doubling every few days) before large sends.

## Restore the database

Azure SQL Basic keeps 7 days of point-in-time backups.

- **Check restores work (do this once, and after big changes):** GitHub → Actions → **infra** → Run workflow with *Redeploy* unticked and **Restore the database to a scratch copy** ticked. It restores to `campaigns-restore-check`, confirms it's Online, and deletes it.
- **Recover data:** Portal → SQL database `campaigns` → **Restore** → choose a point in time → new name `campaigns-restored` → **Review + create** (Basic, about 10–20 minutes). Then either copy what you need from it, or make it the live database: portal → `ashiwaju2-web` → Environment variables → in `ConnectionStrings__Sql` change `Initial Catalog=campaigns` to `Initial Catalog=campaigns-restored` → Apply. The app applies any missing migrations on start. Delete the old database once you're sure.

## Alerts

Emailed to the first allowed user (`alertEmail` in `main.bicep`):

| Alert | Where it comes from | What to do |
| --- | --- | --- |
| Failed above 2% of a campaign's recipients | App check every 5 min → log alert | Open the campaign's Results, filter Failed, read the Note column. |
| Campaign Sending with no batch for 30 minutes (work due, rate limit not the cause) | App check → log alert | Check `/health`; restart the Web App from the portal if needed. Nothing is resent twice. |
| Unknown recipients | App check → log alert | Campaign monitor page → Unknown list. Retry only where a duplicate is acceptable. |
| Webhook 5xx above 5 in 5 minutes | App Insights requests | Portal → `ashiwaju2-web` → **Log stream**; Event Grid keeps retrying for 24 h. |
| CPU above 80% for 15 minutes | App Service plan metric | Usually a very large import; wait, or scale the plan up temporarily. |
| SQL above 80% of 2 GB | SQL metric | Lower `Retention__EventMonths`, or move to Standard S0. |

## Everyday fixes

- **Blank "HTTP ERROR 500" page, `/health` says healthy:** a stale sign-in session. Open `https://ashiwaju2-web.azurewebsites.net/.auth/logout`, then sign in again.
- **Nightly clean-up:** at 3 AM (`App__TimeZone`) raw delivery/click events older than `Retention__EventMonths` (12) and uploaded import files older than 30 days are deleted. Campaign counts are kept forever.
- **Someone asks to be removed by reply:** Suppressions → add their address (reason Unsubscribe). It's excluded from every future campaign, whatever list they're on.
- **Warm-up for the first real list:** day 1 seed inboxes only; day 2 the 1,000 most recent subscribers; day 4 the next 4,000; day 7 everyone. Stop if bounces exceed 2% or anything lands as FilteredSpam (the app pauses a campaign on its own above 0.5%).
