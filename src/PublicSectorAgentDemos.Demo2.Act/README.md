# Demo 2: cross-government flood support

This is an **adapted copy** of the approved `DEFRA-AI-Demos` application source.
The source revision is `b9c808f90f2ec04b2b6f6ef53da039ef334d130f`.
The source repository remains the authoritative source for upstream application fixes.
Apply upstream fixes deliberately. Do not overwrite this bundle with an automatic directory copy.
The bundle root is `src\PublicSectorAgentDemos.Demo2.Act` in the host repository.
The nested `src` directory, namespaces, assembly names, and isolated build settings remain unchanged.

## Application scope

The bundle contains `Demo2.Web`, `Defra.Tools.Mcp`, and their five shared library projects.
It includes flood support configuration, prompts, JSON schemas, fictional fixtures, evaluation seeds, UI assets, and adapted upstream xUnit tests.
All project references resolve inside this bundle.
No other DEFRA application implementation is included.
Patriots is separate and is not a dependency.

Infrastructure, `azure.yaml`, deployment hooks, and infrastructure scripts have a separate implementation scope.
This document describes application behavior and the integration contract, not deployment completion.
The scenario change does not deploy Azure resources or replace an existing live deployment by itself.
See [the scenario runbook](../../docs/runbooks/act-flood-support.md) for the demonstration and approval boundary.

## Build and test

Run these commands from the host repository root:

```powershell
Set-Location .\src\PublicSectorAgentDemos.Demo2.Act
dotnet restore .\DEFRA-AI-Demos.slnx --configfile .\NuGet.config --verbosity quiet
dotnet build .\src\Demo2.Web\Demo2.Web.csproj -c Release --no-restore --verbosity quiet
dotnet build .\src\Defra.Tools.Mcp\Defra.Tools.Mcp.csproj -c Release --no-restore --verbosity quiet
dotnet test .\DEFRA-AI-Demos.slnx -c Release --no-restore --verbosity quiet
```

Use the bundle as the working directory so `global.json` selects the source SDK policy.
The source requires .NET 10 and permits the latest installed feature band.
The imported `Directory.Build.props`, `Directory.Packages.props`, and `global.json` remain unchanged.
`Directory.Build.targets` stops target discovery at the bundle boundary.
The local EditorConfig stops the host repository's code-style rules from changing the source build behavior.
The local NuGet configuration clears inherited sources and mappings.
All package operations use `https://packagefeedproxy.microsoft.io/nuget/v3/index.json`.
Original package versions remain unchanged, including unused central package entries.

The solution contains only the seven application projects and four focused test projects.
Tests cover assessment rules, approvals, rejected or missing approvals, replay prevention, ownership, persistence, audit redaction, telemetry, and MCP.
Integration tests use real Kestrel hosts on loopback port 0.
They stop those hosts gracefully and do not use presentation ports.
The local token test double records requested scopes; it does not contact Entra.
The tests do not establish live Foundry, Cosmos DB, or App Service authentication readiness.

## Flood support boundary

| Contract | Value |
|---|---|
| azd service | `demo2-web` |
| Web project | `src\Demo2.Web` |
| azd service | `defra-tools` |
| MCP project | `src\Defra.Tools.Mcp` |
| MCP endpoint | `/mcp/flood-support` |
| MCP server | `cross-government-flood-support-mcp` |
| Read tools | `getSituationReports`, `findAccommodation`, `checkTransportCapacity` |
| Approval-gated action | `reserveSupportPackage` |
| Tool connection | `flood-support-connection` |
| Agent | `cross-government-flood-support` |
| Agent configuration | `config\agents\catalog.v1.json` |
| Agent instructions | `config\agents\demo2\flood-support.md` |
| Deterministic rules | The scenario file selected by `Demo2:RulesPath` |
| Tool policy | `config\policies\demo2-tools.v1.json` |

The agent catalog contains only the flood support entry.
`reserveSupportPackage` always requires approval. The three read tools do not.
Deployment setup retains this approval requirement and the original App Service authentication boundary.
The MCP action authorizer trusts only the principal header that App Service Easy Auth injects.
Do not expose a production MCP process directly without that platform authentication boundary.

The application still consumes `AZURE_AI_PROJECT_ENDPOINT`, `AZURE_AI_MODEL_DEPLOYMENT_NAME`, and `AZURE_AI_REASONING_EFFORT`.
The toolbox uses `DEFRA_TOOLS_MCP_URL` and `DEFRA_TOOLS_AUDIENCE`.
The agent catalog resolves `DEFRA_TOOLS_MCP_FLOOD_SUPPORT_URL` during bootstrap.
Cosmos configuration uses `AZURE_COSMOS_ENDPOINT`, `AZURE_COSMOS_DATABASE_NAME`, and `AZURE_COSMOS_CONTAINER_NAME`.
Managed identity uses `AZURE_TENANT_ID` and `AZURE_CLIENT_ID`.
The MCP action allowlist uses `MCP_FLOOD_SUPPORT_ACTION_CALLER_PRINCIPAL_ID`.
These are setting names, not supplied environment values.

`Demo2.Web` supports local deterministic simulation and the deployed Foundry agent.
Local simulation can demonstrate approval without claiming a live Foundry or remote MCP invocation.
All resource reservations remain fictional in both modes.
The scenario uses the separate `demo2-flood-support-cases` Cosmos container when deployed.
It does not reinterpret or delete old welfare records.

The approval modal explains the exact package, requested resources, duration, estimated cost, justification, evidence, risks, and unmet needs.
Approval binds the incident and package version. Rejection creates no reservation.
Package changes, expired decisions, mismatched case ownership, and replay attempts cannot silently bypass approval.
The application retains the imported project names and internal compatibility identifiers to preserve build and persistence boundaries.

## Historical provenance and bounded pruning

`provenance\import-manifest.json` records all 161 imported tracked files before adaptation.
Each entry records its SHA256 hash and byte count, including the source checkout's CRLF line endings.
All 161 destination files matched before any adaptation.
The unchanged application baseline built both applications and passed 130 tests.
`provenance\baseline-validation.json` records that evidence and the welfare endpoint contract.

Pruning removed complete outbreak and water/planetary dependency groups.
The changes removed their registrations, startup initializers, discovered tools, providers, validation, catalog entries, schemas, and fixture references.
Only after those consumers were removed were their fixtures removed.
The outbreak stage passed 126 tests before the water/planetary stage started.
The final application and test results are recorded in `provenance\pruning-manifest.json`.

Those records describe the initial welfare import and pruning, not byte parity after the flood support adaptation.
The adaptation replaces the active scenario, tools, policy data, approval context, and corresponding tests.
It retains the shared authentication, audit, telemetry, and guarded state-transition mechanisms.
Tests retain upstream safety assertions where applicable.
The web integration harness uses an in-process host instead of creating and killing a child process.
