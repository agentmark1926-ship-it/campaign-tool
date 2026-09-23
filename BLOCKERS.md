# Blockers

Exact asks for the owner. Claude Code appends here; the owner clears items and replies "done".

Claude Code runs in a cloud session that cannot reach Azure (`management.azure.com` is blocked), so the Azure steps below are yours. The easiest place to run them is **Azure Cloud Shell** (portal → the `>_` icon, Bash); it is already signed in and has `az` and Bicep.

## Values (done, committed to `infra/main.bicepparam`)

- [x] `appName` = `ashiwaju` → Web App `ashiwaju-web` (if Azure says the name is taken, tell me and I'll change it)
- [x] `allowedUsers` = `admin@self-storagedevelopers.com`
- [x] Sending domain = `self-storagedevelopers.com` (owner's choice for the build phase). **Before the first real campaign**, switch to a subdomain such as `news.self-storagedevelopers.com` so campaign bounces and complaints cannot affect everyday Microsoft 365 mail on the root domain.
- [x] Mailing address = Self Storage Developers, 1101 Brickell Ave., 8th Fl, South Tower, Miami, FL 33131 (change the company name in the params if the legal sender name differs)
- [x] Time zone = `America/Chicago`
- [x] `entraClientId` = `96676af7-caee-4824-8e26-a5451355fa03`

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
az group create -n rg-ashiwaju-app -l centralus
export SQL_ADMIN_PASSWORD="$(openssl rand -base64 24)Aa1!" \
       ENTRA_CLIENT_SECRET='<from step 1>' \
       WEBHOOK_SECRET="$(openssl rand -hex 32)" \
       UNSUBSCRIBE_KEY="$(openssl rand -hex 32)"
az deployment group create -g rg-ashiwaju-app -f infra/main.bicep -p infra/main.bicepparam \
  --query properties.outputs -o json
```

- [x] Deployment succeeded (2026-09-23, resource group `rg-ashiwaju-app`, Central US; East US 2 has 0 B1 quota on this subscription and the first Central US group hit an App Service capacity shortage). Outputs recorded in PROGRESS.md.
- [x] (original ask) Paste me the `outputs` block (it contains no secrets: names, URL and the DNS records). The secrets live only in App Service settings; you do not need to save them elsewhere (`WEBHOOK_SECRET` is needed again only when `createEventSubscription` is flipped — re-read it with `az webapp config appsettings list`).

## 3. GitHub Actions deploy identity (Cloud Shell)

```bash
SUB=$(az account show --query id -o tsv); TENANT=$(az account show --query tenantId -o tsv)
DEPLOY_ID=$(az ad app create --display-name ashiwaju-deploy --query appId -o tsv)
az ad sp create --id "$DEPLOY_ID"
az ad app federated-credential create --id "$DEPLOY_ID" --parameters '{"name":"github-main","issuer":"https://token.actions.githubusercontent.com","subject":"repo:agentmark1926-ship-it@329644780/campaign-tool@1383551704:ref:refs/heads/main","audiences":["api://AzureADTokenExchange"]}'
az role assignment create --assignee "$DEPLOY_ID" --role Contributor --scope "/subscriptions/$SUB/resourceGroups/rg-ashiwaju-app"
echo "AZURE_CLIENT_ID=$DEPLOY_ID AZURE_TENANT_ID=$TENANT AZURE_SUBSCRIPTION_ID=$SUB"
```

- [x] GitHub → campaign-tool → Settings → Secrets and variables → Actions: secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`; variable `AZURE_WEBAPP_NAME` = `ashiwaju-web`

## 4. First deploy

- [x] Merged to `main`; deployed and healthy. **Remaining:** sign in and confirm the dashboard.
- [x] (original ask) Merge branch `claude/campaign-tool-setup-7gh7yp` into `main` (open a PR or ask me to). The workflow builds, tests, deploys and checks `/health`. Then open `https://ashiwaju-web.azurewebsites.net`, sign in, and confirm you see the dashboard.

## Long-pole items (start now, needed from Phase 2 on)

- [ ] ACS Email quota request filed (Azure portal → Help + support → Service and subscription limits → "Azure Communication Services Email: Sending Limits"; ask for 30/min, 2,000/hour, 10,000/day; consented newsletter subscribers, ~10k contacts, 1–2 sends/month)
- [ ] DNS records from the `dnsRecords` output added at your DNS host, plus `_dmarc.<subdomain>` TXT `v=DMARC1; p=none; rua=mailto:dmarc@<yourdomain>` and an MX for the subdomain
- [ ] Seed inboxes on Outlook, Gmail, Yahoo, iCloud (send me the addresses)
- [ ] Subscriber CSV available (email column required, everything else optional) — keep it out of the repo

## Phase 2: test domain and delivery events (Cloud Shell, after Phase 2 is deployed)

No code download needed. This links the free Azure test domain, points the app's sender at it, and creates the Event Grid subscription to `/webhooks/acs` (the app must already be running Phase 2 to answer the handshake).

- [x] Ran the block (2026-09-23): sender `DoNotReply@e93c6f02-275f-4e7b-ae0c-b5cd4cd5540f.azurecomm.net`, event subscription created
- [x] Test email from Settings arrived in the inbox; `Delivered` report shown (2026-09-23)

```bash
az config set extension.use_dynamic_install=yes_without_prompt -o none
RG=rg-ashiwaju-app
DOMAIN_ID=$(az communication email domain create -g $RG --email-service-name ashiwaju-email --domain-name AzureManagedDomain --location global --domain-management AzureManaged --query id -o tsv)
az communication update -g $RG -n ashiwaju-acs --linked-domains "$DOMAIN_ID" -o none
FROM="DoNotReply@$(az communication email domain show -g $RG --email-service-name ashiwaju-email --domain-name AzureManagedDomain --query mailFromSenderDomain -o tsv)"
az webapp config appsettings set -g $RG -n ashiwaju-web --settings "Acs__SenderAddress=$FROM" -o none
KEY=$(az webapp config appsettings list -g $RG -n ashiwaju-web --query "[?name=='Webhooks__AcsSecret'].value" -o tsv)
az eventgrid system-topic event-subscription create -g $RG --system-topic-name ashiwaju-acs-events -n email-events \
  --endpoint "https://ashiwaju-web.azurewebsites.net/webhooks/acs?key=$KEY" \
  --included-event-types Microsoft.Communication.EmailDeliveryReportReceived Microsoft.Communication.EmailEngagementTrackingReportReceived \
  --max-delivery-attempts 30 --event-ttl 1440 -o none
echo "Sender is $FROM; event subscription created"
```

## AI writer: Anthropic API key

- [ ] Create an API key at console.anthropic.com → API keys (and set a monthly spend limit under Billing). Then in Cloud Shell, without sending the key to anyone:
  `az webapp config appsettings set -g rg-ashiwaju-app -n ashiwaju-web --settings "Ai__AnthropicApiKey=<paste key>" -o none`
  For future infrastructure redeploys also `export ANTHROPIC_API_KEY=<key>` so the Bicep keeps it.
