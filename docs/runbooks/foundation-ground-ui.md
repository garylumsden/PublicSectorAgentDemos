# Foundation / Ground local UI

This application compares two existing registered prompt agents in one Foundry project:

| Panel | Registered agent |
| --- | --- |
| Foundation | `managing-public-money-foundation` |
| Ground | `managing-public-money-ground` |

The application uses the constants in the existing Demo1 library.
It does not create, update, publish, or deploy agents.
It does not override the model, instructions, reasoning effort, tools, or tool choice.
Both requests contain only the same user input.
Each comparison starts new responses without conversation history.
Both agents use `gpt-5-mini` with `low` model reasoning.
Ground also uses required Foundry IQ retrieval with `minimal` retrieval reasoning.
The UI shows these settings. Elapsed times compare matched model reasoning settings, but service and retrieval variability still apply.

## Start locally

Use the repository's .NET 10 SDK and an existing Azure CLI sign-in with access to the Foundry project.
Set the existing project endpoint, not an application endpoint or model deployment endpoint.
No API key or `azd` workflow is required.

From the repository root:

```powershell
$env:AZURE_AI_FOUNDRY_ENDPOINT = 'https://<resource>.services.ai.azure.com/api/projects/<project>'
$env:AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
dotnet run --project src\PublicSectorAgentDemos.Demo1.Web\PublicSectorAgentDemos.Demo1.Web.csproj -c Release --no-launch-profile
```

Open `http://localhost:5090`.
Stop this process with `Ctrl+C`.
The integration scripts can start this project and poll `GET http://localhost:5090/health`.
The health route reports local process readiness, not Azure access or agent readiness.
Missing or invalid endpoint configuration prevents startup.
An agent invocation reports Azure sign-in, permission, transport, and service failures separately for each panel.

The default prompt is the exact `MPM-005` fixture in `data\ground\v1\fixture-set.json`.
The project embeds that existing fixture file during the build.
Rebuild the project after changing the fixture.
The **Restore MPM-005** button restores the embedded prompt without modifying its text.

## Demo Ready integration

The complete setup command, `scripts\Invoke-DemoReady.ps1`, builds and starts the comparison application.
It passes the existing Demo 1 project endpoint, Azure CLI credential selection, and Application Insights configuration.
It records the process as `demo1-comparison` and checks `/health`.
Both Foundation and Ground Presenter cards open this application.

The complete setup command also provisions the four owned demos.
For a comparison-only start against an existing deployment, use the standalone command above instead.
No agent publication or Azure resource changes are required.

To stop a comparison process recorded by Demo Ready, run:

```powershell
.\scripts\Stop-DemoReady.ps1 -OwnedOnly -Name 'demo1-comparison'
```

This command preserves Presenter, Council, Patriots, and Tokens.
For a standalone process, use `Ctrl+C` in its terminal.

## UI behavior

Select **Compare both agents** to start both requests concurrently.
Each panel shows elapsed time while its request runs.
Each result or error appears immediately after that request finishes, without waiting for the other agent.
The service returns complete responses; the UI does not claim token-level streaming or report invented retrieval progress.
Select **Cancel pending requests** to stop local waiting and request cancellation upstream.
Completed answers remain visible.
Foundry may continue work that it already accepted.

The formatted view supports encoded headings, paragraphs, and Markdown blockquotes.
Other Markdown syntax and citation markers remain literal text.
The application never interprets model HTML, images, or links as active content.
Service citation annotations appear as text, not clickable links.

Each panel exposes copyable original answer text and the unmodified service response JSON.
For multiple answer blocks, the text view joins the unchanged blocks with a newline.
The original JSON retains the original block boundaries and all response fields.
The application does not silently correct answers or replace them with a quality-gate message.
Incomplete responses, refusals, missing citations, and empty answers remain available in the original JSON.

Citation display does not establish source identity, quotation accuracy, evidence coverage, or factual accuracy.

After two eligible responses finish, select **Assess quality** to assess the exact displayed answer pair.
The assessment does not rerun either answer agent.
It uses one separate direct Responses API call to `DEMO1_ASSESSMENT_MODEL`, which defaults to `gpt-5-mini`.
The judge uses low reasoning effort and no tools.
It scores correctness, relevance and completeness, evidence support, uncertainty handling, and next-action usefulness.
Each numeric score includes an exact quotation from the assessed answer.
A criterion shows **Not assessed** when the supplied references cannot support a score.
The server calculates each `Ground - Foundation` difference.
The UI does not calculate an overall score or percentage improvement.

The application matches reviewed fixtures by the exact submitted prompt.
For `MPM-005`, it supplies the repository review guidance for Treasury approval, Parliamentary notification, and the GBP 3 million checklist threshold.
For a custom prompt, no reviewed expected outcome exists.
The judge must not use its training knowledge as proof of current policy.
The citation identity file enables the existing source and quotation checks when it is available.
Missing or invalid citation identity produces an explicit evidence limit.

The assessment is advisory and can report positive, neutral, negative, or insufficient evidence.
It does not prove that grounding caused a difference.
Foundation and Ground both use low model reasoning effort.
Ground uses minimal Foundry IQ retrieval reasoning effort.
The judge uses the same model deployment family by default, which can introduce shared-model bias.
This custom application rubric is not a Microsoft Foundry built-in evaluator score or an approved financial decision.
Assessment failure, timeout, or cancellation does not alter the original answers.

## Integration and API contract

| Setting or route | Contract |
| --- | --- |
| Listener | HTTP, port `5090`, IPv4 and IPv6 loopback only |
| `AZURE_AI_FOUNDRY_ENDPOINT` | Required HTTPS project endpoint under `*.services.ai.azure.com/api/projects/<project>` |
| `GET /` | Static browser UI; no npm or frontend build step |
| `GET /health` | `200`, JSON `{"status":"ok","scope":"local-only"}` |
| `GET /api/session` | Exact default prompt, scenario ID, limits, antiforgery request token, and a strict HttpOnly antiforgery cookie |
| `POST /api/compare` | JSON `{"prompt":"..."}`; requires the cookie, `X-Demo1-CSRF`, and an exact same-origin `Origin` header |
| `POST /api/assess-quality` | JSON `{"comparisonId":"..."}`; uses the same browser-session protections and accepts no answer text or judge instructions |
| Comparison response | `application/x-ndjson`; one JSON line per agent, flushed in completion order |
| Invalid request | Generic JSON error; `400`, `403`, `413`, or `415` |
| Full comparison capacity | `429` with `Retry-After: 5`; no agent invocation |

NDJSON means newline-delimited JSON.
Each comparison line contains `agent`, `state`, `elapsedMs`, `answer`, `error`, and the same opaque `comparisonId`.
The `agent` value is `foundation` or `ground`.
The `state` value is `completed`, `failed`, `timed-out`, or `cancelled`.
`completed` means the service response arrived; inspect `answer.responseStatus` for the service's own status.
An answer contains `originalText`, `originalResponse`, `renderedHtml`, `responseStatus`, and `citations`.
Errors are generic and do not include service response bodies, credentials, or exception details.
After browser cancellation, the connection closes; no cancellation line is guaranteed.

| Bound | Value |
| --- | --- |
| Prompt | 1-8,000 UTF-16 code units; whitespace-only input is invalid |
| Request body | 32,768 UTF-8 bytes, including JSON syntax and escaping |
| Concurrent comparisons | 2 per process, with at most 4 active agent requests |
| Agent timeout | 180 seconds per agent |
| Service response | 1,048,576 bytes per agent, including retrieval output |
| Kestrel connections | 32 per process |
| Stored completed answer pairs | 8 per process, process memory only |
| Answer-pair lifetime | 30 minutes |
| Concurrent assessments | 1 per process |
| Assessment timeout | 90 seconds |
| Serialized assessment input | 131,072 bytes; answers are rejected, not truncated |
| Assessment response | 16,384 bytes |

A completed answer pair is bound to the browser's antiforgery session.
Successful assessments are cached with that pair.
An explicit retry is available after failure or cancellation.
The server does not automatically retry or repair a judge response.

Oversized service responses produce a generic per-agent failure; the UI does not display a truncated answer as complete.
The server does not queue comparisons or retry service requests automatically.
Cancel requests by closing or aborting their HTTP connection.
The server releases comparison capacity after both agent tasks terminate.

## Identity, security, and telemetry

The server calls the shared `AddDemoAzureIdentity` helper from `PublicSectorAgentDemos.Identity`.
The helper registers one `DefaultAzureCredential` from `Azure.Identity`.
Set `AZURE_TOKEN_CREDENTIALS=AzureCliCredential` to select only the existing Azure CLI sign-in; the startup helper already sets this value.
Without this setting, `DefaultAzureCredential` can select other configured credential types.
The server requests the scope `https://ai.azure.com/.default`.
The Azure access token stays on the server.
The browser receives only an antiforgery request token, not an Azure access token.
The server invokes the answer agents at:

```text
{project-endpoint}/agents/{agent-name}/endpoint/protocols/openai/responses?api-version=v1
```

The separate quality judge uses:

```text
{project-endpoint}/openai/v1/responses
```

The implementation follows `tests\PublicSectorAgentDemos.Demo1.CloudIntegrationTests\LiveAgentParityTests.cs`.
It deliberately omits that test's `tool_choice` override and quality-gate enforcement.
Agent configuration remains unchanged.

The listener rejects non-loopback peers, missing peers, unexpected Host headers, and proxy-forwarding headers.
Accepted hosts are `localhost:5090`, `127.0.0.1:5090`, and `[::1]:5090`.
DNS rebinding means an attacker changes a website's address to reach a local service; the explicit Host boundary prevents this access.
POST requests also require the antiforgery cookie, request token, and matching Origin.
The application rejects cross-site fetch metadata and does not enable CORS.
Custom Kestrel endpoints are unsupported and prevent startup.
Configured URL arguments do not override the explicit loopback listener.

HTTP is a deliberate loopback-only shortcut.
Do not expose this application through a proxy, tunnel, container port publication, or remote network binding.
No inbound bearer-token resource exists.
Production hosting requires a separate authenticated HTTPS design with verified deployment compliance.
Local users and processes with access to the same machine are inside this demo's trust boundary.

The application calls the shared `AddDemoObservability` registration.
It uses the existing optional exporter environment settings without adding a telemetry endpoint.
Custom activities include only the agent stage and outcome.
Application logs omit prompts, answers, Azure tokens, and upstream exception details.
The browser does not persist answers in local storage.
HTTP responses use `Cache-Control: no-store`, a restrictive Content Security Policy, and anti-framing headers.

## Parent-owned additions and focused tests

The parent must include these projects in the solution if they are not already present:

```text
src\PublicSectorAgentDemos.Demo1.Web\PublicSectorAgentDemos.Demo1.Web.csproj
tests\PublicSectorAgentDemos.Demo1.Web.Tests\PublicSectorAgentDemos.Demo1.Web.Tests.csproj
```

No central package additions are required.
The projects use existing central versions for `Azure.Identity`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, and `coverlet.collector`.
The integration agent owns the root startup scripts, stop scripts, and Presenter integration.

```powershell
dotnet test tests\PublicSectorAgentDemos.Demo1.Web.Tests\PublicSectorAgentDemos.Demo1.Web.Tests.csproj -c Release --verbosity quiet
```

The HTTP tests use the real application routes through `WebApplicationFactory`.
A controlled agent barrier proves concurrent start, identical input, and independent completion in either order.
Other tests cover cancellation, capacity, input limits, origin protection, host boundaries, safe rendering, and the REST request shape.
The tests do not contact Azure.
The parent performs the real live browser comparison after integration.
