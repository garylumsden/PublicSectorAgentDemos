$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot '..\..\..\infra\demo4\hooks\configure-hosted-agent.ps1')
