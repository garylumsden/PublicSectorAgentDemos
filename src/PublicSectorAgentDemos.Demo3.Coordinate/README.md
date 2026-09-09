# Bundled Cross-Government Assurance Board

This bundle imports `cross-gov-assurance-board-demo` at commit `bc98334e4e61e233291f30ede79ed4b807eb7d1b`.
The upstream repository remains authoritative for application fixes.
All four `GovernanceCouncil` projects, scenario files, prompts, assets, and fictional dossiers retain their upstream bytes and paths.
There are no application changes or dependency upgrades.
The bundle root is `src\PublicSectorAgentDemos.Demo3.Coordinate` in the host repository.
All imported paths remain relative to this root, including the nested `src` directory.
Namespaces, assembly names, and isolated build settings remain unchanged.

`import-manifest.json` records each imported path, upstream Git blob, source SHA-256, and bundled SHA-256.
It also records excluded tracked files and bundle-only additions.
The import excludes live `.azure` state, generated `.env` files, build outputs, and nested Git metadata.
It excludes upstream `.github` instructions, maintenance scripts, and general design documents that the application and azd do not reference.
The upstream `infra\main.json` is a generated artifact, not an azd input; the bundle retains its complete Bicep source instead.

## Independent build

Install a .NET SDK that supports `net10.0` and PowerShell 7.
From the repository root, run:

```powershell
.\src\PublicSectorAgentDemos.Demo3.Coordinate\Build.ps1 -Configuration Release
```

The wrapper selects the bundle working directory before SDK resolution.
It builds `src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj` and all three project dependencies.
It does not start the application, run hooks, trust certificates, or access Azure resources.
NuGet restore uses only `https://packagefeedproxy.microsoft.io/nuget/v3/index.json`.

| Build boundary file | Purpose |
| --- | --- |
| `.gitignore` | Add one exception to the upstream file so the parent ignore rule cannot exclude the unchanged `launchSettings.json`. |
| `Directory.Build.props` | Stop parent build settings, including C# 13 and warnings-as-errors, without overriding upstream project settings. |
| `Directory.Packages.props` | Disable central package management; preserve every inline `PackageReference` version. |
| `global.json` | An empty object stops parent SDK configuration; use the latest installed SDK, as upstream does without this file. |
| `NuGet.config` | Retain the mandatory Microsoft package feed proxy, including when the bundle is copied elsewhere. |
| `.editorconfig` | Stop parent formatting and analyzer rules from affecting unchanged source. |
| `.gitattributes` | Disable Git text conversion throughout this bundle so commits and fresh clones retain imported bytes. |
| `Build.ps1` | Build from the bundle directory and propagate build failures. |

The required versions remain `Microsoft.Agents.AI` and `.Workflows` 1.11.1, `Microsoft.Agents.AI.Foundry` 1.5.0, and `Azure.AI.Projects` 2.0.1.
`OpenAI` remains 2.10.0; `Microsoft.Extensions.AI.OpenAI` remains 10.6.0.
The unchanged project files specify all other package versions.

## Exact infrastructure departure

The infrastructure departures are confined to `infra\resources.bicep`.
The `logAnalytics` resource moves `sku: { name: 'PerGB2018' }` from the resource root into `properties`.
Retention remains 30 days.
The policy, model deployments, and project creation are sequenced to avoid concurrent writes to the same AI account during fresh provisioning.
No resources, model deployments, identities, role assignments, parameters, or outputs are removed or renamed.
`azure.yaml`, all other Bicep files, `main.parameters.json`, both hooks, and the complete `.env.template` remain byte-identical.

The knowledge-base connection retains `authType: 'ProjectManagedIdentity'` and top-level `audience: 'https://search.azure.com/'`.
Official Foundry guidance identifies this authentication type specifically for `RemoteTool` project connections.
The current Cognitive Services TypeSpec contract also defines `ProjectManagedIdentity`.
Bicep's generic `2025-09-01` connection schema still reports `BCP036`; this warning is retained, not suppressed.
Current Foundry examples use a newer API version; local compilation does not prove service acceptance of the retained upstream version.
No authentication substitution or API upgrade is part of this import.

Compile locally with the installed standalone Bicep executable:

```powershell
Set-Location .\src\PublicSectorAgentDemos.Demo3.Coordinate
& "$HOME\.azure\bin\bicep.exe" build .\infra\main.bicep --no-restore --stdout | Out-Null
& "$HOME\.azure\bin\bicep.exe" lint .\infra\main.bicep --no-restore
```

Compilation and linting do not deploy resources.

## Parent setup contract

| Surface | Contract |
| --- | --- |
| Bundle root | `src\PublicSectorAgentDemos.Demo3.Coordinate` |
| azd project | Unchanged `azure.yaml`: `agent-council`, template `agent-council@0.0.1`, Bicep directory `infra`, no hosted application service. |
| Resource boundary | Separate subscription-scope deployment creates `rg-${environmentName}` with Foundry, four model deployments, Cosmos DB, Blob, Search, Key Vault, monitoring, and RBAC. |
| Deployment inputs | `AZURE_ENV_NAME`, `AZURE_LOCATION`, `AZURE_PRINCIPAL_ID`, subscription and tenant context; caller-selected content-policy and grounding settings. |
| Location | Use a customer-selected Azure location, subject to model availability, preview support, service capacity, and quota. |
| Postprovision | azd executes the platform hook from this bundle root; it expands the complete `src\GovernanceCouncil.Web\.env.template` into `.env`. |
| Hook side effects | If the tenant is absent, the hook reads `az account show`; it also invokes `dotnet dev-certs https --trust`. |
| Local application | `src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj`; default HTTPS port 7080 and HTTP port 5080. |
| Scenario | Nearest `config\scenario.json`, or an explicit `COUNCIL_SCENARIO_PATH`; member prompts remain in sibling `config\prompts`. |
| Dossiers | Parent upload copies live in repository-root `demo-dossiers\cross-government`; bundled `data\policies` remains valid for manual uploads. |
| Runtime | Foundry is the default; process variable `COUNCIL_AGENT_RUNTIME=maf` or `local` selects local Microsoft Agent Framework execution. |
| Rounds | Preserve caller-selected `COUNCIL_DEBATE_MAX_ROUNDS`; when absent, retain the upstream default of 5. |

For fresh environments without Web IQ access, the parent must select `COUNCIL_GROUNDING_PROVIDER=foundryiq` before provisioning and hook execution.
Preserve caller-selected `webiq` only when its required access and connection are valid.
The unchanged standalone defaults remain `webiq`; an empty `WEBIQ_API_KEY` prevents creation of the Web IQ connection.
The Foundry runtime uses the project connection; local MAF with Web IQ also needs `WEBIQ_API_KEY` in its environment.
Do not copy a source `.env` or `.azure` directory.

`DotEnvLoader` overwrites matching process variables with `.env` values at startup.
Ensure the generated `.env` reflects the selected provider and round count before application startup.
The upstream template leaves per-tier model overrides commented; retain deployment outputs as process variables when the parent needs these overrides.
The template does not set `COUNCIL_AGENT_RUNTIME`; pass that variable to the application process when selecting MAF.

All 22 upstream output names and expressions remain unchanged:

```text
AZURE_AI_FOUNDRY_ENDPOINT  AZURE_AI_SERVICES_ENDPOINT  AZURE_OPENAI_ENDPOINT
AZURE_AI_PROJECT_NAME  AZURE_RESOURCE_GROUP  AI_SERVICES_RESOURCE_NAME
EMBEDDING_DEPLOYMENT_NAME  COUNCIL_REASONING_MODEL  COUNCIL_SYNTHESIS_MODEL
COUNCIL_FAST_MODEL  COUNCIL_EMBEDDING_MODEL  KNOWLEDGE_BASE_CONNECTION_NAME
WEBIQ_CONNECTION_NAME  WEBIQ_MCP_URL  COUNCIL_GROUNDING_PROVIDER
COSMOS_DB_ENDPOINT  COSMOS_DB_DATABASE_NAME  STORAGE_ACCOUNT_NAME
SEARCH_SERVICE_ENDPOINT  SEARCH_SERVICE_NAME  APPINSIGHTS_CONNECTION_STRING
RAI_POLICY_NAME
```

The provisioned Cosmos database and the application's database name are both `governance-council`.
`COSMOS_DB_DATABASE_NAME` is an unchanged output, not an application-supported database rename switch.

## First-start conditions and readiness

The application enables Azure services only when `COSMOS_DB_ENDPOINT`, `STORAGE_ACCOUNT_NAME`, and `AZURE_AI_FOUNDRY_ENDPOINT` are nonempty.
These three values alone do not establish readiness.
Supply the generated endpoints, model deployments, Search settings, tenant, subscription, resource group, account name, and content-policy name.
Keep `GovernanceCouncil:ProvisionAgentsOnStartup` enabled for first-start initialization.

The application uses `DefaultAzureCredential`; the selected identity must match the intended tenant and authorized deploying user.
The templates grant user roles with `principalType: 'User'`; they do not define an unattended service-principal deployment contract.
Provisioning requires permission to create the resources and role assignments, plus sufficient model quota.
Allow role-based access control (RBAC) assignments to propagate before first startup.

| Identity | Required retained permissions |
| --- | --- |
| Local user | Azure AI User and Azure AI Project Manager on the project; Cognitive Services User, OpenAI User, and Contributor on the account. |
| Local user | Cosmos DB Built-in Data Contributor; Storage Blob Data Owner; Search Service Contributor, Search Index Data Contributor, and Search Index Data Reader. |
| Foundry project identity | Azure AI User on the project; Cognitive Services User on the account; Search reader/contributor/service roles; Blob contributor and Cosmos data contributor. |
| Foundry project identity | Key Vault Secrets User for Web IQ secrets; Reader on Application Insights for portal traces. |
| Search identity | Cognitive Services User and Cognitive Services OpenAI User on the Foundry account for retrieval and answer synthesis. |
| Foundry account identity | Retained Blob, Cosmos, and Search roles for service-managed operations. |

With Foundry IQ selected, startup creates or updates `gc-web-ks` and the configured knowledge base on Search.
The knowledge-base name must match `KNOWLEDGE_BASE_CONNECTION_NAME` and the RemoteTool target.
The scenario must contain grounding domains; the bundled scenario contains `gov.uk` and `learn.microsoft.com`.
The retained Search client and MCP target use `2026-05-01-preview`; service support and applicable preview terms remain deployment prerequisites.
The subsequent startup step provisions Foundry agents, or initializes local MAF agents when selected.
Startup logs provisioning failures but still starts the web application.
The parent must establish knowledge-base and agent readiness, not treat an HTTP response alone as success.

The copied application has no inbound user authentication configuration.
Keep this demonstration local and controlled. Production authentication requires separate design and validation.
No deployed identity telemetry or compliance status is established by this local import.
Validate the azd requirements declared by each deployment project.
An optional tooling extension's requirements are not automatically requirements of this standalone council project.

## Attribution and references

The pinned upstream tree has no standalone application `LICENSE` or `NOTICE` file.
This import does not assign a new license to upstream application content.
Confirm redistribution rights before distributing the application outside its authorized scope.
Cytoscape retains its complete MIT notice in `src\GovernanceCouncil.Web\wwwroot\js\cytoscape.min.js`.
Bootstrap retains its copyright headers; its bundled JavaScript also includes Popper code.
Complete Bootstrap and Popper MIT notices are included in `THIRD-PARTY-NOTICES.txt`, outside unchanged application assets.

References consulted on 2026-09-06:

- [MSBuild directory customization](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- [NuGet central package management](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- [.NET global.json SDK selection](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)
- [C# language-version defaults](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/configure-language-version)
- [Log Analytics 2022-10-01 resource contract](https://learn.microsoft.com/en-us/azure/templates/microsoft.operationalinsights/2022-10-01/workspaces)
- [Foundry IQ project connection authentication](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/foundry-iq-connect)
- [Cognitive Services TypeSpec contract](https://github.com/Azure/azure-rest-api-specs/blob/main/specification/cognitiveservices/CognitiveServices.Management/models.tsp)
- [Bicep CLI](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/bicep-cli)
- [Bootstrap 5.3.3 license](https://github.com/twbs/bootstrap/blob/v5.3.3/LICENSE)
- [Popper 2.11.8 license](https://github.com/floating-ui/floating-ui/blob/v2.11.8/LICENSE.md)
