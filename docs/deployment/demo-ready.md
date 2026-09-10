# Demo-ready orchestration

`scripts\Invoke-DemoReady.ps1` prepares four owned demos from one checkout.
`scripts\Test-DemoReady.ps1` runs engineering checks without deployment or application startup.
Foundation and Ground are two Presenter sessions of Demo 1.
Both primary links open the shared comparison UI at `http://localhost:5090/`.
Patriots is a sixth, optional session with separate external ownership.
Tokens and Credits appears separately as an optional managed extra, not a seventh main session.

## Prerequisites

Install PowerShell 7, .NET 10, Git, Azure CLI, Azure Developer CLI, and Bicep.
Sign in with Azure CLI and Azure Developer CLI.
Use an account with the required resource, role-assignment, and Entra application permissions.
Confirm model availability, quota, and subscription policy in the selected regions.
Make the Microsoft package feed proxy available.
Trust the ASP.NET Core development certificate for the council HTTPS endpoint.
The retained council postprovision hook can request certificate trust.
The root readiness probe does not bypass certificate validation.

No original DEFRA-AI-Demos or cross-gov-assurance-board-demo checkout is required.
Do not copy old `.azure` files or generated `.env` files into a fresh environment.
Do not import old endpoints, credentials, or operational data.

## Invocation and contexts

Replace each location placeholder with a supported Azure location for your subscription.

```powershell
.\scripts\Invoke-DemoReady.ps1 -EnvironmentName psad-demo `
  -Demo1Location '<demo1-location>' -Demo2Location '<demo2-location>' `
  -CouncilLocation '<council-location>' -HostedLocation '<hosted-location>'
```

Add `-SubscriptionId <subscription-guid>` to select the target subscription explicitly.
Otherwise, the command uses the current Azure CLI subscription.
The existing helper reads the signed-in principal instead of requiring a stored principal ID.

| Deployment | Context | Environment suffix | Preparation |
|---|---|---|---|
| Demo 1 | `deploy\demo1` | `-demo1` | Provision, publish both prompt agents and knowledge content, build and start the local comparison UI. |
| Demo 2 | `src\PublicSectorAgentDemos.Demo2.Act` | `-demo2` | Run the isolated azd workflow and its authentication and flood support bootstrap hooks. |
| Demo 3 | `src\PublicSectorAgentDemos.Demo3.Coordinate` | `-demo3` | Provision, generate `.env`, build and start the local Council application. |
| Demo 4 | `deploy\demo4` | `-demo4` | Prepare Entra identity, run existing azd hooks, configure browser reply URI. |
| Patriots, when no local environment exists | External `azure-ai-mgs-patriots` | `-patriots` | Create an environment with Foundry IQ defaults, run `azd up`, then build and start the local application. |
| Tokens and Credits, when no local environment exists | External `tokens-and-credits` | `-tokens` | Run its existing `azd up` workflow and postprovision hook, then build and start the local application. |

The command reads `azd env list --output json` before selecting or creating an environment.
It creates an environment only after a successful list proves that the name is absent.
Authentication, CLI, parsing, and selection failures stop setup.
Normal reruns preserve environment names and contain no teardown operation.
Use the same location and subscription for a rerun of an existing deployment.

For each selected optional checkout, startup applies a stricter rule.
It inspects `azd env list --output json` in that checkout.
If one or more environments exist, startup assumes that checkout is already deployed.
It selects `-PatriotsEnvironmentName`, `-TokensAndCreditsEnvironmentName`, or the configured setup-file name.
Without a configured name, it requires and selects the single default environment.
It runs no `azd up`, `azd provision`, or `azd deploy` command for an existing optional environment.

If no local environment exists, startup creates a deterministic name.
The defaults are `<EnvironmentName>-patriots` and `<EnvironmentName>-tokens`.
A configured optional environment name replaces the default.
New environments use the confirmed subscription, selected optional location, and signed-in principal.
The optional location parameters default to `swedencentral`.

For a new council environment without Web IQ credentials, setup selects `foundryiq`.
Supply `COUNCIL_GROUNDING_PROVIDER=webiq` and `WEBIQ_API_KEY` to request Web IQ explicitly.
Existing provider values take precedence over process defaults.
Invalid providers and Web IQ without credentials fail before provisioning.
Setup preserves model selections, round settings, telemetry, and approval boundaries.
It never forces one debate round.
The generated council `.env` must match the selected provider and round setting.
Without a round override, the root command reads the default from the retained template.
This check matters because the council loads `.env` values over its process environment.
The unchanged council application performs its knowledge and agent initialization during startup.
The root command requires current startup success messages as well as HTTP readiness.
Suppressed information logs or a caught initialization failure block readiness instead of producing a false success.
The report distinguishes HTTP readiness, logged startup success, and an unverified live scenario.
Startup messages do not prove that a live deliberation will succeed.

## Bundled path boundary

The two bundle paths are fixed under `src` and follow the `PublicSectorAgentDemos.DemoN.Stage` folder convention.
Each bundle retains its nested `src` directory, build and package settings, SDK selection, namespaces, and assembly names.
Required files must belong to the main Git working tree.
The resolver rejects nested Git metadata and linked paths that could redirect deployment.
It never creates `.git` inside a bundle.

The legacy `-DefraRepoPath` and `-AssuranceBoardRepoPath` parameters accept only their exact bundled paths.
Legacy environment aliases must match the names derived from `-EnvironmentName`.
Remove external overrides from `PSAD_DEFRA_REPO_PATH`, `PSAD_ASSURANCE_BOARD_REPO_PATH`, or the setup file before startup.
The resolver rejects them instead of deploying an external checkout.
Legacy setup-file environment names are ignored with a warning.

## Optional Patriots

Patriots remains external and is selected only through `-Patriots` or `-IncludePatriots`.
The default startup does not discover its checkout, inspect its environment, or reserve its ports.

Use `-Patriots -PatriotsRepoPath <absolute-path>` to select its checkout explicitly.
Alternatively, use `-IncludePatriots`.
Startup resolves the path from the parameter, `PSAD_PATRIOTS_REPO_PATH`, the setup file, or the named sibling folder.
It clones the approved repository only when the selected setup-file or sibling path is absent.

Startup then inspects the checkout's local azd environments.
If any environment exists, it selects the configured environment or single default.
It performs no Azure deployment command.
If no environment exists, it creates `<EnvironmentName>-patriots`, unless an optional name is configured.
Use `-PatriotsLocation` to select the new environment's location.
Use `-PatriotsEnvironmentName` or `repositories.patriots.azdEnvironment` to select or name the environment.

For a new Patriots environment, startup sets:

| azd value | Value |
|---|---|
| `AZURE_SUBSCRIPTION_ID` | Confirmed subscription |
| `AZURE_LOCATION` | Selected Patriots location |
| `AZURE_PRINCIPAL_ID` | Signed-in principal |
| `COUNCIL_GROUNDING_PROVIDER` | `foundryiq` |
| `WEBIQ_CONNECTION_NAME` | Empty |
| `WEBIQ_API_KEY` | Empty |

It then runs `azd up`, builds the local web project, starts ports 5081 and 7081, and checks both endpoints.

The optional setup file defaults to `.demo-ready\repositories.local.json`; `-SetupPath` selects another file.
Its schema is `scripts\DemoReady\repositories.v1.schema.json`.
The checked-in example includes placeholder paths and optional environment names.
Replace or remove each optional entry before copying the example.
Bundled-only setup can omit the file or use:

```json
{
  "version": 1,
  "repositories": {}
}
```

Store only paths and optional azd environment names.
Both optional entries permit `azdEnvironment`.
If environments exist, the configured name must match one of them.
If none exist, startup creates the configured name.
Never store credentials, tokens, connection strings, tenant IDs, or subscription IDs.
The setup agent `.github\agents\demo-setup.agent.md` validates bundled defaults and optional external paths without deployment.

An unselected Patriots integration appears as **Not configured**, not **Ready**.
When selected, checkout, environment, deployment, build, process, health, or Git-preservation failures stop startup.

## Optional Tokens and Credits extra

Tokens and Credits remains in its external `tokens-and-credits` checkout.
The repository manages its optional Azure environment and local process, not its tracked source.
It appears in Presenter's **Optional extras** section, separate from the six main sessions.

```powershell
.\scripts\Invoke-DemoReady.ps1 -EnvironmentName psad-demo `
  -TokensAndCredits `
  -TokensAndCreditsRepoPath <absolute-path-to-tokens-and-credits>
```

Resolve the checkout in this order:

1. `-TokensAndCreditsRepoPath`.
2. `PSAD_TOKENS_AND_CREDITS_REPO_PATH`.
3. `repositories.tokensAndCredits.path` in the file selected by `-SetupPath`, defaulting to `.demo-ready\repositories.local.json`.
4. The sibling folder named `tokens-and-credits`.

A selected checkout can use `-TokensAndCreditsEnvironmentName` or `repositories.tokensAndCredits.azdEnvironment`.
Empty optional settings use the deterministic name only when the checkout has no local environment.
Existing setup files need no migration.
The committed extra has `configured=false` and `startupStatus=not-configured` before startup resolves its checkout.

Startup first inspects the checkout's local azd environments.
If any environment exists, it selects the configured environment or single default.
It performs no Azure deployment command.
If no environment exists, it creates `<EnvironmentName>-tokens`, unless an optional name is configured.
Use `-TokensAndCreditsLocation` to select the new environment's location.
Startup sets the confirmed subscription, selected location, and signed-in principal.
It then runs the checkout's existing `azd up` workflow.
The existing postprovision hook writes the endpoint and tenant into the ignored local application configuration.

Startup builds and runs only `src\TokensAndCredits.Web\TokensAndCredits.Web.csproj` from that checkout.
It first restores that project's packages through the Microsoft NuGet proxy, then builds the project.
It installs no SDK or tools and does not build an external solution.
Runtime uses `dotnet run --no-build --no-restore --no-launch-profile`.
The only process override is `ASPNETCORE_URLS=http://localhost:5041`.
It preserves existing choices without importing Demo 1 settings.
An existing optional environment receives no azd value write and no deployment command.
For a new environment, normal azd and postprovision files remain ignored in the external checkout.
Build outputs remain in the checkout's normal ignored `bin` and `obj` directories.
Startup compares external Git status with its baseline.
Masked logs, process records, generated catalog data, and readiness state remain under the main repository's `.demo-ready` directory.

Startup and Presenter warm-up use `GET /api/embeddings/manifest`.
The application does not expose `/health`.
The response must have HTTP 200, `origin=local-static-embedding`, and positive integer `dimensions` and `vocabularyCount` values.
This request reads local manifest metadata.
It requests no Azure token.
It makes no model discovery, chat, image, or live embedding call.

## Optional model capacity

The plan includes optional model quota only when that optional environment is new.
Reused optional environments add zero model deployments and zero capacity units to the plan.

| Optional deployment | Type | Model | SKU | Capacity |
|---|---|---|---|---:|
| Patriots | Embedding | `text-embedding-3-small` | `GlobalStandard` | 30 |
| Patriots | Chat | `gpt-5.4` | `GlobalStandard` | 100 |
| Patriots | Chat | `gpt-5-mini` | `GlobalStandard` | 1000 |
| Patriots | Chat | `gpt-5-nano` | `GlobalStandard` | 1000 |
| Patriots | Chat | `grok-4.3` | `GlobalStandard` | 500 |
| Tokens and Credits | Chat | `gpt-5.6-sol` | `GlobalStandard` | 3 |
| Tokens and Credits | Chat | `gpt-5.4` | `GlobalStandard` | 3 |
| Tokens and Credits | Chat | `gpt-5.4-mini` | `GlobalStandard` | 3 |
| Tokens and Credits | Image | `gpt-image-1.5` | `GlobalStandard` | 1 |
| Tokens and Credits | Embedding | `text-embedding-3-small` | `GlobalStandard` | 10 |

Patriots requests 5 deployments and 2630 capacity units.
Tokens and Credits requests 5 deployments and 20 capacity units.

The readiness report records the result in `external.tokensAndCredits.status`:

| Status | Meaning |
|---|---|
| `not-configured` | Tokens and Credits was not selected. Startup skips environment, build, start, and health work. |
| `failed` | A prior optional attempt failed. The current selected run writes the root readiness report as failed. |
| `ready` | The managed process started and the manifest response met the readiness contract. |

A selected Tokens and Credits failure stops startup and cannot appear as a successful extra.
A busy port produces failure; startup never adopts an unknown listener.
Default stop includes only recorded `tokens-and-credits` processes that still pass the ownership and process identity checks.
The record must identify this repository through `optionalExternalOwner` and the exact external project and working directory.
This local process ownership does not extend to Patriots.

## Local processes and endpoints

| Application | HTTP | HTTPS |
|---|---|---|
| Bundled council | `http://localhost:5080/` | `https://localhost:7080/` |
| Presenter | `http://localhost:5088/` | Not used |
| Foundation/Ground UI | `http://localhost:5090/` | Not used |
| Optional managed Tokens and Credits extra | `http://localhost:5041/` | Not used |
| Optional managed Patriots application | `http://localhost:5081/` | `https://localhost:7081/` |

Act and Hosted endpoints come from the current owned environments.
The root command starts the Foundation/Ground UI, council, and Presenter.
It also manages Tokens and Credits when it discovers a valid checkout.
It requires `GET /health` from the Foundation/Ground UI.
It also waits for the council and Presenter endpoints and the Act and Hosted health endpoints.
HTTP 2xx and 3xx indicate endpoint readiness.
A sign-in redirect proves reachability, not successful authentication or a completed business scenario.
The command does not run the Presenter warm-up or approve any action.

The Foundation/Ground UI source is `src\PublicSectorAgentDemos.Demo1.Web`.
Startup passes the existing Demo 1 environment values, including `AZURE_AI_FOUNDRY_ENDPOINT`, to its process.
It selects the signed-in Azure CLI identity through `AZURE_TOKEN_CREDENTIALS=AzureCliCredential`.
The UI has no separate azd environment or Azure deployment.

Cross-Government dossiers remain under `demo-dossiers\cross-government`.
Patriots dossiers remain under `demo-dossiers\patriots`.
Generated links use these separate paths.

## State, telemetry, and freshness

Generated reports, catalogs, process records, and masked logs stay under `.demo-ready`.
Nested `.azure` and generated `.env` files remain ignored.
Each bundle keeps its existing `.azure` directory at the bundle root.
The council keeps its generated `.env` under `src\GovernanceCouncil.Web` inside its bundle.
Relocation preserves this local state; it does not create or select an azd environment.
Environment templates and bundle provenance remain tracked.
The command checks that tracked and untracked repository status has not changed.

Every startup replaces stale readiness with `in-progress`.
Errors replace it with `failed` and the current phase, without old endpoints.
Only the current environment outputs populate a successful report and generated catalog.
The report distinguishes four owned deployments, five owned sessions, and the optional external applications.
Each optional entry records the azd environment name and whether it existed before startup.
It also records whether this run created and deployed the environment.

The existing Demo 1 and Demo 4 telemetry and Entra preparation remain in place.
Demo 2 and council keep their bundle-specific telemetry and authentication hooks.
The local Presenter and Foundation/Ground UI use Demo 1 Application Insights.
Tokens and Credits retains its own azd and application configuration.
No telemetry setting is copied between demonstrations.

## Stop or restart

```powershell
.\scripts\Stop-DemoReady.ps1
```

The default stop scope includes owned local applications and recorded Tokens and Credits processes.
The script checks PID, exact start time, executable, and required command marker.
It also recognizes known pre-relocation council records without weakening those identity checks.
When a working directory is recorded, it must match the same current or legacy layout as the council project marker.
It captures descendants before terminating the named job.
If a child survives, it stops only a captured PID whose identity still matches.
Failed or unverified records remain available for explicit review.
Patriots records remain unchanged, even when the caller explicitly selects Patriots.
No process-name kill or Azure operation is used.

Startup uses the same owned filter before restarting.
An unrelated process occupying an owned port blocks startup instead of being terminated.

## Validation

```powershell
.\scripts\Test-DemoReady.ps1 -EnvironmentName psad-demo
```

Default checks run the existing automation harness, root solution tests, bundle builds, and Bicep compilation.
`-SkipBundledBuild` omits the two bundle builds; `-SkipExternalBuild` remains a deprecated alias.
The validation command never builds or requires Patriots.
An owned running application can lock build outputs; stop owned applications before full validation.

Use `-RunAzurePreview` to preview all four existing environments.
Use `-RunCloudTests` for the existing Demo 1 and Demo 4 cloud checks.
Those checks require deployed environments.
The default local checks prove neither deployment nor cloud readiness.

Run the targeted automation harness separately:

```powershell
pwsh -NoProfile -File .\tests\automation\Invoke-DemoReady.Tests.ps1
```

## Teardown

Normal setup does not delete resources or application registrations.
Use the teardown command to reverse the most recent startup selection:

```powershell
.\scripts\Remove-DemoReadyAzure.ps1 -EnvironmentName psad-demo
```

The interactive command uses the same four-step console experience as startup:

1. Confirm the Azure subscription and environment base.
2. Select each deployment to remove, using the readiness report as the default.
3. Choose whether to keep Patriots and Tokens and Credits on disk.
4. Review every Azure, identity, process, repository, and generated-state action before confirmation.

No removal starts before the final confirmation.
The command stops the local applications and runs `azd down --purge` for the selected `demo1` through `demo4` environments.
It also removes an optional environment when the matching readiness report records `azdEnvironmentCreatedByThisRun=true`.
It never removes an optional environment that existed before startup.
It then explicitly purges matching soft-deleted Azure AI accounts and Key Vaults, including when the resource group is already absent.
It also removes the owned Demo 4 app registration.
It also removes generated readiness and Presenter state. It removes process state only when no protected records remain.

Optional Azure teardown occurs before optional checkout deletion.
If startup cloned Patriots or Tokens and Credits, teardown removes those checkouts only after Git is clean and synchronized with its upstream.
Keep both optional checkouts on disk with:

```powershell
.\scripts\Remove-DemoReadyAzure.ps1 -EnvironmentName psad-demo -KeepPatriotsAndTokensAndCredits
```

`-KeepPatriotsAndTokensAndCredits` controls disk retention only.
It does not retain an optional Azure environment created by startup.

Use `-Demo1`, `-Demo2`, `-Demo3`, `-Demo4`, or `-All` to override the selection recorded in the readiness report.
Use `-NonInteractive` for automation. Without matching readiness state, non-interactive teardown requires an explicit demo selection.
The command does not remove unrelated repositories, identities, environments, resources, or protected process state.

## References

- [Azure Developer CLI environments](https://learn.microsoft.com/azure/developer/azure-developer-cli/manage-environments)
- [Foundry tracing](https://learn.microsoft.com/azure/foundry/observability/how-to/trace-agent-client-side)
- [Application Insights Entra authentication](https://learn.microsoft.com/azure/azure-monitor/app/azure-ad-authentication)
