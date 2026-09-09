# Contributing

This repository supports a Microsoft Foundry demonstration.
Keep changes focused, clear, and safe for public use.

## Before a change

1. Do not add credentials, customer data, tenant identifiers, subscription identifiers, or workstation paths.
2. Use synthetic demonstration data only.
3. Keep optional external repositories separate from this repository.
4. Use the configured Microsoft package feed proxy.

## Validate a change

Run:

```powershell
.\scripts\Test-DemoReady.ps1
```

For orchestration-only changes, also run:

```powershell
pwsh -NoProfile -File .\tests\automation\Invoke-DemoReady.Tests.ps1
```

Do not deploy from a pull request unless the repository owner has approved the Azure scope and cost.
