# Demo 4 hosted cross-government control runbook

> **Preview:** This optional reserve uses preview Foundry Hosted Agent, Memory, and .NET packages.
> Do not use this configuration for production.

## Scope

Demo 4 investigates a grants-payment control failure across public bodies with five fixture scenarios.
Four scenarios are selectable. One hidden scenario exercises fail-closed validation.
It uses the Responses v2 hosted-agent protocol.
It does not use Planetary Computer or live external evidence.

The port uses `DEFRA-AI-Demos` commit `de5d2db25f8369c9ca804e7ca476ee93dee24dc8`.
The port changes the domain, tools, fixtures, and skills.

## Approved scenarios

The fixture stores each expected Memory decision, read, write, and outcome.

| Scenario | Case | `expectedMemoryAction` | `expectedMemoryRead` | `expectedMemoryWrite` | `expectedOutcome` |
|---|---|---|---:|---:|---|
| `CASE-BASELINE-GRANTS` | `CG-8101` | `skip` | `false` | `true` | `completed` |
| `CASE-RECURRENCE-HOUSING` | `CG-8202` | `read` | `true` | `true` | `completed` |
| `CASE-NEAR-MATCH-TRANSPORT` | `CG-8303` | `read` | `true` | `true` | `completed` |
| `CASE-UNRELATED-CONTROL` | `CG-8404` | `skip` | `false` | `true` | `completed` |
| `CASE-UNVERIFIABLE-CLAIM` | `CG-8505` | `skip` | `false` | `false` | `invalid` |

The baseline records the first missing supplier-assurance control failure and writes it once.
The recurrence case reads Memory and reaches the same reason code at a different public body.
The near-match case reads Memory, then current evidence rules the earlier pattern out with a different reason code.
The unrelated case skips Memory because continuity cannot help, and still writes once.
The hidden case must fail closed with no Memory write.

## Trust boundaries

- `assess_case_pattern` returns canonical fixture evidence.
- `search_case_context` returns bounded framing only.
- `search_investigation_memory` returns untrusted references only.
- `case-pattern-guidance` and `case-context-guidance` provide guidance only.
- The hosted agent has no Memory write function.
- The identity-protected application API invokes the hosted agent with its managed identity.
- The application derives its ledger from Responses `function_call` and `function_call_output` items.
- The application ignores model-authored execution claims.
- Only one verified `assess_case_pattern` result supplies the assessment and Memory write input.
- Failed or invalid agent results do not call the Memory coordinator.
- A request-scoped limiter rejects a second Memory read before Foundry receives it.
- A completed assessment can skip Memory when continuity does not improve the answer.
- A refused or unsupported request cannot read or write Memory.
- The agent never copies a prior outcome, confidence, evidence, or reason code into the current assessment.

The two tool outputs use separate versioned fixture files.
The context output cannot contain canonical evidence identifiers, measurements, determinations, or summaries.
Only `assess_case_pattern` can supply canonical evidence for the verified ledger.

The application uses create, reconcile, read-back verification, and delete compensation.
It does not report an indeterminate write as successful.
If cancellation can follow a committed create, a separate 10-second token reconciles and removes the exact record.
The application verifies the removal before it returns the original cancellation.

## Identities

Use one Microsoft Entra identity for each hosted agent.
Use the separate application managed identity for typed Memory writes.
Do not share the application identity with the hosted agent.
Protect `/api/case-pattern-assessments` with a dedicated Entra application registration.
Set `DEMO4_API_CLIENT_ID` before deployment. The application rejects unauthenticated calls.
The hosted-agent extension creates the hosted-agent identity during deployment.
The post-deployment hook assigns Foundry User and Monitoring Metrics Publisher to that separate identity.
The deployer receives Foundry Project Manager at project scope for the post-provision Toolbox, Skill, and Memory operations.
The hosted process registers only `search_investigation_memory` for Memory access.
It does not register a Memory write tool.
The same hook finds the deployed Responses endpoint and configures the application.
The azd root `predeploy` hook prepares the agent package, and the root `postup` hook configures the deployed agent and application.
Keep these steps outside the hosted-agent service block, which the agent extension rewrites.
After a standalone agent deployment, run the root `postup` hook for the same azd environment.

## Access control

Demo 4 uses two authentication layers.

App Service EasyAuth v2 is the browser gate. It applies one global action to every path.
An unauthenticated caller is redirected to the Microsoft Entra login page.
The gate covers `/`, `/Cases`, `/Memory`, every Razor handler, the static application content, and `/api/case-pattern-assessments`.
Only `/health` stays anonymous so the App Service health probe can reach it.
An unauthenticated API call therefore receives the login redirect and never reaches application data.

The EasyAuth contract in `infra/demo4/resources.bicep` requires all of the following:

| Setting | Required value |
| --- | --- |
| `openIdIssuer` | `${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0` |
| `allowedAudiences` | `api://<clientId>` and `<clientId>` |
| `requireAuthentication` | `true` |
| `unauthenticatedClientAction` | `RedirectToLoginPage` |
| `excludedPaths` | `/health` only |
| `httpSettings.requireHttps` | `true` |
| `login.tokenStore.enabled` | `true` |
| `login.nonce.validateNonce` | `true` |
| `allowedPrincipals.identities` | the configured user object IDs |

The deployment fails when the API client ID, the client secret, or the allowed principal is absent.
EasyAuth treats an empty allowed-principal list as no restriction, so an absent principal must fail the deployment.

Microsoft Identity Web is the second layer. It validates bearer tokens for `/api/case-pattern-assessments`.
The application validates the signature, the tenant-specific v2 issuer, the audience, and the token lifetime.
It rejects every validation failure with HTTP 401 and never reaches the hosted agent or Memory.
The Razor pages hold no in-process authorization policy, so the EasyAuth cookie session works in the browser.
Browser pages use `Referrer-Policy: same-origin`. This preserves same-origin headers for EasyAuth's cross-site request forgery (CSRF) checks without sending referrers to other origins.
Do not use `no-referrer` here. Chromium sends `Origin: null` on the form POST, so EasyAuth rejects even a signed-in request with HTTP 403.
Razor antiforgery tokens remain required. Do not disable CSRF checks or add `null` to the allowed origins.
See [App Service CSRF mitigation](https://learn.microsoft.com/azure/app-service/overview-authentication-authorization#mitigation-of-cross-site-request-forgery).
`/Memory` is read-only. It exposes only a GET handler and rejects every write method.

The demo-ready orchestrator owns the Entra application lifecycle in `scripts/DemoReady/Entra.ps1`:

- It requests access-token version 2.
- It enables ID-token issuance for the browser sign-in flow.
- It sets the exact reply URL `https://<app>.azurewebsites.net/.auth/login/aad/callback` and rejects any other reply URL.
- It bounds the client-secret lifetime to 28 days and rotates the secret before expiry.
- It masks the client secret in every command log and never writes it to a log file.

### Authentication limitation

The application uses `Microsoft.Identity.Web` with tenant-specific token validation.
Do not infer production compliance from a package reference or a successful deployment.
Confirm the deployed authentication behavior for every application instance and deployment slot.

## Responses lifecycle

The application creates one Responses conversation for each assessment request.
The configured Responses endpoint must contain only the exact `api-version=v1` query.
The client preserves this query on conversation, continuation, and polling requests.
It polls `queued` and `in_progress` responses at most 120 times.
It rejects all terminal failures with a generic application error.
Cancellation stops polling and does not write Memory.

The client can continue only `agent_framework` approval requests for `load_skill`.
It approves only `case-pattern-guidance` and `case-context-guidance`.
It rejects duplicate approvals and more than two approvals.
The hosted input guard accepts one approval input item with exactly `type`, `approval_request_id`, and `approve`.
The guard validates the exact type, a bounded safe identifier, and a Boolean decision before it bypasses user-text screening.
The approval envelope must contain only `model`, `conversation`, `agent_session_id`, `input`, and `stream`.
The guard requires safe identifiers and `stream: false`.
The polling limit evaluates the response from the final allowed GET before it reports a timeout.

## Memory reconciliation

The application follows `last_id` while `has_more` is true.
It scans at most 16 pages and 256 records during one reconciliation.
It rejects repeated items, repeated cursors, and pagination cycles.
The application can reconcile a valid record after the first 32 items.

## Application memory visibility

The application page shows the four scenario cards in the recommended run order.
Run the grants baseline first. It writes the record that the housing recurrence matches.
Run the transport near-match third and the records-retention case last.

After each run the page reports whether the agent searched Memory.
It reports the returned record count and the freshest record timestamp.
It shows every validated record with the memory ID, record ID, case reference, pattern,
date window, reason code, confidence, evidence IDs, prior recommended follow-up,
and the created and updated timestamps.

The application derives each match verdict. The model never supplies a verdict.

| Comparison with the current assessment | Verdict |
|---|---|
| Same reason code and a different case reference | Matched repeat control failure in another case |
| Same reason code and the same case reference | Earlier run of this case |
| Different reason code | Ruled out, different control |

The page states what changed because of Memory. The statement names each matched prior case
reference. It states that ruled-out records did not alter the current evidence. It states that the
search returned no prior reference when the notebook held no match. It states that the agent did not
consult the notebook when the agent skipped Memory.

The application appends the same statement to `RecommendedFollowUp` before the write. The new record
therefore preserves the decision. The canonical assessment stays byte-equivalent to the
`assess_case_pattern` output.

The application validates every Memory search result as untrusted input. It validates the reported
count, the freshest timestamp, every identifier, the retention window, the record envelope, duplicate
records, and every bounded value. It never renders unvalidated Memory content.

The page shows the exact canonical record after each verified write.

The `/Cases` page lists the canonical current-case fixture and bounded context used by the Hosted Agent tools.
It shows each prompt, case window, reason code, pattern, confidence, evidence item, and context observation.
These fixtures supply current-case facts through `assess_case_pattern` and `search_case_context`.
They are separate from Memory, which contains retained results from earlier investigations.

The `/Memory` page lists every valid record in the shared seven-day notebook. The page uses the
read-only listing method of the application memory client. That method reuses the bounded pagination
and `NotebookRecordCodec.DeserializeAndValidate`. Invalid or foreign items never render. The page
reports a rejected item count or a safe error with a trace ID. The page adds no delete, reset, or
write control.

## Local validation

Use the Microsoft package feed proxy from `NuGet.config`.

```powershell
dotnet restore .\PublicSectorAgentDemos.slnx
dotnet build .\PublicSectorAgentDemos.slnx --no-restore
dotnet test .\PublicSectorAgentDemos.slnx --no-build
```

`.\scripts\Test-DemoReady.ps1` runs the same commands with the automation harness, the Bicep
compilation, and the bundled project builds.

## Standalone Azure definitions

The files below are definitions only:

- `deploy/demo4/azure.yaml`
- `infra/demo4/main.bicep`
- `infra/demo4/resources.bicep`
- `infra/demo4/main.bicepparam`
- `infra/demo4/hooks/configure-foundry.ps1`
- `infra/demo4/hooks/configure-hosted-agent.ps1`
- `deploy/demo4/agent-package/Demo4.HostedAgent.RemoteBuild.csproj`

Use a separate Azure Developer CLI environment for a standalone deployment.
Run `azd provision --preview` before `azd provision`.
Deploy the hosted agent before the application.
The provision hook creates or validates both Skills, the Toolbox, and the seven-day Memory store.
The deployment uses Microsoft Entra identities without hardcoded keys or connection strings.
The Foundry project uses a `ProjectManagedIdentity` Application Insights connection.
The application receives its connection through a Bicep app setting.
The hosted runtime exports through the connected project.
Demo readiness requires fresh telemetry from both the hosted agent and application.
Microsoft Entra trace ingestion for Foundry agents is in public preview.
Before a later deployment, confirm model availability, quota, Toolbox content, identity access, and authentication behavior.

The application API requires the bare application client ID as its token audience.
The hosted endpoint must remain in the Bicep application settings after later provisioning.
The project identity needs `Cognitive Services OpenAI User` for Memory model inference.
