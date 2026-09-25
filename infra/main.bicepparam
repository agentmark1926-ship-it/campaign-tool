// Fill in the placeholders. Secrets come from environment variables (never commit real values):
//   export SQL_ADMIN_PASSWORD=... ENTRA_CLIENT_SECRET=... WEBHOOK_SECRET=... UNSUBSCRIBE_KEY=... ANTHROPIC_API_KEY=...
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam
using './main.bicep'

param appName = 'ashiwaju2'                       // 3-14 lowercase letters/digits
param entraClientId = '453022e6-df6f-4c62-a04e-ff412366c413'
param allowedUsers = 'justin@scoutsearchgroup.com'
// Sending domains. Add a domain to senderDomains and run the infra workflow to get its DNS records;
// once the portal shows it Verified, add it to verifiedDomains and run the workflow again. It then appears in the app's From dropdown.
param senderDomains = []
param verifiedDomains = []
param senderDisplayName = 'Self Storage Developers'
param mailingAddress = 'Self Storage Developers, 1101 Brickell Ave., 8th Fl, South Tower, Miami, FL 33131'
param timeZone = 'America/Chicago'
param linuxFxVersion = 'DOTNETCORE|10.0'

// Flip these on later deployments, in this order (see comments at the top of main.bicep):
param createEventSubscription = false
param createSenderUsername = false

// Secrets: pass on the command line, never commit real values.
param sqlAdminPassword = readEnvironmentVariable('SQL_ADMIN_PASSWORD', '')
param entraClientSecret = readEnvironmentVariable('ENTRA_CLIENT_SECRET', '')
param webhookSecret = readEnvironmentVariable('WEBHOOK_SECRET', '')
param unsubscribeKey = readEnvironmentVariable('UNSUBSCRIBE_KEY', '')
param anthropicApiKey = readEnvironmentVariable('ANTHROPIC_API_KEY', '')
