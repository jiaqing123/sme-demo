// ---------------------------------------------------------------------------
// sme-demo — Azure Container Apps deployment (resource-group scope)
// ---------------------------------------------------------------------------
// One reviewable file, no modules. The Docker Compose stack in the repository
// root is the development contract; this template is its Azure counterpart:
//
//   web (nginx, external ingress) -> auth / api (internal ingress)
//                                    api -> Azure SQL, Blob + Table Storage,
//                                           issuer-mock, auth metadata
//   auth -> Azure SQL, sms-mock, Azure Files (/keys)
//
// Deliberate demo limitations, documented here rather than hidden:
//   * No VNet integration on the Container Apps environment. Services talk
//     over the environment's private ingress hostnames, but the environment
//     itself is not injected into a customer network.
//   * Azure SQL is publicly reachable with the special 0.0.0.0 "Allow Azure
//     services" firewall rule. That rule is a coarse Azure-wide boundary, not
//     a network restriction.
//   * Key Vault has public network access enabled and RBAC authorization on;
//     the deployment identity must be allowed to write secrets.
//   * Seating on Azure Files is an unverified assumption for a non-root UID
//     (see the auth-keys storage comments).
//
// Images are promoted by digest. The pipeline resolves a tag to a digest once
// and passes the six fully qualified references below; this template never
// rebuilds or retags anything.

targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Deployment environment. Drives resource names and is recorded in Azure tags.')
@allowed([
  'test'
  'uat'
  'prod'
])
param environmentName string

@description('Azure region for every new resource. Existing resources (the ACR) may live elsewhere.')
param location string

@description('Prefix for every resource name created by this deployment.')
@minLength(2)
// Container App and job names are limited to 32 characters. The longest suffix
// is "-job-migrate-auth" (16 chars), so a 10-character prefix with a 4-character
// environment name still fits exactly.
@maxLength(10)
param namePrefix string = 'smedemo'

@description('Name of the existing Azure Container Registry that holds the six images.')
@minLength(5)
@maxLength(50)
param acrName string

@description('Resource group of the existing ACR. Defaults to the deployment resource group.')
param acrResourceGroup string = resourceGroup().name

// The six image references are copied verbatim from the Build stage's
// resolve-digests.sh output. Each value has the form
// <registry>/sme-demo/<image>@sha256:<digest>; promotion never changes it.
@description('API image reference pinned by digest.')
param apiImage string

@description('Auth image reference pinned by digest.')
param authImage string

@description('Web (nginx) image reference pinned by digest.')
param webImage string

@description('SMS mock image reference pinned by digest.')
param smsMockImage string

@description('Issuer mock image reference pinned by digest.')
param issuerMockImage string

@description('Migrator image reference pinned by digest. Also used for the bootstrap SQL job.')
param migratorImage string

@description('SQL Server administrator login. The password is supplied as a secure parameter.')
param sqlAdminLogin string = 'smeadmin'

@description('SQL Server administrator password. Used by the migrator and bootstrap jobs, never by the applications.')
@secure()
param sqlAdminPassword string

@description('Password for the sme_api_app application login.')
@secure()
param apiDbPassword string

@description('Password for the sme_auth_app application login.')
@secure()
param authDbPassword string

// A published demo key is intentional: the issuer-mock validates it and the
// demo documentation publishes it. It is still a secret-shaped value, so it is
// marked @secure() and stored in Key Vault rather than emitted as an env literal.
@description('Shared API key for the issuer mock. The default is the published demo value.')
@secure()
#disable-next-line secure-parameter-default
param issuerApiKey string = 'demo-issuer-key'

// DEMO_MODE and the demo credentials are NOT secrets: the demo publishes them
// in docs/README so a reviewer can log in. They are plain parameters on purpose
// and must be replaced (and DEMO_MODE set false) before a non-demo deployment.
@description('Enable the published demo login flow (fixed OTP, demo password).')
param demoMode bool = true

@description('Organisation id shared by auth and api (SME tenant scope).')
param orgId string = 'sme'

@description('Demo username shown to the public. Published, not a secret.')
param demoUsername string = 'demo'

@description('Demo password shown to the public. Published, not a secret.')
#disable-next-line secure-secrets-in-params
param demoPassword string = 'Demo_Passw0rd!'

@description('Fixed demo OTP shown to the public. Published, not a secret.')
param demoOtp string = '123456'

@description('Log Analytics retention in days (30 is the minimum allowed by the workspace SKU).')
@minValue(30)
@maxValue(730)
param logRetentionDays int = 30

@description('Path to the bootstrap SQL inside the migrator image. Must match what the image copies to /app.')
param bootstrapSqlPath string = '/app/bootstrap/azure-app-users.sql'

@description('Tags applied to every resource created by this template.')
param tags object = {}

// ---------------------------------------------------------------------------
// Naming
// ---------------------------------------------------------------------------
// Container App and job names must be <= 32 characters and lowercase. The
// environment name is part of the name so one resource group can host only one
// environment per prefix, which keeps the rollback and smoke scripts simple.

// Storage account names cannot contain hyphens and must be globally unique.
// The uniqueString input includes the prefix and environment so two prefixes in
// one resource group do not collide, while the name stays inside the 24-char
// storage-account limit (15 chars total).
var storageAccountName = toLower('st${uniqueString(resourceGroup().id, namePrefix, environmentName)}')
var logAnalyticsName = '${namePrefix}-${environmentName}-logs'
var containerAppsEnvironmentName = '${namePrefix}-${environmentName}-env'
var sqlServerName = toLower('${namePrefix}-${environmentName}-sql-${uniqueString(resourceGroup().id)}')
// Key Vault names are limited to 24 characters; environmentName is 4 chars at
// most and the suffix is a fixed-length hash, so this is always within range.
var keyVaultName = toLower('kv-${environmentName}-${uniqueString(resourceGroup().id, namePrefix, environmentName)}')
var identityName = '${namePrefix}-${environmentName}-id'

var webAppName = '${namePrefix}-${environmentName}-web'
var authAppName = '${namePrefix}-${environmentName}-auth'
var apiAppName = '${namePrefix}-${environmentName}-api'
var smsMockAppName = '${namePrefix}-${environmentName}-sms-mock'
var issuerMockAppName = '${namePrefix}-${environmentName}-issuer-mock'

var migrateApiJobName = '${namePrefix}-${environmentName}-job-migrate-api'
var migrateAuthJobName = '${namePrefix}-${environmentName}-job-migrate-auth'
var dbGrantsJobName = '${namePrefix}-${environmentName}-job-db-grants'

// ---------------------------------------------------------------------------
// 1. Observability: Log Analytics + Container Apps environment
// ---------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
  }
}

// No vnetConfiguration: this is the documented demo limitation. The environment
// gets a public default domain; the platform's internal ingress is the only
// network boundary between the services. A production deployment would add a
// VNet, private DNS, and private endpoints for SQL, Storage, and Key Vault.
resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppsEnvironmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        // The shared key is a deployment-time secret. It is not emitted as an
        // output; it does appear in the resource's deployment payload just like
        // any connection string, which is why deployment history access is
        // restricted (see docs/azure-pipelines.md).
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// The default domain is needed inside this same deployment to build the
// cross-service URLs (auth metadata, SMS provider, nginx upstreams). Deriving
// it from the resource avoids a two-pass deployment with outputs.
var defaultDomain = containerAppsEnvironment.properties.defaultDomain

var webFqdn = '${webAppName}.${defaultDomain}'
var authFqdn = '${authAppName}.${defaultDomain}'
var apiFqdn = '${apiAppName}.${defaultDomain}'
var smsMockFqdn = '${smsMockAppName}.${defaultDomain}'
var issuerMockFqdn = '${issuerMockAppName}.${defaultDomain}'

// ---------------------------------------------------------------------------
// 2. Storage: Blob (card art), Table (transactions), Azure Files (auth keys)
// ---------------------------------------------------------------------------

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // Card art is served through the API, never directly from Blob Storage.
    allowBlobPublicAccess: false
    // The API reads the single connection string today (AZURE_STORAGE_CONNECTION_STRING),
    // so shared-key access must stay enabled. Migrating the API to managed
    // identity + Storage Blob Data Contributor is the follow-up that removes
    // this key from Key Vault.
    allowSharedKeyAccess: true
  }
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {}
}

resource cardArtContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: 'card-art'
  properties: {
    // Private: the only reader is the API, using the account connection string.
    publicAccess: 'None'
  }
}

resource tableServices 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {}
}

resource transactionsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableServices
  name: 'transactions'
}

resource fileServices 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {}
}

// The auth signing key store. Quota 5 GiB is the Azure Files minimum; the key
// files are a few KiB. Ownership is the risk: the auth image runs as the
// non-root APP_UID from the chiseled base image, and Azure Files (SMB) presents
// files as root:root unless the mount is configured otherwise. If the first
// real deployment cannot write here, the documented fallback is to keep a
// single auth replica and mount nothing; tokens then reset whenever a new
// revision starts (see docs/runbook.md §5 and the ADR's unverified list).
resource authKeysShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileServices
  name: 'auth-keys'
  properties: {
    shareQuota: 5
  }
}

// Connection string consumed by the API. Built from the account key because the
// application still uses connection-string auth; stored only in Key Vault.
var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

// Container Apps environment storage entry (Azure Files). The account key is
// consumed by the platform during mount, so it is part of the deployment payload.
resource authKeysEnvironmentStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: containerAppsEnvironment
  name: 'auth-keys'
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: authKeysShare.name
      accessMode: 'ReadWrite'
    }
  }
}

// ---------------------------------------------------------------------------
// 3. Azure SQL
// ---------------------------------------------------------------------------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
    // Demo limitation: public endpoint plus the 0.0.0.0 rule below. A real
    // deployment should use a private endpoint and no public access.
    publicNetworkAccess: 'Enabled'
  }
}

// 0.0.0.0 is Azure SQL's special "Allow Azure services" marker: it admits
// traffic from any Azure tenant, not just this subscription. It is what lets
// the Container Apps environment reach SQL without a VNet.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// SKU choice: S0 (Standard, 10 DTU) rather than Basic (5 DTU). The demo is
// tiny, but /readyz opens SQL every few seconds and auth issues one short
// transaction per login; Basic's 5 DTU budget leaves no headroom for a
// cold start plus the issuer sync. Both are single-database, so S0 costs very
// little more and avoids a predictable throttle. Production would use at
// least S2/vCore with zone redundancy.
var databaseSku = {
  name: 'S0'
  tier: 'Standard'
}

resource apiDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: 'sme_api'
  location: location
  tags: tags
  sku: databaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

resource authDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: 'sme_auth'
  location: location
  tags: tags
  sku: databaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

var sqlServerFqdn = '${sqlServer.name}.${environment().suffixes.sqlServerHostname}'

// Connection strings. Encrypt=True is the Azure SQL default but is stated
// explicitly; TrustServerCertificate=False means the server certificate is
// validated against the public CA that Azure SQL uses.
var sqlAdminConnectionString = 'Server=${sqlServerFqdn};Database=master;User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;'
// MigratorSettings requires DATABASE_URL to name a database (it uses the
// InitialCatalog as the target and rewrites it to master only for the
// reachability/creation check). One connection string therefore cannot serve
// both migrations, so the admin string is split per target database. The
// canonical "sql-admin-connection-string" points at master for the bootstrap
// job; the two suffixed secrets are consumed by the migrator jobs.
var sqlAdminApiConnectionString = 'Server=${sqlServerFqdn};Database=${apiDatabase.name};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;'
var sqlAdminAuthConnectionString = 'Server=${sqlServerFqdn};Database=${authDatabase.name};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;'
var apiConnectionString = 'Server=${sqlServerFqdn};Database=${apiDatabase.name};User Id=sme_api_app;Password=${apiDbPassword};Encrypt=True;TrustServerCertificate=False;'
var authConnectionString = 'Server=${sqlServerFqdn};Database=${authDatabase.name};User Id=sme_auth_app;Password=${authDbPassword};Encrypt=True;TrustServerCertificate=False;'

// ---------------------------------------------------------------------------
// 4. Key Vault
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    // RBAC-only: data-plane access is granted with the "Key Vault Secrets User"
    // role below. Access policies are intentionally not used.
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
    // Demo only. Production must enable purge protection (and usually a longer
    // soft-delete window) before holding anything that matters.
    enablePurgeProtection: false
    // Public network access stays enabled because the Container Apps
    // environment has no VNet. Access is still authenticated by RBAC.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

var keyVaultBaseUrl = 'https://${keyVault.name}.${environment().suffixes.keyvaultDns}'

// Every secret is created explicitly so the same names are used by the
// application env vars, the job env vars, and the runbook. The deployment
// identity needs Key Vault Secrets Officer on this vault (an unverified
// prerequisite recorded in the ADR).
var keyVaultSecretDefinitions = [
  {
    name: 'sql-admin-password'
    value: sqlAdminPassword
  }
  {
    name: 'api-db-password'
    value: apiDbPassword
  }
  {
    name: 'auth-db-password'
    value: authDbPassword
  }
  {
    name: 'sql-admin-connection-string'
    value: sqlAdminConnectionString
  }
  {
    name: 'sql-admin-connection-string-api'
    value: sqlAdminApiConnectionString
  }
  {
    name: 'sql-admin-connection-string-auth'
    value: sqlAdminAuthConnectionString
  }
  {
    name: 'api-connection-string'
    value: apiConnectionString
  }
  {
    name: 'auth-connection-string'
    value: authConnectionString
  }
  {
    name: 'issuer-api-key'
    value: issuerApiKey
  }
]

resource keyVaultSecrets 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = [
  for secret in keyVaultSecretDefinitions: {
    parent: keyVault
    name: secret.name
    properties: {
      value: secret.value
    }
  }
]

// The storage connection string needs listKeys(), which cannot be evaluated at
// the start of the deployment and therefore cannot be part of the loop above.
resource storageConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'storage-connection-string'
  properties: {
    value: storageConnectionString
  }
}

// ---------------------------------------------------------------------------
// 5. User-assigned managed identity and role assignments
// ---------------------------------------------------------------------------
// One identity for every Container App and job. Applications pull images with
// it and read Key Vault secrets at revision start; jobs read the admin
// connection string the same way.

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

// Existing ACR. Containers pull with the identity above, so the pipeline never
// logs Docker in to the registry.
//
// Single-file constraint: the ACR must be in *this* resource group. A role
// assignment scoped to a resource in another resource group needs a module,
// which this reviewable one-file template deliberately avoids. acrResourceGroup
// is kept as a pipeline-facing parameter and echoed in the outputs so the
// deployment record shows where the registry is expected to live; deploy.sh
// validates the expectation with `az acr show`.
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

var acrPullRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var keyVaultSecretsUserRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '4633458b-17de-408a-b874-0445c86b69e6')

// Deterministic GUID names: a retried deployment addresses the same role
// assignment instead of creating a second one.
resource acrPullRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // identity.id is known at the start of the deployment; principalId is only
  // known after the identity resource is created, so it cannot name a resource.
  name: guid(acr.id, identity.id, acrPullRoleDefinitionId)
  scope: acr
  properties: {
    roleDefinitionId: acrPullRoleDefinitionId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVaultSecretsUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleDefinitionId)
  scope: keyVault
  properties: {
    roleDefinitionId: keyVaultSecretsUserRoleDefinitionId
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Deliberately NOT granted: Storage Blob Data Contributor. The API uses a
// connection string (shared key), so a data-plane role would widen access
// without being used. Add the role together with the managed-identity storage
// migration, not before.

// ---------------------------------------------------------------------------
// 6. Container Apps
// ---------------------------------------------------------------------------
// Secret references use the Key Vault pattern shown here once and repeat it for
// every app and job: the platform fetches the secret with the app identity at
// revision provisioning, so no secret value is embedded in the template.
//
// All services keep minReplicas=maxReplicas=1. Rate limits and /readyz are
// per-instance in-process state (ADR-017); a second replica would give each
// browser a different rate-limit bucket and a different readiness answer, so
// replicas must stay at 1 until a shared limiter and shared readiness source
// exist. The auth signing keys on Azure Files are the other single-instance
// dependency.

var apiKeyVaultSecretRefs = [
  {
    name: 'api-connection-string'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/api-connection-string'
    identity: identity.id
  }
  {
    name: 'storage-connection-string'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/storage-connection-string'
    identity: identity.id
  }
  {
    name: 'issuer-api-key'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/issuer-api-key'
    identity: identity.id
  }
]

var authKeyVaultSecretRefs = [
  {
    name: 'auth-connection-string'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/auth-connection-string'
    identity: identity.id
  }
]

var issuerMockKeyVaultSecretRefs = [
  {
    name: 'issuer-api-key'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/issuer-api-key'
    identity: identity.id
  }
]

// JWT_ISSUER is the internal auth FQDN on purpose: it mirrors Compose
// (http://auth:8081) and keeps the metadata document and JWKS reachable from
// inside the environment without a round trip through the public web ingress.
// The browser never inspects `iss`; api validates signature and issuer against
// the same value.
var jwtIssuer = 'http://${authFqdn}'

// ---------------------------------------------------------------------------
// 6a. Internal services: auth, api, sms-mock, issuer-mock
// ---------------------------------------------------------------------------
// A loop keeps the four near-identical definitions together. The only
// structural difference is auth's Azure Files volume; env values still spell
// out every option each service reads.

var internalServiceApps = [
  {
    key: 'auth'
    name: authAppName
    image: authImage
    port: 8081
    env: [
      {
        name: 'ASPNETCORE_URLS'
        value: 'http://+:8081'
      }
      {
        name: 'LOG_FORMAT'
        value: 'json'
      }
      {
        name: 'DATABASE_URL'
        secretRef: 'auth-connection-string'
      }
      {
        name: 'ORG_ID'
        value: orgId
      }
      {
        name: 'DEMO_MODE'
        value: string(demoMode)
      }
      {
        name: 'DEMO_USERNAME'
        value: demoUsername
      }
      {
        name: 'DEMO_PASSWORD'
        value: demoPassword
      }
      {
        // Compose still supplies DEMO_PHONE (default +61400000000); keep the
        // same demo identity here so auth's non-demo validation has one value.
        name: 'DEMO_PHONE'
        value: '+61400000000'
      }
      {
        name: 'DEMO_OTP'
        value: demoOtp
      }
      {
        name: 'JWT_ISSUER'
        value: jwtIssuer
      }
      {
        name: 'JWT_AUDIENCE'
        value: 'sme-demo-api'
      }
      {
        name: 'CORS_ORIGIN'
        value: 'https://${webFqdn}'
      }
      {
        name: 'SMS_PROVIDER_URL'
        value: 'http://${smsMockFqdn}'
      }
      {
        // Only safe because auth has internal ingress and the platform ingress
        // is the only path to it: no browser or other workload can reach the
        // container directly to spoof X-Forwarded-For. A directly reachable
        // auth must keep this false (ADR-017).
        name: 'AUTH_TRUST_FORWARDED_HEADERS'
        value: 'true'
      }
      {
        name: 'KEYS_PATH'
        value: '/keys'
      }
    ]
    secrets: authKeyVaultSecretRefs
    // The platform probe replaces Compose's in-image
    // SmeDemo.HealthProbe executable (the runtime image has no shell/curl).
    // Auth exposes liveness only; its /healthz is 200 as soon as the process
    // answers, exactly like the Compose healthcheck.
    probes: [
      {
        type: 'Readiness'
        httpGet: {
          path: '/healthz'
          port: 8081
          scheme: 'HTTP'
        }
        initialDelaySeconds: 10
        periodSeconds: 10
        timeoutSeconds: 5
        failureThreshold: 3
      }
      {
        type: 'Liveness'
        httpGet: {
          path: '/healthz'
          port: 8081
          scheme: 'HTTP'
        }
        initialDelaySeconds: 30
        periodSeconds: 10
        timeoutSeconds: 5
        failureThreshold: 3
      }
    ]
  }
  {
    key: 'api'
    name: apiAppName
    image: apiImage
    port: 8082
    env: [
      {
        name: 'ASPNETCORE_URLS'
        value: 'http://+:8082'
      }
      {
        name: 'LOG_FORMAT'
        value: 'json'
      }
      {
        name: 'DATABASE_URL'
        secretRef: 'api-connection-string'
      }
      {
        name: 'ORG_ID'
        value: orgId
      }
      {
        name: 'JWT_ISSUER'
        value: jwtIssuer
      }
      {
        name: 'JWT_AUDIENCE'
        value: 'sme-demo-api'
      }
      {
        name: 'AUTH_METADATA_URL'
        value: 'http://${authFqdn}/.well-known/openid-configuration'
      }
      {
        // auth is internal HTTP ingress. Metadata is fetched from inside the
        // environment, so HTTPS is impossible until a TLS-terminating ingress
        // (or a VNet private endpoint) is added; that change must flip this to
        // true. The JWT itself is still validated by signature and issuer.
        name: 'AUTH_REQUIRE_HTTPS_METADATA'
        value: 'false'
      }
      {
        name: 'CORS_ORIGIN'
        value: 'https://${webFqdn}'
      }
      {
        name: 'AZURE_STORAGE_CONNECTION_STRING'
        secretRef: 'storage-connection-string'
      }
      {
        name: 'TRANSACTIONS_TABLE'
        value: 'transactions'
      }
      {
        name: 'CARD_ART_CONTAINER'
        value: 'card-art'
      }
      {
        // The SPA origin, not the API's own hostname: nginx proxies /api, so
        // that is the base URL users and browsers actually see.
        name: 'PUBLIC_BASE_URL'
        value: 'https://${webFqdn}'
      }
      {
        name: 'SEED_ENABLED'
        value: 'true'
      }
      {
        name: 'ISSUER_BASE_URL'
        value: 'http://${issuerMockFqdn}'
      }
      {
        name: 'ISSUER_API_KEY'
        secretRef: 'issuer-api-key'
      }
      {
        name: 'ISSUER_SYNC_INTERVAL'
        value: '5m'
      }
      {
        name: 'ISSUER_SYNC_ENABLED'
        value: 'true'
      }
    ]
    secrets: apiKeyVaultSecretRefs
    probes: [
      {
        // Dependency readiness, exactly as Compose's api healthcheck: SQL,
        // Blob Storage and auth metadata must all be reachable. The platform's
        // probe replaces the in-image SmeDemo.HealthProbe executable.
        type: 'Readiness'
        httpGet: {
          path: '/readyz'
          port: 8082
          scheme: 'HTTP'
        }
        initialDelaySeconds: 10
        periodSeconds: 10
        timeoutSeconds: 5
        failureThreshold: 3
      }
      {
        type: 'Liveness'
        httpGet: {
          path: '/healthz'
          port: 8082
          scheme: 'HTTP'
        }
        initialDelaySeconds: 30
        periodSeconds: 10
        timeoutSeconds: 5
        failureThreshold: 3
      }
    ]
  }
  {
    key: 'sms-mock'
    name: smsMockAppName
    image: smsMockImage
    port: 8083
    env: [
      {
        name: 'ASPNETCORE_URLS'
        value: 'http://+:8083'
      }
    ]
    secrets: []
    probes: [
      {
        type: 'Liveness'
        httpGet: {
          path: '/healthz'
          port: 8083
          scheme: 'HTTP'
        }
        initialDelaySeconds: 10
        periodSeconds: 10
        timeoutSeconds: 5
        failureThreshold: 3
      }
    ]
  }
  {
    key: 'issuer-mock'
    name: issuerMockAppName
    image: issuerMockImage
    port: 8084
    env: [
      {
        name: 'ASPNETCORE_URLS'
        value: 'http://+:8084'
      }
      {
        name: 'ISSUER_API_KEY'
        secretRef: 'issuer-api-key'
      }
    ]
    secrets: issuerMockKeyVaultSecretRefs
    probes: [
      {
        type: 'Liveness'
        httpGet: {
          path: '/healthz'
          port: 8084
          scheme: 'HTTP'
        }
        initialDelaySeconds: 10
        periodSeconds: 10
        timeoutSeconds: 5
        failureThreshold: 3
      }
    ]
  }
]

// Names must be known at the start of the deployment, while the env values
// contain the environment's defaultDomain (assigned during this deployment).
// The name array is therefore static, the loop iterates over its indexes, and
// the body may still read the runtime FQDNs from internalServiceApps[i].
var internalServiceAppNames = [
  authAppName
  apiAppName
  smsMockAppName
  issuerMockAppName
]

resource internalApps 'Microsoft.App/containerApps@2024-03-01' = [
  for i in range(0, length(internalServiceAppNames)): {
    name: internalServiceAppNames[i]
    location: location
    tags: tags
    // Image pulls need AcrPull and revision start reads Key Vault. The role
    // assignment is created in this deployment, but RBAC propagation can lag;
    // if the first revision fails to pull or read a secret, redeploy after the
    // assignment has propagated (recorded as an unverified assumption).
    dependsOn: [
      acrPullRoleAssignment
      keyVaultSecretsUserRoleAssignment
      keyVaultSecrets
      storageConnectionStringSecret
    ]
    identity: {
      type: 'UserAssigned'
      userAssignedIdentities: {
        '${identity.id}': {}
      }
    }
    properties: {
      managedEnvironmentId: containerAppsEnvironment.id
      configuration: {
        // Multiple revision mode is what makes the documented rollback
        // (activate the previous revision, move 100% of traffic to it) work.
        // The ingress traffic rule below still sends 100% to the latest
        // revision on a normal deployment, so no manual cutover is needed.
        activeRevisionsMode: 'Multiple'
        ingress: {
          external: false
          targetPort: internalServiceApps[i].port
          transport: 'http'
          // Internal ingress is plain HTTP between services inside the
          // environment. The only TLS-terminating, external listener is web.
          allowInsecure: true
          traffic: [
            {
              latestRevision: true
              weight: 100
            }
          ]
        }
        secrets: internalServiceApps[i].secrets
      }
      template: {
        containers: [
          {
            name: internalServiceApps[i].key
            image: internalServiceApps[i].image
            resources: {
              cpu: json('0.5')
              memory: '1Gi'
            }
            env: internalServiceApps[i].env
            probes: internalServiceApps[i].probes
            volumeMounts: internalServiceApps[i].key == 'auth'
              ? [
                  {
                    volumeName: 'auth-keys'
                    mountPath: '/keys'
                  }
                ]
              : []
          }
        ]
        volumes: internalServiceApps[i].key == 'auth'
          ? [
              {
                name: 'auth-keys'
                storageType: 'AzureFile'
                storageName: authKeysEnvironmentStorage.name
              }
            ]
          : []
        scale: {
          // ADR-017: in-memory rate limits and per-instance readiness. Keep 1.
          minReplicas: 1
          maxReplicas: 1
        }
      }
    }
  }
]

// ---------------------------------------------------------------------------
// 6b. Web (nginx): the only external listener
// ---------------------------------------------------------------------------
// nginx serves the SPA and proxies /auth and /api so the browser has one
// origin. UPSTREAM_AUTH/UPSTREAM_API/NGINX_RESOLVER are the names the nginx
// image reads (see web/nginx); 168.63.129.16 is Azure's platform DNS address,
// which resolves the internal Container Apps FQDNs.

resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: webAppName
  location: location
  tags: tags
  dependsOn: [
    acrPullRoleAssignment
  ]
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Multiple'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        // HTTPS-only ingress; the platform certificate uses the default
        // environment domain. The smoke test talks to https://<webFqdn>.
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      secrets: []
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'DEMO_MODE'
              value: string(demoMode)
            }
            {
              name: 'DEMO_USERNAME'
              value: demoUsername
            }
            {
              name: 'DEMO_PASSWORD'
              value: demoPassword
            }
            {
              name: 'DEMO_OTP'
              value: demoOtp
            }
            {
              name: 'UPSTREAM_AUTH'
              value: 'http://${authFqdn}'
            }
            {
              name: 'UPSTREAM_API'
              value: 'http://${apiFqdn}'
            }
            {
              name: 'NGINX_RESOLVER'
              value: '168.63.129.16'
            }
          ]
          probes: [
            {
              // nginx has no /healthz; GET / returns the SPA shell once it is
              // serving. This is liveness, not dependency readiness: a 502
              // from a downstream service does not restart nginx.
              type: 'Liveness'
              httpGet: {
                path: '/'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

// ---------------------------------------------------------------------------
// 7. Container Apps Jobs (manual, one-shot)
// ---------------------------------------------------------------------------
// The migrator image is the same one Compose runs. Jobs are definitions only;
// tools/deploy/deploy.sh starts them after the resource deployment in this
// order: bootstrap logins (master) -> api migration -> auth migration, and
// waits for each execution.
//
// The bootstrap script is one file dispatched on DB_NAME(): it creates the
// server-level logins only when run against master, and the database user plus
// datareader/datawriter roles when run against sme_api or sme_auth. That is why
// all three jobs set BOOTSTRAP_SQL, while the migrate jobs also set
// MIGRATIONS_PATH so the schema is applied first.
//
// Job container secrets: the admin connection string is used by the migrator
// and bootstrap scripts. The application logins cannot create schemas, so these
// jobs must not use the app connection strings.

// The bootstrap script substitutes ${SME_API_DB_PASSWORD} and
// ${SME_AUTH_DB_PASSWORD} over the whole file, so every one of the three job
// runs needs both values even though a single run only touches one branch.
var jobDbPasswordSecretRefs = [
  {
    name: 'api-db-password'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/api-db-password'
    identity: identity.id
  }
  {
    name: 'auth-db-password'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/auth-db-password'
    identity: identity.id
  }
]

var migratorSecretRefs = [
  {
    name: 'sql-admin-connection-string'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/sql-admin-connection-string'
    identity: identity.id
  }
]

var migrateApiSecretRefs = [
  {
    name: 'sql-admin-connection-string-api'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/sql-admin-connection-string-api'
    identity: identity.id
  }
]

var migrateAuthSecretRefs = [
  {
    name: 'sql-admin-connection-string-auth'
    keyVaultUrl: '${keyVaultBaseUrl}/secrets/sql-admin-connection-string-auth'
    identity: identity.id
  }
]

resource migrateApiJob 'Microsoft.App/jobs@2024-03-01' = {
  name: migrateApiJobName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: concat(migrateApiSecretRefs, jobDbPasswordSecretRefs)
    }
    template: {
      containers: [
        {
          name: 'migrator'
          image: migratorImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'DATABASE_URL'
              secretRef: 'sql-admin-connection-string-api'
            }
            {
              // Required by the bootstrap file's ${...} substitution even
              // though this run only creates the sme_api_app user.
              name: 'SME_API_DB_PASSWORD'
              secretRef: 'api-db-password'
            }
            {
              name: 'SME_AUTH_DB_PASSWORD'
              secretRef: 'auth-db-password'
            }
            {
              name: 'MIGRATIONS_PATH'
              value: '/app/migrations/api'
            }
            {
              // After the schema is applied, the same bootstrap script runs
              // against sme_api to create the user and its reader/writer roles.
              name: 'BOOTSTRAP_SQL'
              value: bootstrapSqlPath
            }
            {
              name: 'DB_WAIT_SECONDS'
              value: '300'
            }
          ]
        }
      ]
    }
  }
}

resource migrateAuthJob 'Microsoft.App/jobs@2024-03-01' = {
  name: migrateAuthJobName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: concat(migrateAuthSecretRefs, jobDbPasswordSecretRefs)
    }
    template: {
      containers: [
        {
          name: 'migrator'
          image: migratorImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'DATABASE_URL'
              secretRef: 'sql-admin-connection-string-auth'
            }
            {
              // Required by the bootstrap file's ${...} substitution even
              // though this run only creates the sme_auth_app user.
              name: 'SME_API_DB_PASSWORD'
              secretRef: 'api-db-password'
            }
            {
              name: 'SME_AUTH_DB_PASSWORD'
              secretRef: 'auth-db-password'
            }
            {
              name: 'MIGRATIONS_PATH'
              value: '/app/migrations/auth'
            }
            {
              // After the schema is applied, the same bootstrap script runs
              // against sme_auth to create the user and its reader/writer roles.
              name: 'BOOTSTRAP_SQL'
              value: bootstrapSqlPath
            }
            {
              name: 'DB_WAIT_SECONDS'
              value: '300'
            }
          ]
        }
      ]
    }
  }
}

resource dbGrantsJob 'Microsoft.App/jobs@2024-03-01' = {
  name: dbGrantsJobName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: concat(migratorSecretRefs, jobDbPasswordSecretRefs)
    }
    template: {
      containers: [
        {
          name: 'migrator'
          image: migratorImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              // Master connection: with DB_NAME() = master the bootstrap script
              // creates only the server-level logins. It runs before the two
              // migrations; the per-database user grants are applied by the
              // migrate jobs' bootstrap pass.
              name: 'DATABASE_URL'
              secretRef: 'sql-admin-connection-string'
            }
            {
              // Required by the bootstrap file's ${...} substitution; the
              // master branch creates both server-level logins.
              name: 'SME_API_DB_PASSWORD'
              secretRef: 'api-db-password'
            }
            {
              name: 'SME_AUTH_DB_PASSWORD'
              secretRef: 'auth-db-password'
            }
            {
              // /app/migrations has no top-level *.sql (the scripts live in the
              // api/ and auth/ subdirectories), so the migrator finds no schema
              // scripts here and goes straight to the bootstrap file.
              name: 'MIGRATIONS_PATH'
              value: '/app/migrations'
            }
            {
              // The path is copied into the migrator image as
              // /app/bootstrap/azure-app-users.sql (COPY db/bootstrap ./bootstrap).
              // Keep the default in sync with the image; the pipeline may
              // override it with the bootstrapSqlPath parameter.
              name: 'BOOTSTRAP_SQL'
              value: bootstrapSqlPath
            }
            {
              name: 'DB_WAIT_SECONDS'
              value: '300'
            }
          ]
        }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------
// No secret values are exposed. webUrl is the public entry point the smoke
// script and the deployment record use.

output webFqdn string = webFqdn
output webUrl string = 'https://${webFqdn}'
output apiFqdn string = apiFqdn
output authFqdn string = authFqdn
output sqlServerFqdn string = sqlServerFqdn
output containerAppsEnvironmentName string = containerAppsEnvironment.name
output containerAppsEnvironmentDefaultDomain string = defaultDomain
output keyVaultName string = keyVault.name
output resourceGroupName string = resourceGroup().name
// Echoed for the deployment record. The template can create the AcrPull role
// assignment only when the registry is in this resource group (see the ACR
// resource comment); deploy.sh checks this before deploying.
output acrResourceGroupName string = acrResourceGroup
