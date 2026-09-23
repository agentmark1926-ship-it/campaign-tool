// Fill in the placeholders. Secrets come from environment variables (never commit real values):
//   export SQL_ADMIN_PASSWORD=... ENTRA_CLIENT_SECRET=... WEBHOOK_SECRET=... UNSUBSCRIBE_KEY=...
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam
using './main.bicep'

param appName = 'ashiwaju'                       // 3-14 lowercase letters/digits
param entraClientId = '96676af7-caee-4824-8e26-a5451355fa03'
param allowedUsers = 'admin@self-storagedevelopers.com'
param senderDomain = 'self-storagedevelopers.com'             // root domain during the build; switch to a subdomain before real sends
param mailingAddress = 'Self Storage Developers, 1101 Brickell Ave., 8th Fl, South Tower, Miami, FL 33131'
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
