# Documentation

This folder contains the repository conventions and one runbook for each owned demonstration.

## Ownership

This repository owns four independent demo deployments, the Presenter, and the orchestration.

| Demo | Stage | Owner | Guide |
|------|-------|-------|-------|
| Demo 1 | Foundation and Ground with a local comparison UI | This repository | [Ground](runbooks/ground.md) |
| Demo 2 | Act: cross-government flood support | Bundled in this repository | [Flood support](runbooks/act-flood-support.md) |
| Demo 3 | Cross-Government Coordinate | Bundled in this repository | [Council bundle](../src/PublicSectorAgentDemos.Demo3.Coordinate/README.md) |
| Demo 3 | Patriots Coordinate | External `azure-ai-mgs-patriots` | Use the guide in that repository. |
| Demo 4 | Hosted agents | This repository | [Hosted](runbooks/hosted.md) |

## Operations

| Task | Guide |
|---|---|
| Prepare the owned demos and start their local applications | [Demo-ready orchestration](deployment/demo-ready.md) |
| Validate the repository without deployment | [Demo-ready orchestration](deployment/demo-ready.md) |
| Run the ordered session guide | [Presenter mode](presenter/RUNBOOK.md) |
| Add code, data, or configuration | [Repository conventions](CONVENTIONS.md) |
| Read the Azure template layout | [Infrastructure](../infra/README.md) |

The repository has two commands. `scripts\Invoke-DemoReady.ps1` makes the demonstration ready.
`scripts\Test-DemoReady.ps1` runs the tests, builds, and deep checks.

The startup script deploys an optional external checkout only when that checkout has no local azd environment.
It reuses an existing optional environment without a deployment command.
Guided teardown confirms the Azure target, selections, optional checkout handling, and complete plan.
It removes selected owned environments and optional environments created by the recorded startup run.
It can remove startup-created optional checkouts or keep them with `-KeepPatriotsAndTokensAndCredits`.

Act lives in `src\PublicSectorAgentDemos.Demo2.Act`.
Cross-Government Coordinate lives in `src\PublicSectorAgentDemos.Demo3.Coordinate`.
Their provenance files identify the upstream repositories and retained adaptation boundaries.
Patriots Coordinate remains optional and external in `azure-ai-mgs-patriots`.
Tokens and Credits remains external in `tokens-and-credits`.
Presenter shows it under **Optional extras**, separate from the six main sessions.
See [demo-ready orchestration](deployment/demo-ready.md#optional-tokens-and-credits-extra) for discovery and local process ownership.
