# Final rehearsal

> **Superseded historical record.** Date: 2026-09-03.
> This record describes an earlier Act implementation and vendored council engine.
> The later external-only layout below was also superseded.
> Current bundles live under `src\PublicSectorAgentDemos.Demo2.Act` and `src\PublicSectorAgentDemos.Demo3.Coordinate`.
> Their provenance files identify the current upstream revisions.
> Patriots remains optional and external.
> Use [demo-ready orchestration](demo-ready.md) for the current process.

## Status

Passed for the layout of that date.

## Offline validation

- The restore used the Microsoft package feed proxy.
- The build passed with zero warnings and zero errors.
- The default test suite passed: 160 passed and eight opt-in tests skipped.
- All Bicep and Bicep parameter files compiled.
- The Azure Developer CLI schema passed for every declared context.
- The provisioning preview contained no delete.

## Live rehearsal

| Session | Result | Proof |
|---|---|---|
| Foundation | Passed | The opt-in Demo 1 parity test passed. |
| Ground | Passed | The same test verified retrieval, citations, and refusal behavior. |
| Act | Passed | Both health endpoints and authenticated canonical MCP evidence returned HTTP 200. |
| Cross-Government Coordinate | Passed | The saved corpus contained embedded Assessments and a saved Nexus. |
| Patriots Coordinate | Passed | The local application from the external repository returned HTTP 200 on both fixed URLs. |
| Hosted | Passed | All four Demo 4 cloud tests passed. |

## Presenter warm-up

The Presenter page returned HTTP 200 on port 5088.
The final warm-up returned `success` for all six cards.
Cross-Government Coordinate used ports 5080 and 7080.
Patriots used ports 5081 and 7081.
Every external project build passed with zero warnings and zero errors.
Every external repository stayed unchanged.

## Intermediate external-only layout (superseded)

- The Act source, its deployment context, and its Bicep templates left this repository.
- The vendored council engine, its data, and its root Bicep composition left this repository.
- The root `azure.yaml` was removed. Only `deploy/demo1` and `deploy/demo4` remain.
- At the recorded rehearsal date, startup resolved three external repositories and did not deploy them.
- At the recorded rehearsal date, teardown removed only the Demo 1 and Demo 4 azd environments.
- Each external demo then owned its own telemetry.
