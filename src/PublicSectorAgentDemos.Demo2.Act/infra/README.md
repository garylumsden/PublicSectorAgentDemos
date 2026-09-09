# Standalone Demo2 deployment

This azd project deploys only the cross-government flood support application and its dependencies.
Run azd from `src\PublicSectorAgentDemos.Demo2.Act`, not from the repository root.
The application source remains under `src\Demo2.Web` and `src\Defra.Tools.Mcp`.
Infrastructure bootstrap code stays under `infra\bootstrap`.

## Deployment ownership

The parent setup owns environment creation, preflight approval, provisioning, deployment, and teardown.
No Azure or Entra resource was created, changed, or deleted during extraction.
No local azd environment was created during extraction.

Before any future deployment, run the `azure-deployment-preflight` skill in this project context.
Obtain approval for the target subscription, tenant, resource group, Graph changes, and model quota.
The preflight must cover both Azure RBAC and Microsoft Graph permissions.
Do not treat local compilation as permission, quota, authentication, or deployment evidence.
Preview commands can invoke hooks. Review their effects before running `azd provision --preview`.

Set the process-local user agent before every azd command:

```powershell
$env:AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'
Set-Location .\src\PublicSectorAgentDemos.Demo2.Act
```

The parent can run `azd up` after it creates and configures an approved new environment.
Do not copy `.azure` files, credentials, resource IDs, or outputs from the original checkout.
The hooks never query an old deployment or invoke another DEFRA application.

## Resource boundary

| Retained resource | Configuration and consumer |
| --- | --- |
| Resource group | `rg-${AZURE_ENV_NAME}`; all Azure resources use this group. |
| Microsoft Foundry account and project | System-assigned identities, key authentication disabled, original public endpoint contract. |
| Chat deployment and safety policy | `gpt-5.4-mini`, version `2026-03-17`, `GlobalStandard`, capacity `500`; blocking safety policy retained. |
| Linux App Service plan | Original `P0v3`, one instance. Hosts only `defra-tools` and `demo2-web`. |
| Two App Services | .NET 10, HTTPS, TLS 1.2, no FTP/basic publishing credentials, health endpoints, diagnostic settings. |
| Two user-assigned identities | Tools identity and web identity; original resource names remain environment-derived. |
| Two Entra applications and enterprise applications | Single-tenant web sign-in and the tools API audience. No old client IDs are reused. |
| Cosmos DB | Serverless, local authentication disabled, Session consistency, `defra-demos` database, `demo2-flood-support-cases` container, `/caseId` partition key. |
| Application Insights and Log Analytics | Original diagnostics, 30-day retention, application telemetry, Foundry telemetry connection. |
| Flood support project connection | `flood-support-connection`; `/mcp/flood-support` endpoint; project-managed identity. |
| Required roles | Web: Foundry User and container-scoped Cosmos contributor. Project identity: account-scoped Foundry User and telemetry access. Deployer: original project, website, and Cosmos roles. |

The template reuses these Azure Verified Module (AVM) pins:
`managed-identity/user-assigned-identity:0.6.0`, `operational-insights/workspace:0.15.1`,
`insights/component:0.7.2`, `web/serverfarm:0.7.0`, `web/site:0.23.1`, and
`document-db/database-account:0.19.0`.
The Graph extension remains `microsoftgraph/v1.0:1.0.0` in the bundle-local `bicepconfig.json`.
No application SDK, runtime, package, or model version was upgraded.

### Historical dependency removals

This table records the original welfare-only extraction. The current scenario uses the same resource categories with new tools and data.

| Removed dependency | Evidence |
| --- | --- |
| Demo3 web app, identity, Entra application, Cosmos container, and roles | Only Demo3 app settings and its outbreak connection reference them. |
| Demo4 hosted agent, container settings, roles, and skills hooks | Only the hosted-agent azd service and water bootstrap reference them. |
| Azure Maps | Only the former shared postprovision hook and non-welfare consumers use it. |
| Search, Blob Storage, embedding model, Foundry IQ connections, and associated RBAC | Only `Defra.Bootstrap` knowledge provisioning uses these resources. Welfare agents use the direct MCP connection. |
| Fast chat model | The agent catalog selects this model only for Demo3 role stages. Welfare and its structured-output formatter use the retained chat deployment. |
| Water, planetary, and outbreak MCP connections | The welfare catalog selects only `defra-tools-welfare-connection`. |
| Foundry toolbox resources and azd AI extensions | The upstream manifest contains only the water toolbox. Welfare uses direct MCP, not a Foundry toolbox. Bicep creates the project; the existing .NET SDK creates the prompt agent. |

Trace sources include `WelfareAgentClient.cs`, `Demo2Options.cs`, `Demo2StateStores.cs`,
`HighImpactActionAuthorizer.cs`, the agent catalog, and the original hooks and platform template.
Application pruning is separate from infrastructure extraction.
The bootstrap requires exactly one flood support catalog entry.
It never loads other prompt files or provisions those agents.

Fictional flood support fixtures initialize inside the tools application.
Cosmos starts with an empty container; the web application creates cases, conversations, approvals, and decisions during use.
No data-upload step or Blob Storage account is required.
The existing audit writer stores the web audit file under the App Service home directory.
The tools service writes its existing audit stream; application telemetry continues through Application Insights.

The resource group retains the user-approved `SecurityControl=Ignore` demo policy tag.
Without this exclusion, the subscription policy changes Cosmos public network access to `Disabled`, blocking the public App Service deployment.
The tag does not replace authentication: Cosmos local key authentication remains disabled, and its managed-identity role assignments remain required.
Do not treat this demonstration policy exclusion as a production security design.

## Inputs

| Input | Meaning |
| --- | --- |
| `AZURE_ENV_NAME` | Required new environment name; 1-64 letters, digits, or hyphens. |
| `AZURE_SUBSCRIPTION_ID` | Required subscription selected by azd. |
| `AZURE_LOCATION` | Required customer-selected Azure location with the required service and model availability. |
| `AZURE_PRINCIPAL_ID` | Required object ID in the subscription tenant. Must match the signed-in azd principal. |
| `AZURE_AI_LOCATION` | Optional Foundry/model region override; defaults to `AZURE_LOCATION`. |
| `AZURE_APP_LOCATION` | Optional application/state/telemetry region override; defaults to `AZURE_LOCATION`. |
| `AZURE_AUTHORIZED_USER_OBJECT_IDS` | Optional comma-separated user object IDs, maximum 6. Defaults to the deployment principal. |
| `AZURE_ADMIN_USER_OBJECT_IDS` | Optional comma-separated administrator user object IDs, maximum 6. Defaults to the deployment principal. |
| `AZURE_TOOLS_AUTHORIZED_PRINCIPAL_OBJECT_IDS` | Optional extra tools caller object IDs, maximum 12. Does not authorize resource reservations. |
| `AZURE_AI_ACCOUNT_SKU_NAME` | Optional account SKU; original default `S0`. |
| `AZURE_APP_SERVICE_HOSTNAME_SUFFIX` | Optional original App Service DNS suffix; default `azurewebsites.net`. |

The source parameter file retains an offline compilation fallback.
The deployment hook requires `AZURE_LOCATION` explicitly.
`AZURE_LOCATION` is output as the selected AI region, matching the original contract.
Both explicit location outputs preserve split-region choices on later runs.
A service-principal deployment requires explicit human user lists for interactive web access.
Use the same tenant and principal for Azure CLI and azd; the .NET bootstrap selects `AzureCliCredential`.

## Outputs

| Output group | Names |
| --- | --- |
| Web | `DEMO2_WEB_URL`, `DEMO2_WEB_SITE_NAME`, `DEMO2_WEB_HOSTNAME` |
| Tools | `DEFRA_TOOLS_URL`, `DEFRA_TOOLS_SITE_NAME`, `DEFRA_TOOLS_HOSTNAME`, `DEFRA_TOOLS_MCP_FLOOD_SUPPORT_URL` |
| Scope | `AZURE_RESOURCE_GROUP`, `AZURE_TENANT_ID`, `AZURE_LOCATION`, `AZURE_AI_LOCATION`, `AZURE_APP_LOCATION` |
| Foundry | `FOUNDRY_PROJECT_ENDPOINT`, `AZURE_AI_PROJECT_ENDPOINT`, account/project IDs and names, `AZURE_AI_PROJECT_PRINCIPAL_ID` |
| Model and agent | `AZURE_AI_MODEL_DEPLOYMENT_NAME`, `AZURE_AI_QUALITY_MODEL_DEPLOYMENT_NAME`, `AZURE_AI_REASONING_EFFORT`, `AZURE_AI_RAI_POLICY_NAME`, `DEMO2_AGENT_NAME` |
| Connections | `DEFRA_TOOLS_FLOOD_SUPPORT_CONNECTION_NAME`, `AZURE_AI_APPLICATION_INSIGHTS_CONNECTION_NAME` |
| Auth | `DEFRA_TOOLS_AUDIENCE`, `DEFRA_TOOLS_AUTH_CLIENT_ID`, `DEMO2_AUTH_CLIENT_ID` |
| State | `AZURE_COSMOS_ACCOUNT_ID`, `AZURE_COSMOS_ACCOUNT_NAME`, `AZURE_COSMOS_ENDPOINT`, `AZURE_COSMOS_DATABASE_NAME`, `AZURE_COSMOS_DEMO2_CONTAINER_NAME` |
| Telemetry | `APPLICATIONINSIGHTS_RESOURCE_ID`, `APPLICATIONINSIGHTS_NAME`, `APPLICATIONINSIGHTS_CONNECTION_STRING`, Log Analytics resource ID, name, and workspace ID |
| Identity | Tools and Demo2 identity resource IDs, client IDs, and principal IDs |

No output contains an Entra password, account key, bearer token, or connection credential.
The Application Insights connection string contains an ingestion identifier, not an authentication secret.
Microsoft documents that instrumentation keys are not security tokens or security keys.
Do not add actual credentials to plain Bicep outputs or azd environment values.

## azd lifecycle

1. `preprovision` validates inputs and binds the azd identity to the subscription tenant. It stores the tenant in this environment.
2. `provision` creates the Demo2 Azure boundary and its two Entra applications.
3. `postprovision` creates both enterprise applications and rotates a new 29-day web sign-in credential directly into App Service settings.
4. `deploy defra-tools` publishes only the tools service. Its `postdeploy` hook waits for health and project connection propagation.
5. That hook runs `infra\bootstrap\Demo2.Bootstrap.csproj`. It creates only `cross-government-flood-support` or retains its unchanged version.
6. `deploy demo2-web` publishes the flood support web app after its managed agent is ready.

The hash and approval serialization come from the original tested `AgentProvisioning.cs` implementation.
A missing agent version list means first-time creation, not reuse of an old agent.
Changed definitions create a new version; matching hashes do not create another version.
The bootstrap rejects changed flood support tool allowlists, missing reservation approval, and unsafe prompt paths or endpoint URLs.
There is no all-demo `postup`, toolboxes hook, guidance download, knowledge upload, or hosted-agent command.

The credential hook preserves unrelated App Service settings and removes only credentials with its own display name after replacement succeeds.
Bicep does not declare empty credential arrays, so later provisioning cannot delete hook-owned credentials through Graph reconciliation.
The hooks never delete Entra applications or resource groups.
The parent must approve credential rotation and any separate teardown operation.
Credentials are not printed, written to reports, or stored in an azd environment.
Re-run the credential hook through approved provisioning before its 29-day credential expires.

## Authentication and approval contract

The issuer remains `${loginEndpoint}${tenantId}/v2.0`.
The tools audience remains `api://${tenantId}/${toolsSiteName}`; the web audience uses the corresponding web site name.
Easy Auth validates both the configured client ID and the explicit audience, with tenant-specific OpenID metadata.
The web app redirects unauthenticated users to Entra sign-in. The tools service returns HTTP 401.
Only `/health` bypasses authentication.
The tools allowlist contains the project identity, web identity, approved users, and explicitly added callers.
`MCP_FLOOD_SUPPORT_ACTION_CALLER_PRINCIPAL_ID` contains only the Foundry project identity and the Demo2 web managed identity.
The managed agent always requests approval for `reserveSupportPackage`; read tools do not require approval.
The web administration workflow also reserves directly after enforcing its persisted approval, package snapshot, case version, and replay checks.
Both identity paths must work; neither permits an unapproved human caller to reserve directly through the tools API.
No broad tools caller permission changes that high-impact action restriction.

### Documented schema exception

The flood support connection uses `Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01`
with `authType: 'ProjectManagedIdentity'` and an explicit audience.
The published ARM/Bicep union omits this discriminator, including in the `2026-05-01` reference.
Current Foundry MCP documentation explicitly supports project-managed identity and its audience.
Retain the existing single-line `BCP036` suppression for this documented metadata gap.
Do not replace it with `ManagedIdentity` or `AAD`: those are different authentication contracts.
All other emitted Bicep warnings must be resolved, not suppressed.
Confirm this connection during an approved deployment and authenticated flood support call.

## Replacing an existing welfare deployment

This source change does not deploy or delete anything automatically.
An approved deployment changes the MCP route and publishes the new prompt agent before publishing the web application.
It also creates `demo2-flood-support-cases` and its container-scoped role assignment.
Existing `demo2-cases` records remain separate; there is no data migration or automatic deletion.
The old agent and connection are not reused as the new scenario.
Remove retired resources only through a separately approved cleanup.
The hosting plan, resource names, managed identities, telemetry, and approved resource-group policy tag remain in place.

## Local validation

Use the existing Bicep executable directly. `az bicep build --stdout` can fail on Windows character encoding.
These commands do not create an azd environment or call Azure deployment APIs:

```powershell
bicep restore infra\main.bicep
bicep build infra\main.bicep --stdout --no-restore
bicep lint infra\main.bicep --no-restore
bicep format infra\main.bicep
bicep format infra\main.bicepparam
bicep format infra\modules\foundry.bicep
bicep format infra\modules\platform.bicep
dotnet test infra\tests\Demo2.Infrastructure.Tests.csproj
```

The extraction passed Bicep restore, build, parameter compilation, lint, and formatting with Bicep `0.45.15`.
The build emitted no warnings; the documented project-identity type exception remains explicit.
The 9 offline bootstrap tests passed with the existing .NET package versions.
The azd file passed its official JSON schema, and all source and hook paths resolved.

For a no-network bootstrap contract check, pass the project root and `--validate-only` to the bootstrap executable.
Supply synthetic endpoint and tenant values plus the documented model and reasoning inputs.
This path reads the bundled catalog and prompt without obtaining credentials.

## Remaining deployment gates

Fresh deployment, interactive sign-in, real token rejection, model invocation, Cosmos persistence, and approved dispatch require the parent's authorized cloud run.
Confirm regional model availability and capacity `500` before provisioning.
Graph application and enterprise-application creation require the documented directory permissions.
Runtime authentication telemetry has not been verified by infrastructure extraction.
Verify the deployed authentication behavior before claiming production compliance.

This deployment requires no Foundry azd extension: Bicep and the pinned .NET SDK perform the required operations.
Validate this project's declared requirements rather than requirements belonging to optional development tooling.

This demo retains a single region, one App Service instance, serverless Cosmos, and public endpoints protected by Entra authentication.
These choices preserve cost and operational simplicity but do not provide regional failover or network isolation.
A production design requires separate availability, network, credential, and compliance decisions.

## Sources

See `provenance.json` for the original commit and source hashes.
The extraction is an adapted copy, not a byte-identical infrastructure mirror.
Official Microsoft references were read on 2026-09-06:

- [Bicep best practices](https://learn.microsoft.com/azure/azure-resource-manager/bicep/best-practices).
- [Foundry MCP authentication and approval](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/model-context-protocol?view=foundry).
- [Connection schema, 2025-09-01](https://learn.microsoft.com/azure/templates/microsoft.cognitiveservices/2025-09-01/accounts/projects/connections).
- [Connection schema, 2026-05-01](https://learn.microsoft.com/azure/templates/microsoft.cognitiveservices/2026-05-01/accounts/projects/connections).
- [App Service Entra authentication](https://learn.microsoft.com/azure/app-service/configure-authentication-provider-aad).
- [Microsoft Graph Bicep applications and permissions](https://learn.microsoft.com/graph/templates/bicep/reference/applications?view=graph-bicep-1.0).
- [Application Insights connection strings](https://learn.microsoft.com/azure/azure-monitor/app/connection-strings).

The Microsoft documentation MCP tools were unavailable in this worker session.
The cited Microsoft Learn pages were retrieved directly over HTTPS instead.
