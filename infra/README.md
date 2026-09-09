# Infrastructure

This folder holds the root Bicep templates for Demo 1 and Demo 4.
The repository also owns the Act and Cross-Government Coordinate bundles.

Each owned demo deploys through its own standalone subscription-scope entry point. There is no
root `azure.yaml` and no unified root composition.

Act retains its infrastructure under `src\PublicSectorAgentDemos.Demo2.Act\infra`.
Cross-Government Coordinate retains its infrastructure under `src\PublicSectorAgentDemos.Demo3.Coordinate\infra`.
Each bundle deploys from the `azure.yaml` at its bundle root.
Patriots Coordinate remains optional and external. This folder holds no template for it.

## Demo 1 Foundation and Ground

`demo1-main.bicep` is the standalone subscription-scope entry point.
`demo1-foundation-ground.bicep` and `demo1-standalone-resources.bicep` contain its resource-group
modules. `demo1-model-deployment.bicep` deploys one model to the Demo 1 AI Services account.
Use them with `deploy/demo1/azure.yaml`.

Build the templates:

```powershell
az bicep build --file infra\demo1-main.bicep
```

## Demo 4 Hosted agents

`demo4/main.bicep` is the standalone subscription-scope entry point.
`demo4/resources.bicep` contains its resource-group composition.
`demo4/main.bicepparam` supplies its parameters.
`demo4/hooks` contains the Foundry and hosted-agent configuration hooks.
Use them with `deploy/demo4/azure.yaml`.

Build the templates:

```powershell
az bicep build --file infra\demo4\main.bicep
az bicep build-params --file infra\demo4\main.bicepparam
```

Demo 4 keeps project-local package versions and does not use the root central versions.

Template validation does not deploy a resource.

## Shared rules

All service access uses managed identity and role-based access control. Do not hardcode a key, a
connection string, or a secret.

Every owned context creates workspace-based Application Insights with local authentication
disabled. Each owned Foundry project connection uses `ProjectManagedIdentity`. An application
receives the Application Insights connection only from a Bicep application setting or from the
startup process environment. The Azure Monitor exporter uses `DefaultAzureCredential`.

`scripts\Test-DemoReady.ps1` compiles the retained Bicep files during repository validation.
`scripts\Invoke-DemoReady.ps1` provisions only the demonstrations selected in its confirmed plan.
`scripts\Remove-DemoReadyAzure.ps1` reverses the recorded selection across Demo 1 through Demo 4.
It purges each azd environment and matching soft-deleted Azure AI accounts and Key Vaults.
