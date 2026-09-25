// Email Campaign Tool — Azure infrastructure (see SPEC.md, "Deployment")
//
// First deployment (resources only):
//   az group create -n <rg> -l eastus2
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam
//
// The free Azure-managed test domain (DoNotReply@<id>.azurecomm.net) is always linked, so sending works before any DNS exists.
// Custom domains: list each in senderDomains (Azure creates it and prints its DNS records), then add it to verifiedDomains once
// the portal shows it Verified; every verified domain is linked and becomes a choice in the app's From dropdown.
//
// Later, flip these parameters and redeploy (each is a separate step because Azure validates them synchronously):
//   createEventSubscription=true after the app is deployed and answering POST /webhooks/acs
//   createSenderUsername=true    after the ACS email quota increase is approved (extra MailFrom addresses are blocked on the default quota; also enables click tracking)
//
// Use the GitHub 'infra' workflow for redeploys: it reuses the secrets already in the Web App's settings.
//
// The `dnsRecords` output prints the exact TXT/CNAME values to add for each sending domain.

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

@description('Custom sending domains to register in Azure, e.g. [\'news.yourdomain.com\']. Each gets its own DNS records (see the dnsRecords output).')
param senderDomains array = []

@description('The subset of senderDomains that shows Verified in the portal. Only these are linked and offered as From addresses; the first is the default.')
param verifiedDomains array = []

@description('Extra MailFrom username to create on each verified domain once the quota increase is approved. DoNotReply@<domain> exists by default.')
param senderUsername string = 'updates'

@description('The name recipients see next to the From address.')
param senderDisplayName string = 'Updates'

@description('Per-domain override of senderDisplayName, e.g. { \'news.example.com\': \'Example Co\' }.')
param senderDisplayNames object = {}

@description('App Service Linux runtime. Use DOTNETCORE|8.0 if 10.0 is not offered in your region yet.')
param linuxFxVersion string = 'DOTNETCORE|10.0'

@description('Postal mailing address printed in every email footer (CAN-SPAM).')
param mailingAddress string = ''

param timeZone string = 'America/Chicago'

@description('Where Azure Monitor alerts are emailed. Defaults to the first allowed user.')
param alertEmail string = first(split(allowedUsers, ','))

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

resource emailDomains 'Microsoft.Communication/emailServices/domains@2023-04-01' = [for domain in senderDomains: {
  parent: emailService
  name: domain
  location: 'global'
  properties: {
    domainManagement: 'CustomerManaged'
    // Click tracking is only allowed after the quota increase, the same moment the extra MailFrom address is created.
    userEngagementTracking: createSenderUsername ? 'Enabled' : 'Disabled'
  }
}]

// Free Azure-managed test domain (<id>.azurecomm.net): works without DNS, low sending limits. Always linked.
resource testDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

// Sets the display name on the default DoNotReply address of each verified domain.
resource doNotReplyUsers 'Microsoft.Communication/emailServices/domains/senderUsernames@2023-04-01' = [for domain in verifiedDomains: {
  name: '${emailServiceName}/${domain}/donotreply'
  properties: {
    username: 'DoNotReply'
    displayName: senderDisplayNames[?domain] ?? senderDisplayName
  }
  dependsOn: [ emailDomains ]
}]

resource senderUsers 'Microsoft.Communication/emailServices/domains/senderUsernames@2023-04-01' = [for domain in (createSenderUsername ? verifiedDomains : []): {
  name: '${emailServiceName}/${domain}/${senderUsername}'
  properties: {
    username: senderUsername
    displayName: senderDisplayNames[?domain] ?? senderDisplayName
  }
  dependsOn: [ emailDomains ]
}]

resource acs 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: acsName
  location: 'global'
  properties: {
    dataLocation: 'United States'
    linkedDomains: concat([ testDomain.id ], map(verifiedDomains, d => resourceId('Microsoft.Communication/emailServices/domains', emailServiceName, d)))
  }
  dependsOn: [ emailDomains ]
}

var testSender = 'DoNotReply@${testDomain.properties.mailFromSenderDomain}'
var senderNames = map(verifiedDomains, d => '${d}=${senderDisplayNames[?d] ?? senderDisplayName}')
var customSenders = flatten(map(verifiedDomains, d => createSenderUsername ? [ '${senderUsername}@${d}', 'DoNotReply@${d}' ] : [ 'DoNotReply@${d}' ]))

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
        { name: 'Acs__SenderAddress', value: empty(customSenders) ? testSender : customSenders[0] }
        { name: 'Acs__SenderAddresses', value: join(concat(customSenders, [ testSender ]), ',') }
        { name: 'Acs__SenderNames', value: join(senderNames, ';') }
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

// ---------- Alerts (SPEC Phase 7; emailed to alertEmail) ----------

resource alertGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${appName}-alerts'
  location: 'Global'
  properties: {
    groupShortName: take(appName, 12)
    enabled: true
    emailReceivers: [
      { name: 'owner', emailAddress: trim(alertEmail), useCommonAlertSchema: true }
    ]
  }
}

// The app checks failed share > 2%, campaigns Sending with no batch for 30 minutes, and Unknown rows every 5 minutes
// and writes a warning starting with "ALERT" for each; this rule emails when any such line arrives.
resource appConditionAlert 'Microsoft.Insights/scheduledQueryRules@2022-06-15' = {
  name: '${appName}-app-conditions'
  location: location
  properties: {
    displayName: 'Campaign tool: failed > 2%, stalled campaign, or Unknown recipients'
    severity: 2
    enabled: true
    scopes: [ appInsights.id ]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: 'traces | where message has "ALERT"'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: { actionGroups: [ alertGroup.id ] }
  }
}

resource webhook5xxAlert 'Microsoft.Insights/scheduledQueryRules@2022-06-15' = {
  name: '${appName}-webhook-5xx'
  location: location
  properties: {
    displayName: 'Campaign tool: delivery-report webhook returned 5xx more than 5 times in 5 minutes'
    severity: 2
    enabled: true
    scopes: [ appInsights.id ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      allOf: [
        {
          query: 'requests | where url has "/webhooks/acs" and toint(resultCode) >= 500'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 5
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: { actionGroups: [ alertGroup.id ] }
  }
}

resource cpuAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${appName}-cpu'
  location: 'global'
  properties: {
    description: 'App Service plan CPU above 80% for 15 minutes'
    severity: 2
    enabled: true
    scopes: [ plan.id ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        { name: 'cpu', criterionType: 'StaticThresholdCriterion', metricName: 'CpuPercentage', operator: 'GreaterThan', threshold: 80, timeAggregation: 'Average' }
      ]
    }
    actions: [ { actionGroupId: alertGroup.id } ]
  }
}

resource sqlStorageAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${appName}-sql-storage'
  location: 'global'
  properties: {
    description: 'SQL database above 80% of its 2 GB'
    severity: 2
    enabled: true
    scopes: [ sqlDb.id ]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT1H'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        { name: 'storage', criterionType: 'StaticThresholdCriterion', metricName: 'storage_percent', operator: 'GreaterThan', threshold: 80, timeAggregation: 'Maximum' }
      ]
    }
    actions: [ { actionGroupId: alertGroup.id } ]
  }
}

// ---------- Outputs ----------

output webAppName string = web.name
output webAppUrl string = 'https://${web.properties.defaultHostName}'
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output storageAccountName string = storage.name
output acsResourceName string = acs.name
output emailServiceName string = emailService.name
output testSenderAddress string = testSender
@description('DNS records to add for each sending domain (Domain verification TXT, SPF TXT, DKIM/DKIM2 CNAMEs, DMARC TXT).')
output dnsRecords array = [for (domain, i) in senderDomains: {
  domain: domain
  records: emailDomains[i].properties.verificationRecords
}]
