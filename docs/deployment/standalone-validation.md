# Standalone deployment validation

> **Superseded historical record.** Date: 2026-09-02.
> This record covers an earlier set of four deployment contexts.
> The intermediate external-only ownership below was also superseded.
> Current bundles live under `src\PublicSectorAgentDemos.Demo2.Act` and `src\PublicSectorAgentDemos.Demo3.Coordinate`.
> Their provenance files identify the current upstream revisions.
> Use [demo-ready orchestration](demo-ready.md) for the current process.

## Status

| Demo | Intermediate owner (superseded) | Result on the record date |
|---|---|---|
| Demo 1 Foundation and Ground | This repository | Passed |
| Demo 2 Act | External `DEFRA-AI-Demos` | Passed, then moved out of this repository |
| Demo 3 Coordinate | External `cross-gov-assurance-board-demo` | Passed, then moved out of this repository |
| Demo 4 Hosted agents | This repository | Passed |

The record does not repeat the resource names, endpoints, or regions of that date. Read the live
values from the azd environment of each context.

## Demo 1 Foundation and Ground

- The Azure context matched the approved subscription.
- The model catalog contained the required model versions.
- The available quota covered the planned capacity of `gpt-5-mini` and `text-embedding-3-small`.
- `azd provision --preview --no-prompt` completed with no delete.
- `azd deploy --all --no-prompt` completed.
- The pinned April 2026 corpus checksum passed.
- The knowledge source reused a completed ingestion with no failed item.
- Foundation version 1 and Ground version 3 were active.
- The corpus probe called `knowledge_base_retrieve` and returned an exact approved Blob citation.
- The live parity test passed all four routes for both agents.
- The unsupported route returned the exact refusal without a retrieval call.
- The Search identity had `Storage Blob Data Reader` on only the corpus container.
- The Search identity had `Cognitive Services User` on only the Foundry account.

## Demo 4 Hosted agents

- The region model and App Service availability checks passed.
- The provisioning preview contained no delete.
- Provisioning created all resources after the account operations were serialized.
- The remote build retried two transient Microsoft package proxy TLS failures.
- The application health endpoint returned HTTP 200.
- Hosted Agent version 4 reported `active` with Responses v2.
- The Toolbox connected and discovered two reviewed Skills.
- The approval guard accepted only the verified platform agent reference.
- The hosted skip scenario completed without a Memory read.
- The relevant scenario performed exactly one Memory read.
- The completed skip scenario wrote and verified one Memory record.
- The invalid scenario failed before the recording Memory client received a write.
- The authenticated application API returned HTTP 200 for the completed skip scenario.
- Token logs confirmed the signature, lifetime, and audience checks.
- The application identity and the hosted-agent identity each had `Foundry User` at project scope.
- The project identity had `Cognitive Services OpenAI User` at account scope.

No secret, access token, principal identifier, or tenant identifier is recorded here.
No Azure resource was deleted during this validation.

## Intermediate external-only layout (superseded)

- The Demo 2 Act context, source, and Bicep templates left this repository.
- The Demo 3 Coordinate context, source, data, and root Bicep composition left this repository.
- The root `azure.yaml` and the unified root composition were removed.
- Only `deploy/demo1` and `deploy/demo4` remain as Azure Developer CLI contexts.
