# PublicSectorAgentDemos

This repository supports a live demonstration of agent maturity on Microsoft Foundry.
It contains four independently deployed demos, a local Foundation/Ground UI, and a local Presenter application.

## Ownership

| Owned deployment | Source and deployment context | Scope |
|---|---|---|
| Demo 1 | `src\PublicSectorAgentDemos.Demo1.FoundationAndGround`, `src\PublicSectorAgentDemos.Demo1.Web`, `deploy\demo1` | Foundation and Ground share one deployment and a local comparison UI. |
| Demo 2 Act | `src\PublicSectorAgentDemos.Demo2.Act` | Cross-government flood support and approval-controlled MCP reservations, adapted from DEFRA-AI-Demos. |
| Cross-Government Council | `src\PublicSectorAgentDemos.Demo3.Coordinate` | Bundled council application and its standalone infrastructure. |
| Demo 4 Hosted | `src\PublicSectorAgentDemos.Demo4.*`, `deploy\demo4` | Hosted agent, application, and Memory contract. |

The bundle provenance files record their upstream source and adaptation boundaries.
The applications keep independent build settings and deployment contexts.
Each bundle retains its nested `src` directory and its original namespaces and assembly names.
Presenter is a supporting application, not another Azure deployment.
The Foundation/Ground UI also uses the existing Demo 1 deployment; it adds no deployment context.
The [Act runbook](docs/runbooks/act-flood-support.md) explains the fictional flood scenario and the exact reservation approval boundary.

**Patriots remains optional and external.**
The repository commands never copy, deploy, build, start, stop, or reconfigure Patriots.
The four owned demos need neither its checkout nor its environment.

## Fresh setup

Install PowerShell 7, .NET 10, Git, Azure CLI, Azure Developer CLI, and Bicep.
Sign in with Azure CLI and Azure Developer CLI.
Confirm the required model quota and permissions before deployment.
Use the Microsoft package feed proxy configured in `NuGet.config`.

Run the root command with one logical presentation base.
Replace each location placeholder with a supported Azure location for your subscription.

```powershell
.\scripts\Invoke-DemoReady.ps1 -EnvironmentName psad-demo `
  -Demo1Location '<demo1-location>' -Demo2Location '<demo2-location>' `
  -CouncilLocation '<council-location>' -HostedLocation '<hosted-location>'
```

Use `-SubscriptionId <subscription-guid>` to select a subscription explicitly.
Otherwise, the command uses the current Azure CLI subscription.
The existing identity helper obtains the signed-in principal.
No workstation path, old endpoint, `.azure` state, or original sibling checkout is required.

| Context | Derived environment |
|---|---|
| `deploy\demo1` | `psad-demo-demo1` |
| `src\PublicSectorAgentDemos.Demo2.Act` | `psad-demo-demo2` |
| `src\PublicSectorAgentDemos.Demo3.Coordinate` | `psad-demo-demo3` |
| `deploy\demo4` | `psad-demo-demo4` |

The command creates missing environments, runs each demo's hooks, and generates current Presenter links.
Both Foundation and Ground open the local comparison UI at `http://localhost:5090/`.
A new council environment uses Foundry IQ when no Web IQ credentials are supplied.
Explicit valid grounding choices and council round settings remain unchanged.
The command does not perform teardown.

Remove the deployments and local state created by the latest startup run:

```powershell
.\scripts\Remove-DemoReadyAzure.ps1
```

With no options, the interactive command confirms the Azure target, asks which demos to remove, asks whether to keep the optional checkouts, and shows the complete teardown plan before confirmation.
Teardown uses `azd down --purge`, then explicitly purges matching soft-deleted AI accounts and Key Vaults.
Use `-NonInteractive` with explicit selection arguments for automation.

Keep the optional Patriots and Tokens and Credits checkouts on disk:

```powershell
.\scripts\Remove-DemoReadyAzure.ps1 -KeepPatriotsAndTokensAndCredits
```

## Optional external applications

Bundled setup needs no `.demo-ready\repositories.local.json`.
The example at `scripts\DemoReady\repositories.example.json` includes a placeholder path for the optional Tokens and Credits application.
Replace the placeholder with a real checkout path, or remove that optional entry before using the example.
The setup agent at `.github\agents\demo-setup.agent.md` validates the bundled defaults.
Existing Patriots-only setup files remain valid without migration.

### Related repositories

| Repository | Purpose |
|---|---|
| [MGS Patriots](https://github.com/garylumsden/azure-ai-mgs-patriots) | Optional council demonstration used for the Patriots comparison. |
| [Azure Agent Council Template](https://github.com/garylumsden/azure-agent-council-template) | Reusable council application template that underpins the Coordinate pattern. |
| [Tokens and Credits](https://github.com/garylumsden/tokens-and-credits) | Optional local application that explains model tokens, embeddings, and credits. |

### [MGS Patriots](https://github.com/garylumsden/azure-ai-mgs-patriots)

To include an independently running Patriots application:

```powershell
.\scripts\Invoke-DemoReady.ps1 -EnvironmentName psad-demo `
  -PatriotsRepoPath <absolute-path-to-azure-ai-mgs-patriots>
```

Alternatively, use `-IncludePatriots` with a stored path, `PSAD_PATRIOTS_REPO_PATH`, or the named sibling checkout.
Startup reuses or clones the checkout, builds it, and starts its local application.
Without opt-in, Presenter shows **Not configured** and makes no Patriots warm-up request.
A Patriots failure does not block the five owned sessions across four deployments.
Legacy DEFRA or council external path overrides fail before deployment.

### [Tokens and Credits](https://github.com/garylumsden/tokens-and-credits)

Tokens and Credits is an optional managed local application from the external `tokens-and-credits` repository.
Presenter lists it under **Optional extras**, separate from the six main sessions.

```powershell
.\scripts\Invoke-DemoReady.ps1 -EnvironmentName psad-demo `
  -TokensAndCreditsRepoPath <absolute-path-to-tokens-and-credits>
```

Discovery checks the parameter, `PSAD_TOKENS_AND_CREDITS_REPO_PATH`, `repositories.tokensAndCredits.path` in the selected setup file, then the named sibling checkout.
A valid discovered checkout is included automatically; there is no `IncludeTokens` switch.
Startup builds only `src\TokensAndCredits.Web\TokensAndCredits.Web.csproj` and binds `http://localhost:5041` without its launch profile.
It preserves the checkout's existing configuration and credential choices instead of importing Demo 1 settings.
Readiness requires HTTP 200 and valid local manifest metadata from `GET /api/embeddings/manifest`, not `/health`.
This request makes no model call and requires no azd environment.
Missing checkouts remain **Not configured** with a warning.
Invalid checkouts or build, port, process, health, or Git-preservation failures produce **Failed** without blocking the main presentation.
Startup makes no external source, configuration, provisioning, or deployment changes.

## Stop and validate

```powershell
.\scripts\Stop-DemoReady.ps1
.\scripts\Test-DemoReady.ps1
```

Stop targets managed local processes with matching identities, including recorded Tokens and Credits processes.
It preserves Patriots processes and registry records, even when Patriots is explicitly selected for stopping.
Validation runs the existing automation harness, root solution checks, bundled application builds, and Bicep compilation.
Use `-RunAzurePreview` for the four existing environments.
Use `-RunCloudTests` for the existing Demo 1 and Demo 4 cloud checks.
Validation does not deploy or start the presentation.

Run only the orchestration harness:

```powershell
pwsh -NoProfile -File .\tests\automation\Invoke-DemoReady.Tests.ps1
```

See the [demo-ready runbook](docs/deployment/demo-ready.md) for setup order and ownership boundaries.
See [Presenter mode](docs/presenter/RUNBOOK.md) for session instructions.
See [repository conventions](docs/CONVENTIONS.md) before changing application code or data.

## Build the root solution

```powershell
dotnet restore .\PublicSectorAgentDemos.slnx
dotnet build .\PublicSectorAgentDemos.slnx --no-restore
dotnet test .\PublicSectorAgentDemos.slnx --no-build
```

The root solution lists the bundles as solution items, not build projects, to preserve their independent package versions.
There is no root `azure.yaml` and no combined infrastructure template.
Nested `.azure` state, generated `.env` files, logs, secrets, and build outputs remain ignored.
Environment templates remain tracked.
