#!/usr/bin/env bash
# One-paste setup of the whole app in a fresh Azure subscription, from Azure Cloud Shell (Bash):
#
#   git clone https://github.com/agentmark1926-ship-it/campaign-tool && bash campaign-tool/infra/bootstrap.sh
#
# Creates: the resource group, the sign-in app registration, every resource in main.bicep (sending from the free Azure
# test domain), and the GitHub Actions deploy identity. Secrets are generated here and stored only in the Web App's
# settings; nothing secret is printed. At the end it prints the values to paste into GitHub and to send to Claude Code.
#
# Overrides (optional): APP=<3-14 lowercase letters/digits> LOCATION=<region> bash infra/bootstrap.sh
set -euo pipefail

APP="${APP:-ashiwaju2}"
LOCATION="${LOCATION:-centralus}"
RG="rg-$APP-app"
REPO_SUBJECTS=(
  "repo:agentmark1926-ship-it/campaign-tool:ref:refs/heads/main"
  "repo:agentmark1926-ship-it@329644780/campaign-tool@1383551704:ref:refs/heads/main"
)
cd "$(dirname "$0")/.."

SUB=$(az account show --query id -o tsv)
TENANT=$(az account show --query tenantId -o tsv)
# Allow whichever name App Service sign-in reports for you (work account UPN, or the email of a personal account).
ME_UPN=$(az ad signed-in-user show --query userPrincipalName -o tsv)
ME_MAIL=$(az ad signed-in-user show --query mail -o tsv 2>/dev/null || true)
ME_EMAIL=$(az account show --query user.name -o tsv)
ALLOWED=$(printf '%s\n' "$ME_EMAIL" "$ME_MAIL" "$ME_UPN" | grep -v '^$' | grep -v '^None$' | awk '!seen[tolower($0)]++' | paste -sd, -)
echo "Subscription $SUB, tenant $TENANT. Sign-in allowed for: $ALLOWED"

read -r -s -p "Anthropic API key for the AI writer (paste, or press Enter to skip): " ANTHROPIC_API_KEY; echo
export ANTHROPIC_API_KEY

echo "== Registering Azure resource providers (first run on a new subscription takes a few minutes)"
for ns in Microsoft.Web Microsoft.Sql Microsoft.Storage Microsoft.Communication Microsoft.EventGrid \
          Microsoft.Insights Microsoft.OperationalInsights Microsoft.AlertsManagement; do
  az provider register -n "$ns" --wait -o none
done

echo "== Resource group $RG in $LOCATION"
az group create -n "$RG" -l "$LOCATION" -o none

echo "== Sign-in app registration"
ENTRA_CLIENT_ID=$(az ad app list --display-name "$APP" --query "[0].appId" -o tsv)
if [ -z "$ENTRA_CLIENT_ID" ]; then
  ENTRA_CLIENT_ID=$(az ad app create --display-name "$APP" \
    --web-redirect-uris "https://$APP-web.azurewebsites.net/.auth/login/aad/callback" \
    --enable-id-token-issuance true --sign-in-audience AzureADMyOrg --query appId -o tsv)
fi
ENTRA_CLIENT_SECRET=$(az ad app credential reset --id "$ENTRA_CLIENT_ID" --append --display-name easyauth --query password -o tsv)

export ENTRA_CLIENT_SECRET
export SQL_ADMIN_PASSWORD="$(openssl rand -hex 16)Aa1!"
export WEBHOOK_SECRET="$(openssl rand -hex 32)"
export UNSUBSCRIBE_KEY="$(openssl rand -hex 32)"

echo "== Deploying main.bicep (about 5-10 minutes)"
az deployment group create -g "$RG" -n bootstrap -f infra/main.bicep -p infra/main.bicepparam \
  -p appName="$APP" entraClientId="$ENTRA_CLIENT_ID" allowedUsers="$ALLOWED" senderDomains='[]' verifiedDomains='[]' \
  --query "properties.outputs.{webAppUrl:webAppUrl.value, sender:testSenderAddress.value}" -o json

echo "== GitHub Actions deploy identity"
DEPLOY_ID=$(az ad app list --display-name "$APP-deploy" --query "[0].appId" -o tsv)
if [ -z "$DEPLOY_ID" ]; then
  DEPLOY_ID=$(az ad app create --display-name "$APP-deploy" --query appId -o tsv)
  az ad sp create --id "$DEPLOY_ID" -o none
fi
i=0
for subject in "${REPO_SUBJECTS[@]}"; do
  i=$((i + 1))
  az ad app federated-credential create --id "$DEPLOY_ID" -o none --parameters \
    "{\"name\":\"github-main-$i\",\"issuer\":\"https://token.actions.githubusercontent.com\",\"subject\":\"$subject\",\"audiences\":[\"api://AzureADTokenExchange\"]}" \
    2>/dev/null || echo "(federated credential github-main-$i already exists)"
done
for attempt in 1 2 3 4 5; do
  az role assignment create --assignee "$DEPLOY_ID" --role Contributor --scope "/subscriptions/$SUB/resourceGroups/$RG" -o none && break
  echo "(waiting for the new identity to appear, retry $attempt)"; sleep 20
done

cat <<EOF

================ DONE ================
1. GitHub -> campaign-tool -> Settings -> Secrets and variables -> Actions
   Secrets (update each):
     AZURE_CLIENT_ID       = $DEPLOY_ID
     AZURE_TENANT_ID       = $TENANT
     AZURE_SUBSCRIPTION_ID = $SUB
   Variables tab:
     AZURE_WEBAPP_NAME     = $APP-web

2. Send Claude Code these three lines (none of them is secret):
     appName=$APP
     entraClientId=$ENTRA_CLIENT_ID
     allowedUsers=$ALLOWED
======================================
EOF
