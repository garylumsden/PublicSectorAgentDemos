# Act: cross-government flood support

## Scenario and actors

Flooding displaces 180 households in Riverton.
Local emergency accommodation reaches capacity at 18:00.
A government training centre's previous report lists 90 rooms; its latest report lists 60.
The agent must explain the conflict instead of assuming the larger figure is available.

The council emergency coordinator submits the incident.
MHCLG, DfT, and NHS liaison roles supply accommodation, transport, and support context.
An authorised incident commander approves or rejects the proposed package.
The liaison roles supply evidence; the incident commander makes the reservation decision.

## MCP tools

Model Context Protocol (MCP) connects the deployed agent to four tools at `/mcp/flood-support`.

| Tool | Purpose | Approval |
|---|---|---|
| `getSituationReports` | Read the incident reports, deadlines, aggregate needs, and conflicting evidence. | Not required |
| `findAccommodation` | Read accommodation options and accessibility capacity. | Not required |
| `checkTransportCapacity` | Read vehicles, passenger places, and accessible transport places. | Not required |
| `reserveSupportPackage` | Record one bounded, versioned support package. | Always required |

The registered agent is `cross-government-flood-support`.
Its project connection is `flood-support-connection`.
The server label is `flood-support`.
The bootstrap rejects missing approval requirements or an incorrect tool list.

## Main package

Use incident area `AREA-1001` and package `PKG-RIV-060`, version `1`.

| Item | Requested amount |
|---|---|
| Accommodation | 60 rooms at Riverton Government Training Centre |
| Accessible accommodation | 12 of those 60 rooms |
| Transport | 2 coaches, with 120 passenger places in total |
| Accessible transport | 4 of those passenger places |
| Duration | 24 hours |
| Estimated cost | GBP 8,100: 60 rooms at GBP 95 plus 2 coaches at GBP 1,200 |
| Unmet accommodation need | 120 of the 180 households |

Package amounts are fixed for this demonstration; they are not live supplier quotations.
For this fixture, one room accommodates one household.
This assumption does not establish a real accommodation standard.
The agent cannot invent a different price or silently increase the approved quantities.

## Presenter input

Use the Act input in `config\presenter\sessions.v1.json`.
It includes the household count, accessibility requirements, transport requirements, conflicting capacity reports, and 24-hour request.
Use the same incident area in the application form.
The editable scenario cards provide additional starting cases.

## Approval demonstration

1. Submit the incident and inspect the evidence and deterministic policy results.
2. Wait for the reservation approval modal.
3. Explain the requested site, rooms, transport, accessibility, duration, and estimated cost.
4. Explain why the package is requested and which evidence supports it.
5. Identify the unresolved risks and the 120 households that this package cannot accommodate.
6. Inspect the incident reference, package version, exact tool arguments, and approval expiry.
7. Enter an audit-safe decision rationale.
8. Approve the package or reject the request.
9. Show the reservation receipt and the case record after approval.

Approval authorises one reservation for the displayed incident and package.
It does not authorise expenditure, evacuation, eligibility decisions, or another tool call.
Rejection creates no reservation.
Expired, malformed, mismatched, or changed requests must not bypass the approval boundary.
Changing a package requires a new approval.

If a local continuation stops, the workflow checks the reservation ledger.
It retires an unexecuted request or records the confirmed receipt.
An uncertain remote outcome retains its original action ID and package.
Use **Reconcile original reservation** in the case record while its approval remains valid.
This operation can return an existing receipt or complete that same approved request; it cannot select another package.
If approval has expired, check the original action with the service operator before making another request.
An unresolved outcome blocks reassessment and urgency changes for that case.

## Local and deployed execution

Local deterministic mode uses the supplied fixtures and the same decision modal.
It is not a live Foundry inference or a remote MCP invocation.
The application must identify that mode explicitly.

To run the local simulation, use these commands from the repository root:

```powershell
Set-Location .\src\PublicSectorAgentDemos.Demo2.Act
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'https://localhost:7092;http://localhost:5092'
dotnet run --project .\src\Demo2.Web\Demo2.Web.csproj -c Release --no-launch-profile -- `
  '--environment=Development' `
  '--AZURE_AI_PROJECT_ENDPOINT=' '--AZURE_COSMOS_ENDPOINT=' `
  '--DEFRA_TOOLS_MCP_URL=' '--DEFRA_TOOLS_MCP_FLOOD_SUPPORT_URL=' `
  '--Demo2:Agent:Mode=LocalContract' '--Demo2:Persistence:Provider=InMemory'
```

Open `https://localhost:7092/` with a trusted ASP.NET Core development certificate.
Stop the process with `Ctrl+C` before rebuilding it.
The empty endpoint arguments override inherited cloud settings, including local user secrets, without changing stored configuration.
The health endpoint must report `agentMode: LocalContract` and `persistence: InMemory`.
The approval and rejection workflow remains a manual presenter check.
Local state and simulated resource reservations reset when the process stops.
Select **Reset reservations** between demonstrations to clear the reservation inventory and restore room and transport capacity.
Reset starts a fresh assessment but keeps previous case and audit records.
It changes the inventory generation, so approval requests issued before reset cannot reserve resources afterward.
The reset control is a presenter action, not a tool available to the agent.

The deployed mode uses the registered Foundry agent and the authenticated MCP endpoint.
Do not connect this demonstration directly to real accommodation or transport booking systems.
Such an integration needs authoritative inventory, durable allocation controls, and a verified approval authority.

The source branch does not update an existing deployment automatically.
See the [deployment guide](../../src/PublicSectorAgentDemos.Demo2.Act/infra/README.md) before an approved deployment.
The new `demo2-flood-support-cases` container keeps scenario state separate from existing welfare records.
No previous cases, agents, application registrations, or resource groups are deleted by the scenario change.

## Source and compatibility

The workflow adapts the imported DEFRA Demo 2 implementation.
Its provenance manifests retain the original source revision and initial extraction evidence.
Project names, namespaces, and some internal compatibility identifiers remain unchanged.
The active prompts, MCP contracts, fixtures, approval context, and operator-facing content describe flood support.

The integration retains the existing Agent Framework and Foundry SDK patterns.
See [Agent Framework hosted MCP tools](https://learn.microsoft.com/agent-framework/agents/tools/hosted-mcp-tools) for the platform approval mechanism.
The package catalogue and simulation are specific to this demonstration.
