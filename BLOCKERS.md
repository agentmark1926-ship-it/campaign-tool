# Blockers

Exact asks for the owner. Claude Code appends here; the owner clears items and replies "done".

Claude Code runs in a cloud session that cannot reach Azure (`management.azure.com` is blocked), so the Azure steps below are yours. The easiest place to run them is **Azure Cloud Shell** (portal → the `>_` icon, Bash); it is already signed in and has `az` and Bicep.

## Values (done, committed to `infra/main.bicepparam`)

- [x] `appName` = `ashiwaju` → Web App `ashiwaju-web` (if Azure says the name is taken, tell me and I'll change it)
- [x] `allowedUsers` = `admin@self-storagedevelopers.com`
- [ ] Sending subdomain = `news.self-storagedevelopers.com` — **confirm**. You gave the root domain; the spec sends from a dedicated subdomain so campaign bounces or spam complaints can never hurt the reputation of your everyday Microsoft 365 mail on `self-storagedevelopers.com`, and so ACS's SPF/DKIM records don't touch your M365 ones. Reply "confirm" or name another subdomain.
- [x] Mailing address = Self Storage Developers, 1101 Brickell Ave., 8th Fl, South Tower, Miami, FL 33131 (change the company name in the params if the legal sender name differs)
- [x] Time zone = `America/Chicago`
- [ ] `entraClientId` — from step 1 below

## 1. Entra app registration for sign-in (Cloud Shell)

```bash
APP=ashiwaju
az ad app create --display-name "$APP" \
  --web-redirect-uris "https://$APP-web.azurewebsites.net/.auth/login/aad/callback" \
  --enable-id-token-issuance true --query appId -o tsv            # -> ENTRA_CLIENT_ID
az ad app credential reset --id <ENTRA_CLIENT_ID> --append --display-name easyauth --query password -o tsv   # -> ENTRA_CLIENT_SECRET (keep it; do not send it to me)
```

- [ ] Reply with the ENTRA_CLIENT_ID (not secret). Keep the secret for step 2.

## 2. Deploy the infrastructure (Cloud Shell, after the params are committed)

```bash
git clone https://github.com/agentmark1926-ship-it/campaign-tool && cd campaign-tool
git checkout claude/campaign-tool-setup-7gh7yp
az group create -n rg-ashiwaju -l eastus2
export SQL_ADMIN_PASSWORD="$(openssl rand -base64 24)Aa1!" \
       ENTRA_CLIENT_SECRET='<from step 1>' \
       WEBHOOK_SECRET="$(openssl rand -hex 32)" \
       UNSUBSCRIBE_KEY="$(openssl rand -hex 32)"
az deployment group create -g rg-ashiwaju -f infra/main.bicep -p infra/main.bicepparam \
  --query properties.outputs -o json
```

- [ ] Deployment succeeded. Paste me the `outputs` block (it contains no secrets: names, URL and the DNS records). The secrets live only in App Service settings; you do not need to save them elsewhere (`WEBHOOK_SECRET` is needed again only when `createEventSubscription` is flipped — re-read it with `az webapp config appsettings list`).

## 3. GitHub Actions deploy identity (Cloud Shell)

```bash
SUB=$(az account show --query id -o tsv); TENANT=$(az account show --query tenantId -o tsv)
DEPLOY_ID=$(az ad app create --display-name ashiwaju-deploy --query appId -o tsv)
az ad sp create --id "$DEPLOY_ID"
az ad app federated-credential create --id "$DEPLOY_ID" --parameters '{"name":"github-main","issuer":"https://token.actions.githubusercontent.com","subject":"repo:agentmark1926-ship-it/campaign-tool:ref:refs/heads/main","audiences":["api://AzureADTokenExchange"]}'
az role assignment create --assignee "$DEPLOY_ID" --role Contributor --scope "/subscriptions/$SUB/resourceGroups/rg-ashiwaju"
echo "AZURE_CLIENT_ID=$DEPLOY_ID AZURE_TENANT_ID=$TENANT AZURE_SUBSCRIPTION_ID=$SUB"
```

- [ ] GitHub → campaign-tool → Settings → Secrets and variables → Actions: secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`; variable `AZURE_WEBAPP_NAME` = `ashiwaju-web`

## 4. First deploy

- [ ] Merge branch `claude/campaign-tool-setup-7gh7yp` into `main` (open a PR or ask me to). The workflow builds, tests, deploys and checks `/health`. Then open `https://ashiwaju-web.azurewebsites.net`, sign in, and confirm you see the dashboard.

## Long-pole items (start now, needed from Phase 2 on)

- [ ] ACS Email quota request filed (Azure portal → Help + support → Service and subscription limits → "Azure Communication Services Email: Sending Limits"; ask for 30/min, 2,000/hour, 10,000/day; consented newsletter subscribers, ~10k contacts, 1–2 sends/month)
- [ ] DNS records from the `dnsRecords` output added at your DNS host, plus `_dmarc.<subdomain>` TXT `v=DMARC1; p=none; rua=mailto:dmarc@<yourdomain>` and an MX for the subdomain
- [ ] Seed inboxes on Outlook, Gmail, Yahoo, iCloud (send me the addresses)
- [ ] Subscriber CSV available (email column required, everything else optional) — keep it out of the repo
