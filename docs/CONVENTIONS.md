# Repository conventions

## Scope

This repository contains four independent demo deployments, the Presenter, and the demo orchestration scripts.
Foundation and Ground are two sessions of Demo 1.
Act is bundled under `src\PublicSectorAgentDemos.Demo2.Act`.
Cross-Government Coordinate is bundled under `src\PublicSectorAgentDemos.Demo3.Coordinate`.
Their provenance files identify the approved upstream source and adaptations.
Preserve each bundle's nested `src` directory, build and package boundaries, SDK selection, namespaces, and assembly names.
Apply upstream fixes deliberately instead of replacing a bundle with an automatic directory copy.

## Source layout

Projects under `src\Shared` are used by more than one owned application or by the Presenter.
Keep shared contracts, identity, telemetry, and their source files in that folder.
Keep code used by only one demo inside that demo's bundle.
The root `PublicSectorAgentDemos.slnx` groups the shared projects under `Platform/Shared` and each demo under its own solution folder.

Patriots Coordinate remains optional and external in `azure-ai-mgs-patriots`.
Do not copy its source, configuration, or data into this repository.
Use the resolution and azd ownership contract in `scripts\DemoReady\External.ps1`.

Tokens and Credits remains in the external `tokens-and-credits` repository.
It is an optional Azure-backed local application, separate from the six main Presenter sessions.
Its local process can be started and stopped through recorded process ownership.
Do not copy or edit its tracked source.

## External repositories

After explicit Patriots opt-in, resolve its path in this order: parameter, environment variable, the ignored
setup file `.demo-ready\repositories.local.json`, then a sibling folder.
Do not use external checkout overrides for the bundled Act or council applications.
Patriots opt-in reuses or clones its checkout and starts its local application.
Reuse an existing local azd environment without deployment.
Create and deploy a deterministic optional environment only when no local environment exists.
Tokens and Credits resolves its path from the parameter, environment variable, setup-file entry, then the named sibling folder.
Keep Tokens and Credits failures separate from main presentation readiness.
Its local manifest readiness request makes no model call.
Use its existing `azd up` and postprovision workflow only for a new optional environment.

Never commit a workstation-specific absolute path. Never write a credential, a connection string,
a tenant identifier, or a subscription identifier to the setup file.

Never edit or commit tracked source in an external repository from this repository.
For selected optional applications, manage only the azd environment lifecycle and ignored local configuration described above.
Preserve the exact Git status of every external working tree. Treat a change of that status as a failure.

## Authentication

Use identity-based authentication for every Azure service.

Resolve one shared `TokenCredential` through `AddDemoAzureIdentity`.
This shared-helper rule applies to root projects; the imported bundles retain their existing authentication implementations.

The helper uses `DefaultAzureCredential`. Do not add a hardcoded key, connection string, password,
token, or secret.

Use managed identity and Azure role-based access control for deployed workloads.

## Infrastructure

Define every owned Azure resource in Bicep.

Use `azure.yaml` as the Azure Developer CLI entry point of each owned demo. This repository has no
root `azure.yaml` and no unified root composition.

Do not create a required resource manually in the Azure portal.

## Observability

Register OpenTelemetry through `AddDemoObservability` in root applications.

Use `DemoTelemetry.ActivitySource` for a custom span. A span is one timed operation in a
distributed trace.

Root applications use the shared observability helper.
Imported bundles retain their own telemetry implementations and deployment hooks.

Use workspace-based Application Insights for each owned Azure demo context.
Disable local Application Insights authentication.
Connect each owned Foundry project with `ProjectManagedIdentity`.
Microsoft Entra trace ingestion for Foundry agents is in public preview.
Grant Monitoring Metrics Publisher only to the project and application identities that emit
telemetry.
Pass `APPLICATIONINSIGHTS_CONNECTION_STRING` only through the runtime environment.
Do not write this value to source, reports, process state, or logs.

The helper uses Azure Monitor when the Application Insights value is present.
It uses `DefaultAzureCredential` for ingestion.
`OTEL_EXPORTER_OTLP_ENDPOINT` remains an optional additional local exporter when Application
Insights is absent.

Each external demo owns its own telemetry. Do not validate it, do not require it, and do not
share an owned Application Insights component with it.

## Data

Use demonstration data only.

Every root JSON fixture must contain a `"dataVersion"` (or `"version"`) value at the root.
Imported fixtures retain their existing contracts and provenance.

Do not store real case, entity, citizen, or staff information.

Create a new version folder when a fixture contract changes.

## C# and .NET

Target .NET 10 and C# 13 in root projects.
Imported bundles retain their existing SDK, language, analyzer, and package settings.

Enable nullable reference types and implicit global using directives.

Use file-scoped namespaces.

Prefer records for immutable contracts.

Use primary constructors when they make dependencies clear.

Keep root shared package versions in `Directory.Packages.props`.
Do not move bundle package versions into the root file.

## Bundle build isolation

Build each bundle from its own working directory so its `global.json` controls SDK selection.
Preserve bundle-local `Directory.Build.props`, `Directory.Build.targets` where present, `Directory.Packages.props`, `NuGet.config`, and `.editorconfig`.
Keep the bundles as root solution items, not root build projects.
Do not upgrade a package or rename a namespace or assembly as part of relocation.
Use only the Microsoft package feed proxy configured in each bundle's `NuGet.config`.
Keep each existing `.azure` directory and generated `.env` file within its bundle and ignored by Git.
Do not copy private environment state from an external checkout or commit it.

## Demonstration structure

Keep root shared typed contracts in `contracts`.

Keep project files in `src`.
Name demo source folders `PublicSectorAgentDemos.DemoN.Stage`.
Keep imported project names and nested `src` directories within their named bundle.

Keep root xUnit projects in `tests`. Keep imported tests inside their bundle boundaries.
Keep the orchestration harness in `tests\automation`.

Name the root stage data folders `ground` and `hosted`.

Keep the Presenter session catalog in `config\presenter\sessions.v1.json`. Keep it free of any
workstation-specific absolute path. Let the startup script generate every environment-specific
endpoint.
