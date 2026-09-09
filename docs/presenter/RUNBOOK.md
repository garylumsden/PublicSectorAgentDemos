# Presenter mode

Presenter mode is a standalone, offline-first guide for the demonstration session.
It runs at `http://localhost:5088`.

## Session order

The catalog contains exactly six sessions in this required order:

| Order | Session | Owner | Launch target |
|---|---|---|---|
| 1 | Foundation | This repository | Comparison UI at `http://localhost:5090/`. |
| 2 | Ground | This repository | The same comparison UI, with a separate session card. |
| 3 | Act | Bundled `src\PublicSectorAgentDemos.Demo2.Act` | The deployed Act web application. |
| 4 | Cross-Government Coordinate | Bundled `src\PublicSectorAgentDemos.Demo3.Coordinate` | `http://localhost:5080/dossiers` |
| 5 | Patriots Coordinate (optional) | External `azure-ai-mgs-patriots` | Not configured unless explicitly included. |
| 6 | Hosted (optional) | This repository | The deployed Demo 4 application. |

The separate **Optional extras** section contains Tokens and Credits.
It does not change the six-session order, the five owned sessions, or the four deployment contexts.

Demonstration upload files are grouped by demo under the repository-root `demo-dossiers` folder.
Cross-Government Coordinate uses `demo-dossiers\cross-government`, starting with `dossier-shared-ai-service.md`.
That folder also contains four alternative Cross-Government scenarios.
Patriots Coordinate uses `demo-dossiers\patriots`, starting with `01-puppet-president.md`.
That folder contains all 16 Patriots dossiers.
The two sessions never share a dossier.

## Foundation and Ground comparison

Both cards use the exact MPM-005 question from `data\ground\v1\fixture-set.json`.
The case distinguishes Treasury approval, Parliamentary notification, and the Contingent Liability checklist threshold.
Use a fresh conversation for each agent. Keep the Foundation result visible.
Do not claim that Foundation must be wrong or that grounding guarantees correctness.

Ground must show verbatim source blockquotes, citations, and a fact-to-rule explanation.
Check the three decisions separately. Do not compare answer length as the main result.
The Foundry portal does not execute the repository's response gate.
If a quote is changed, incomplete, or unsupported, reject that response.

Open [the saved comparison and source extracts](../runbooks/ground-comparison.md) when the live result fails or takes too long.
State that it is a saved result. Both agents use the same `gpt-5-mini` model and high reasoning.
High reasoning increases response time; it does not guarantee correct interpretation.

## Run

```powershell
dotnet run --project .\src\PublicSectorAgentDemos.Presenter
```

The page reads `config\presenter\sessions.v1.json` by default. This committed catalog holds the
session order, prompts, and fallbacks. It holds no workstation-specific absolute path.

`scripts\Invoke-DemoReady.ps1` generates a session catalog for the current session at
`.demo-ready\sessions.generated.json`. It replaces every launch URL and every warm-up endpoint
with a validated value:

- Foundation and Ground launch the local comparison UI. Auxiliary links open their validated Foundry agent URLs.
  Their existing model warm-up still uses the Demo 1 Responses endpoint.
- Act uses `DEMO2_WEB_URL` from the bundled Demo 2 environment.
- Council uses its fixed local ports and an absolute path under `demo-dossiers\cross-government`.
- Explicitly included Patriots uses its external local ports and a path under `demo-dossiers\patriots`.
- Hosted uses the deployed Demo 4 application host.

The startup script passes the generated catalog through `PRESENTER_SESSION_CATALOG_PATH`. Do not
change the committed catalog for environment-specific endpoints.

The Presenter has no project reference to a demo. It does not use a demo package graph. The page
renders without Azure credentials and without network access. During startup, the Presenter
exports request telemetry to the Demo 1 Application Insights component. Without deployment
metadata, the Presenter still renders in offline-first mode and does not require Azure Monitor.
Demo 1 grants the deploying user Monitoring Metrics Publisher on its Application Insights resource.
This permits the local Presenter's Azure CLI identity to write telemetry while local key authentication remains disabled.

## Start the applications manually

The startup script builds and starts the Demo 1 comparison UI, bundled council, and Presenter.
The comparison UI receives the existing Demo 1 `AZURE_AI_FOUNDRY_ENDPOINT` and standard `APPLICATIONINSIGHTS_CONNECTION_STRING`.
It uses the shared observability implementation and Azure CLI credential selection.
Startup requires a successful `GET http://localhost:5090/health`; this does not prove a live scenario result.
Use this command for a manual council check after provisioning:

```powershell
$env:ASPNETCORE_URLS = 'https://localhost:7080;http://localhost:5080'
dotnet run --project .\src\PublicSectorAgentDemos.Demo3.Coordinate\src\GovernanceCouncil.Web --no-launch-profile
```

Act and Demo 4 run in Azure.
Start Patriots independently through its own repository if you need the optional comparison.
Use `-IncludePatriots` or `-PatriotsRepoPath` with root startup to enable its link.
The default catalog marks Patriots **Not configured** and disables its launch and warm-up.
The five owned sessions represent four deployments; Foundation and Ground share Demo 1.

## Optional Tokens and Credits

Startup discovers the external `tokens-and-credits` repository using this precedence:

1. `-TokensAndCreditsRepoPath`
2. `PSAD_TOKENS_AND_CREDITS_REPO_PATH`
3. `repositories.tokensAndCredits.path` in the secret-free setup file
4. A sibling folder named `tokens-and-credits`

Use `scripts\DemoReady\repositories.example.json` as an example. Replace its placeholder path before use.
No azd environment is required. Startup does not provision, deploy, or write external configuration.
Startup builds only `src\TokensAndCredits.Web\TokensAndCredits.Web.csproj`.
It binds HTTP to `localhost:5041` without a launch profile.
Existing configuration and environment values remain unchanged.
The app uses standard `AzureFoundry__*` configuration and `DefaultAzureCredential` for optional cloud features.
Startup does not import Demo 1 model settings into this app.
An endpoint without any chat or embedding deployment causes the external app to reject startup.

The app has no `/health` route.
Startup and warm-up request `GET /api/embeddings/manifest`.
HTTP 200 must contain the local embedding origin and positive dimension and vocabulary counts.
This route reads the bundled embedding asset. It does not request an Azure token or invoke a model.
Model discovery, chat, images, and live embeddings are outside this readiness check.
The readiness report never claims cloud model readiness for this extra.

An absent checkout appears as **Not configured**. Discovery, build, port, or health failures appear as **Startup failed**.
These outcomes do not prevent main readiness. A busy port is not evidence that the optional app is ready.
Startup never stops an unrecorded listener to free a port.
The stop script manages Tokens and Credits only when its process record explicitly identifies this checkout as the owner.
It retains the existing PID, creation-time, executable, command, and process-tree identity checks.
It also recognizes exact council process records from before relocation without widening the permitted paths.
Patriots remains link-only, even when an explicit stop name is supplied.

## Warm-up boundary

Select **Warm up demos** only after the page loads. Warm-up runs in the background and reports
one result for each session and a separate result for each extra.
Repeated selections use the same result for five minutes.

The warm-up service sends only these requests:

- a minimal Responses request to each configured Foundry model deployment;
- `GET /` to a configured application;
- `GET /health`, `GET /warm`, or `GET /` to Demo 4, as configured.
- `GET /api/embeddings/manifest` to configured Tokens and Credits, without a model request.

The Presenter registers the shared `AddDemoAzureIdentity` helper and resolves its singleton `TokenCredential` only when a configured Foundry request needs it.
The helper uses `DefaultAzureCredential`; startup selects Azure CLI authentication with `AZURE_TOKEN_CREDENTIALS=AzureCliCredential`.
Local-only warm-up does not construct or request an Azure credential.
The service sends the token only to a validated Azure Foundry host.
It does not dispatch, approve, persist council data, or write Memory.
It does not add a route to a demo.

`success` means the configured endpoint returned HTTP 2xx or 3xx. A cloud application behind Easy
Auth answers an unauthenticated warm-up request with a sign-in redirect, so a redirect counts as
ready. Tokens and Credits instead requires HTTP 200 and the local manifest response.
Redirects are not followed. `not-running` means a local application refused the connection. `failure` means
authentication, timeout, remote network, or HTTP response validation failed.
`not-configured` means the optional external link is disabled; the service made no request for it.

The startup script starts owned local applications and the discovered optional Tokens and Credits application.
It runs readiness probes, not model warm-up. Select **Warm up demos**
on the page for the six session results and the separate extra result. Run `.\scripts\Test-DemoReady.ps1` for the
repository tests and builds.

## Offline and fallback operation

If Azure or the network is unavailable, keep the Presenter page open. Copy the exact input from
each card. Use the visible saved-result fallback on that card. State when a shown item is a saved
result.
