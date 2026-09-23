// Fill in the placeholders. Secrets come from environment variables (never commit real values):
//   export SQL_ADMIN_PASSWORD=... ENTRA_CLIENT_SECRET=... WEBHOOK_SECRET=... UNSUBSCRIBE_KEY=...
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam
using './main.bicep'

param appName = 'campaigns'                       // 3-14 lowercase letters/digits
param entraClientId = '00000000-0000-0000-0000-000000000000'
param allowedUsers = 'you@yourdomain.com'
param senderDomain = 'news.yourdomain.com'
param mailingAddress = 'Your Company, 123 Main St, City, ST 00000'
param timeZone = 'America/Chicago'
param linuxFxVersion = 'DOTNETCORE|10.0'

// Flip these on later deployments, in this order (see comments at the top of main.bicep):
param linkDomain = false
param createEventSubscription = false
param createSenderUsername = false

// Secrets: pass on the command line, never commit real values.
param sqlAdminPassword = readEnvironmentVariable('SQL_ADMIN_PASSWORD', '')
param entraClientSecret = readEnvironmentVariable('ENTRA_CLIENT_SECRET', '')
param webhookSecret = readEnvironmentVariable('WEBHOOK_SECRET', '')
param unsubscribeKey = readEnvironmentVariable('UNSUBSCRIBE_KEY', '')
