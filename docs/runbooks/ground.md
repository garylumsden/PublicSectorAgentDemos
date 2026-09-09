# Demo 1: Foundation and Ground

## Scope

This demo compares two pre-provisioned prompt agents for Managing Public Money.

- `managing-public-money-foundation` uses only the canonical base instructions.
- `managing-public-money-ground` adds pinned Foundry IQ retrieval and required citations.
- Both agents use the shared `gpt-5-mini` deployment with low model reasoning.
- Both receive the same question, base instructions, and explanation length limit.
- Foundation has no tools. Its answer is not required to be wrong or to match the expected route.
- All schedules, scenarios, people, dates, organisations, and amounts are demonstration data.

The source design was read from `DEFRA-AI-Demos` at commit `de5d2db25f8369c9ca804e7ca476ee93dee24dc8`. Demo 1 reuses its pinned-source, checksum, scope-gate, and fail-closed patterns. It does not copy the waste-classification domain.

## Exact demonstration flow

1. Run the default offline validation.

   ```powershell
   dotnet restore PublicSectorAgentDemos.slnx --source https://packagefeedproxy.microsoft.io/nuget/v3/index.json
   dotnet build PublicSectorAgentDemos.slnx --no-restore
   dotnet test PublicSectorAgentDemos.slnx --no-build
   .\scripts\ground\Sync-ManagingPublicMoney.ps1 -Mode Validate
   ```

2. Show `config/ground/prompts/managing-public-money.base.md`.
3. Show both entries in `config/ground/agent-definitions.v1.json`.
4. Explain that the Foundation entry has no knowledge, retrieval, tool, or citation property.
5. Explain that the Ground entry adds only the grounding extension and pinned Foundry IQ configuration.
6. Select MPM-005 from `data/ground/v1/fixture-set.json`. Use the exact prompt on both Presenter cards.
7. Run the Foundation agent. Record its provisional answer.
8. Run the Ground agent with the same prompt.
9. For a supported scenario, verify a `knowledge_base_retrieve` call occurred.
10. Inspect `Evidence` blockquotes against the retrieved snippets. Reject changed words, invented punctuation, and missing conditions.
11. Read `Application`. Compare Treasury approval, Parliamentary notification, and the checklist threshold separately.
12. For the unsupported scenario, verify the agent returns only the exact refusal. Verify that it returns no assessment and no citation.
13. Compare the answers. Do not present either answer as spending approval.
14. Repeat with the four control scenarios by using the cloud rehearsal.

The five demonstration scenario routes are:

| Scenario | Required route |
|---|---|
| Routine renewal within the stated delegation | `delegated` |
| Novel and repercussive grant | `treasury-consent` |
| Materially ambiguous commitment | `accounting-officer-escalation` |
| Staff rota request outside the domain | `refuse-unsupported` |
| Non-routine letter of comfort below the general notification threshold | `treasury-consent` |

### Primary comparison and source grounds

Use the [exact MPM-005 prompt, source extracts, and saved comparison](ground-comparison.md).
The question distinguishes three controls in Annex 5.4. The answers require more than a general warning about novel spending.

- A5.4.23 makes Treasury approval essential for letters of comfort, despite non-binding wording.
- A5.4.27 overrides the general under-GBP-300,000 notification exemption for the specified non-routine instrument.
- A5.4.19 sets the mandatory checklist submission threshold at GBP 3 million of maximum exposure.

GBP 240,000 does not meet the checklist threshold. This does not remove the separate approval and notification requirements.
Treasury can still request supporting papers. Neither agent approves the proposal.

Both agents use `Assessment`, `Application`, and `NextAction`. Ground adds `Evidence` after `Assessment`.
The explanation target is 220 words, excluding quotations. This is a prompt target, not an enforced length guarantee.
Ground quotes 4-20 consecutive source words per block. It must preserve the rule's conditions.
The quote validator accepts PDF whitespace changes, but not paraphrases or literal JSON escape sequences.

High reasoning is intentional and applies to both agents. Lower reasoning produced altered quotations during development.
High reasoning increases latency. Use the saved comparison if a live answer exceeds the presentation time available.

## Prepare the pinned GOV.UK source

The repository contains the April 2026 GOV.UK PDF and its SHA-256 pin.

```powershell
.\scripts\ground\Sync-ManagingPublicMoney.ps1 -Mode Download
```

The script only accepts HTTPS on `assets.publishing.service.gov.uk`. It rejects an unexpected redirect, a non-PDF file, and SHA-256 drift. If GOV.UK changes the file, review the new publication before a separate commit updates the URL and checksum.

## Optional isolated Azure preparation

Use the standalone Azure Developer CLI project in `deploy\demo1`. It creates one isolated resource
group. It provisions only Demo 1 AI Services, model deployments, a Foundry project, Storage,
Search, and role assignments. It provisions no other demo.

```powershell
azd auth login
azd -C .\deploy\demo1 env new demo1-ground
azd -C .\deploy\demo1 env set AZURE_LOCATION <azure-region>
azd -C .\deploy\demo1 provision
$env:RUN_DEMO1_CLOUD_INTEGRATION = 'true'
.\scripts\ground\Publish-Demo1Agents.ps1
```

Select a region that has quota for `gpt-5-mini` and `text-embedding-3-small`.

The Azure Developer CLI writes the required Bicep outputs to the environment. The publish script requests separate bearer tokens for Foundry and Search. It validates each HTTPS service host before it sends the applicable token.
The standalone project connects workspace-based Application Insights with `ProjectManagedIdentity`.
The project identity has Monitoring Metrics Publisher on that component.
The demo-ready command starts the demonstration. It runs no trace-ingestion check. Confirm
prompt-agent trace ingestion in the portal, or run `.\scripts\Test-DemoReady.ps1 -RunCloudTests`.
The Demo 1 **Traces** page showing **Connect** is a failed deployment, not an offline fallback.
Microsoft Entra trace ingestion is in public preview.

The Bicep outputs include the exact Blob Storage host and knowledge-source name. The publish script confirms that Search returns this knowledge-source identifier.

The Search service identity receives `Storage Blob Data Reader` only on the corpus container. It does not receive access to other containers.

The publish script polls the knowledge-source status after each create or update. The default timeout is 600 seconds with a 5-second interval. It requires a new completed synchronization with at least one processed item. It stops before knowledge-base or agent creation if the status contains failed items, errors, an error state, or a timeout.

After agent creation, the publish script sends a deterministic corpus probe to the Ground agent. The probe must call `knowledge_base_retrieve`. The script reads real `file_citation` annotations from `output_text`, then stores their service `file_id` values in `.azure\demo1-citation-identity.json`. It prints the `DEMO1_CITATION_IDENTITY_PATH` assignment for the live test. Provisioning fails if the probe does not return the pinned filename and at least one file ID.

The publish script writes `definition.reasoning.effort` from the catalog for both prompt agents. The catalog `foundryIq.required` value is a policy declaration. It does not enforce runtime behavior by itself.

The Prompt Agent API supports a persisted `tool_choice: required`, but that choice is unconditional. It would also force retrieval for the unsupported-domain refusal. The live client therefore sends invocation-time `tool_choice: required` only after the scenario requires grounding. The provisioning corpus probe also sends this choice. A direct portal invocation cannot apply this scenario-aware override. It relies on the prompt and is not equivalent to the live rehearsal.

The root `azure.yaml` was removed. This repository has no unified root composition. Use
`deploy/demo1/azure.yaml` for an isolated Demo 1 deployment. `scripts\Invoke-DemoReady.ps1` uses
the same context and writes the citation identity file under the ignored `.demo-ready` directory.

The publish script does not run from a post-provision hook. This repository change does not deploy Azure resources.

## Optional cloud tests and repeated rehearsal

Default tests skip the separate CloudIntegration project. To enable it, set these values:

```powershell
$env:RUN_DEMO1_CLOUD_INTEGRATION = 'true'
$env:AZURE_AI_FOUNDRY_ENDPOINT = 'https://<resource>.services.ai.azure.com/api/projects/<project>'
$env:DEMO1_CITATION_IDENTITY_PATH = '<publish-script-output-path>'
dotnet test .\tests\PublicSectorAgentDemos.Demo1.CloudIntegrationTests\PublicSectorAgentDemos.Demo1.CloudIntegrationTests.csproj --filter 'Category=CloudIntegration'
.\scripts\ground\Invoke-Rehearsal.ps1 -Runs 5 -MinimumSuccessRate 100
.\scripts\ground\Invoke-Rehearsal.ps1 -Runs 3 -ScenarioId MPM-005 -ResultsPath .\.demo-ready\ground-rehearsal
```

The rehearsal prints JSON with the run count, success count, failure count, duration, and success rate.
Each scenario uses one fresh request per agent. There are no hidden response retries.
The test reads the live definitions and checks equal model, reasoning, and shared base instructions.
It confirms that Foundation has no tools. Only Ground must match the expected assessment route.
`-ResultsPath` saves both answers before assertions, including rejected Ground answers.
Keep result files in ignored local storage. Review them against the MPM-005 `reviewRubric`.
The automatic score covers response structure, source identity, and quotation integrity. It does not prove factual accuracy.
`.\scripts\Test-DemoReady.ps1 -RunCloudTests` runs the same rehearsal with the azd environment
values already applied.

The live test reads only `url_citation` and `file_citation` annotations attached to an `output_text` part. For `file_citation`, it requires an exact exported `file_id` and the pinned filename. Its `index` is only a zero-based file-list index. A visible `[n]` marker maps to file-list index `n - 1`. The validator never uses this index as a character offset.

For `url_citation`, the validator uses only the supported `url`, `start_index`, and `end_index` fields. The offsets must cover the visible marker. The full normalized URI must equal a manifest URI. The comparison includes the scheme, host, port, path, query, and fragment.

The validator defines a material output unit as each non-empty line in an `output_text` block. It excludes a label-only line, the exact refusal, and the exact grounding-unavailable message. Each material line must contain an unchanged service citation marker.

The rehearsal client applies `GroundedResponseGate` to every Ground response. It requires two to four verbatim blockquotes and an `Application` section.
Each quote must match the decoded snippet for its service marker and approved Blob URI.
It must end at a source sentence or list boundary. A shortened conditional rule must not become an unconditional claim.
Retrieval, source, annotation, coverage, or quotation failures return only the local fail-closed message.
The client currently recognises the observed Foundry IQ marker-plus-JSON-snippet format. A different retrieval format fails closed.
These checks do not establish that the interpretation or every cross-reference is correct.

**Portal boundary:** the Foundry playground does not run the repository's C# gate.
Inspect the retrieval, quotations, and application manually before presenting a portal answer.
Do not describe a portal response as automatically validated.

The fail-closed service response is exact only when the response contains one assistant message with one unmodified `output_text` part. The text must have no extra whitespace. The part must have no annotation. Service-generated `reasoning` output items can precede the message. A tool call, another message, another content part, or any other output item invalidates the response.

Agent publishing follows the [official Foundry Agents REST contract](https://github.com/Azure/azure-rest-api-specs/blob/main/specification/ai-foundry/data-plane/Foundry/src/agents/routes.tsp). It first queries `GET /agents/{agent_name}`. It uses `POST /agents` when the named agent is absent. It otherwise uses the idempotent `POST /agents/{agent_name}` update operation. Foundry creates a new immutable version only when the definition changes. The script then reads `GET /agents/{agent_name}/versions/{agent_version}` and verifies the returned definition. A rerun preserves an agent created before a later agent failed, then resumes the missing agent.

## Fallbacks

| Failure | Required action |
|---|---|
| Local PDF is missing | Run the download command. Do not use another source. |
| SHA-256 drift occurs | Stop. Review the GOV.UK update before changing the pin. |
| Foundry IQ is unavailable | Return the exact grounding-unavailable message. Do not answer from model memory. |
| Retrieval has no relevant passage | Return only the exact grounding-unavailable message. Do not add risks or actions. |
| A citation is missing or uses another source | Reject the Ground answer. Do not present it. |
| Knowledge-source ingestion fails or times out | Stop before knowledge-base and agent creation. Review the reported failed item or status. |
| A request is outside the domain | Return only the exact refusal. Do not retrieve evidence or attach citations. |
| Azure credentials or variables are missing | Use the offline flow and saved configuration. Do not claim a live result. |
| A cloud rehearsal fails | Keep its failure in the reported success rate. Investigate before the demo. |
| Foundation gives the correct detailed answer | Show that result honestly. Compare its evidence basis; do not retry until it makes a mistake. |
| Ground alters a quote or omits a condition | Reject the result. Use the labelled saved comparison and original source extracts. |
| A live response takes too long | Use the saved comparison. State that the result is saved rather than live. |

## Update existing prompt agents only

Reuse the current environment and preflight when no Bicep or Azure resource change is needed.
Load the existing Demo 1 environment values into the current process without printing their values.
Set the existing citation identity path. Then run:

```powershell
$env:RUN_DEMO1_CLOUD_INTEGRATION = 'true'
$env:DEMO1_CITATION_IDENTITY_PATH = '.\.demo-ready\demo1-citation-identity.json'
.\scripts\ground\Publish-Demo1Agents.ps1 -AgentsOnly
```

`-AgentsOnly` validates the pinned local PDF and existing citation identity.
It updates only the two named prompt agents, verifies their definitions, and runs the corpus probe.
It does not upload the corpus, synchronise ingestion, update Search definitions, or provision infrastructure.
Do not run `Invoke-DemoReady.ps1` to apply this prompt-only change.

The Foundry skill dependency check was unavailable during this update: its required `azure.ai.agents` version differed from the installed version.
The existing repository REST publisher and .NET rehearsal were used instead. Shared tooling was not upgraded.

The exact fail-closed message is:

`Grounding unavailable. No evidence-based assessment can be provided.`

The exact unsupported-domain refusal is:

`Refusal: This request is outside the Managing Public Money assessment domain.`
