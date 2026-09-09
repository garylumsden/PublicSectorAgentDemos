$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot '..\..\..\infra\demo4\hooks\configure-foundry.ps1')
