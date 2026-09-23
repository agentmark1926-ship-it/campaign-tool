// Email Campaign Tool — Azure infrastructure (see SPEC.md, "Deployment")
//
// First deployment (resources only):
//   az group create -n <rg> -l eastus2
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam
//
// Until linkDomain=true, mail is sent from the free Azure-managed test domain (DoNotReply@<id>.azurecomm.net).
//
// Later, flip these parameters and redeploy (each is a separate step because Azure validates them synchronously):
//   linkDomain=true              after the email domain shows "Verified" in the portal (DNS records added)
//   createEventSubscription=true after the app is deployed and answering POST /webhooks/acs
//   createSenderUsername=true    after the ACS email quota increase is approved (extra MailFrom addresses are blocked on the default quota)
//
// The `dnsRecords` output prints the exact TXT/CNAME values to add for the sending subdomain.

targetScope = 'resourceGroup'

@description('Base name for all resources. Lowercase letters and digits only, 3-14 chars.')
@minLength(3)
@maxLength(14)
param appName string

param location string = resourceGroup().location

@description('Entra tenant ID used by App Service Authentication.')
param entraTenantId string = subscription().tenantId

@description('Entra app registration (client) ID for App Service Authentication. Create with: az ad app create --display-name <appName> --web-redirect-uris https://<appName>-web.azurewebsites.net/.auth/login/aad/callback')
param entraClientId string

@secure()
@description('Client secret of that app registration.')
param entraClientSecret string

@description('Comma-separated UPNs allowed to use the app, e.g. you@yourdomain.com')
param allowedUsers string

param sqlAdminLogin string = 'campaignadmin'

@secure()
param sqlAdminPassword string

@secure()
@description('Random string (32+ chars) that Event Grid must present on the webhook URL.')
param webhookSecret string

@secure()
@description('Random string (32+ chars) used to sign unsubscribe tokens.')
param unsubscribeKey string

@secure()
@description('Anthropic API key for the AI email writer (optional; the writer is disabled without it).')
param anthropicApiKey string = ''

@description('Sending subdomain, e.g. news.yourdomain.com')
param senderDomain string

@description('Extra MailFrom username to create once the quota increase is approved. DoNotReply@<senderDomain> exists by default.')
param senderUsername string = 'updates'

param senderDisplayName string = 'Updates'

@description('App Service Linux runtime. Use DOTNETCORE|8.0 if 10.0 is not offered in your region yet.')
param linuxFxVersion string = 'DOTNETCORE|10.0'

@description('Postal mailing address printed in every email footer (CAN-SPAM).')
param mailingAddress string = ''

param timeZone string = 'America/Chicago'

param linkDomain bool = false
param createEventSubscription bool = false
param createSenderUsername bool = false

var suffix = uniqueString(resourceGroup().id)
var planName = '${appName}-plan'
var webName = '${appName}-web'
var sqlServerName = '${appName}-sql-${suffix}'
var sqlDbName = 'campaigns'
var storageName = take('${appName}${suffix}', 24)
var acsName = '${appName}-acs'
var emailServiceName = '${appName}-email'
var logName = '${appName}-logs'
var appInsightsName = '${appName}-ai'
var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDbName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

// ---------- Monitoring ----------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------- Storage ----------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource assetsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'email-assets'
  properties: { publicAccess: 'Blob' }
}

resource importsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'imports'
  properties: { publicAccess: 'None' }
}

// ---------- SQL ----------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDbName
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    maxSizeBytes: 2147483648
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ---------- Azure Communication Services (email) ----------

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: emailServiceName
  location: 'global'
  properties: {
    dataLocation: 'United States'
  }
}

resource emailDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: senderDomain
  location: 'global'
  properties: {
    domainManagement: 'CustomerManaged'
    userEngagementTracking: 'Disabled' // enable in the portal after the quota increase is approved
  }
}

// Free Azure-managed test domain (<id>.azurecomm.net): works without DNS, low sending limits.
// It is the linked sender domain until linkDomain=true switches to the custom domain.
resource testDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource senderUser 'Microsoft.Communication/emailServices/domains/senderUsernames@2023-04-01' = if (createSenderUsername) {
  parent: emailDomain
  name: senderUsername
  properties: {
    username: senderUsername
    displayName: senderDisplayName
  }
}

resource acs 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: acsName
  location: 'global'
  properties: {
    dataLocation: 'United States'
    linkedDomains: linkDomain ? [ emailDomain.id ] : [ testDomain.id ]
  }
}

// ---------- App Service ----------

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  properties: {
    reserved: true
  }
}

resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: webName
  location: location
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      minTlsVersion: '1.2'
      healthCheckPath: '/health'
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ConnectionStrings__Sql', value: sqlConnectionString }
        { name: 'ConnectionStrings__Storage', value: storageConnectionString }
        { name: 'Acs__ConnectionString', value: acs.listKeys().primaryConnectionString }
        { name: 'Acs__SenderAddress', value: !linkDomain ? 'DoNotReply@${testDomain.properties.mailFromSenderDomain}' : (createSenderUsername ? '${senderUsername}@${senderDomain}' : 'DoNotReply@${senderDomain}') }
        { name: 'Webhooks__AcsSecret', value: webhookSecret }
        { name: 'Auth__UnsubscribeKey', value: unsubscribeKey }
        { name: 'Auth__AllowedUsers', value: allowedUsers }
        { name: 'App__BaseUrl', value: 'https://${webName}.azurewebsites.net' }
        { name: 'App__TimeZone', value: timeZone }
        { name: 'App__MailingAddress', value: mailingAddress }
        { name: 'Sending__MaxPerMinute', value: '25' }
        { name: 'Sending__MaxPerHour', value: '90' }
        { name: 'Sending__BatchSize', value: '25' }
        { name: 'Import__UpdateExisting', value: 'true' }
        { name: 'Retention__EventMonths', value: '12' }
        { name: 'Storage__PublicContainer', value: 'email-assets' }
        { name: 'Storage__UploadsContainer', value: 'imports' }
        { name: 'Ai__AnthropicApiKey', value: anthropicApiKey }
        { name: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET', value: entraClientSecret }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
      ]
    }
  }
}

resource webAuth 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: web
  name: 'authsettingsV2'
  properties: {
    platform: {
      enabled: true
      runtimeVersion: '~1'
    }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
      excludedPaths: [
        '/unsubscribe/*'
        '/webhooks/*'
        '/health'
      ]
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: entraClientId
          clientSecretSettingName: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
          openIdIssuer: '${environment().authentication.loginEndpoint}${entraTenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            'api://${entraClientId}'
          ]
        }
      }
    }
    login: {
      tokenStore: { enabled: true }
    }
  }
}

// ---------- Event Grid: ACS email events -> app webhook ----------

resource acsEvents 'Microsoft.EventGrid/systemTopics@2022-06-15' = {
  name: '${appName}-acs-events'
  location: 'global'
  properties: {
    source: acs.id
    topicType: 'Microsoft.Communication.CommunicationServices'
  }
}

resource emailEventSubscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2022-06-15' = if (createEventSubscription) {
  parent: acsEvents
  name: 'email-events'
  properties: {
    destination: {
      endpointType: 'WebHook'
      properties: {
        endpointUrl: 'https://${web.properties.defaultHostName}/webhooks/acs?key=${webhookSecret}'
      }
    }
    filter: {
      includedEventTypes: [
        'Microsoft.Communication.EmailDeliveryReportReceived'
        'Microsoft.Communication.EmailEngagementTrackingReportReceived'
      ]
    }
    eventDeliverySchema: 'EventGridSchema'
    retryPolicy: {
      maxDeliveryAttempts: 30
      eventTimeToLiveInMinutes: 1440
    }
  }
}

// ---------- Outputs ----------

output webAppName string = web.name
output webAppUrl string = 'https://${web.properties.defaultHostName}'
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output storageAccountName string = storage.name
output acsResourceName string = acs.name
output emailServiceName string = emailService.name
output senderDomainName string = emailDomain.name
output testSenderAddress string = 'DoNotReply@${testDomain.properties.mailFromSenderDomain}'
@description('DNS records to add for the sending subdomain (Domain verification TXT, SPF TXT, DKIM/DKIM2 CNAMEs, DMARC TXT).')
output dnsRecords object = emailDomain.properties.verificationRecords
