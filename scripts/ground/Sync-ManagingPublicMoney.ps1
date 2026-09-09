[CmdletBinding()]
param(
    [ValidateSet('Validate', 'Download')]
    [string]$Mode = 'Validate',
    [string]$RepositoryRoot = (Join-Path (Join-Path $PSScriptRoot '..') '..')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
$manifestPath = Join-Path $root 'data\ground\v1\source-manifest.json'
$knowledgeRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'data\ground\v1\knowledge'))
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ($manifest.dataVersion -ne 'v1' -or @($manifest.sources).Count -ne 1) {
    throw 'The Managing Public Money source manifest is invalid.'
}

$source = $manifest.sources[0]
$filename = [string]$source.expectedLocalFilename
if ([System.IO.Path]::GetFileName($filename) -ne $filename -or
    [System.IO.Path]::GetExtension($filename) -ne '.pdf') {
    throw "The source filename is unsafe: $filename"
}

$downloadUri = [Uri]$source.downloadUrl
if (-not $downloadUri.IsAbsoluteUri -or
    $downloadUri.Scheme -ne [Uri]::UriSchemeHttps -or
    $downloadUri.Host -ne 'assets.publishing.service.gov.uk') {
    throw 'The source URL must use the approved GOV.UK asset host over HTTPS.'
}

New-Item -ItemType Directory -Path $knowledgeRoot -Force | Out-Null
$targetPath = [System.IO.Path]::GetFullPath((Join-Path $knowledgeRoot $filename))
if ([System.IO.Path]::GetDirectoryName($targetPath) -ne $knowledgeRoot) {
    throw 'The source path escapes the knowledge directory.'
}

if ($Mode -eq 'Download') {
    $downloadPath = "$targetPath.download"
    try {
        Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
        $response = Invoke-WebRequest `
            -Uri $downloadUri `
            -OutFile $downloadPath `
            -MaximumRedirection 5 `
            -PassThru `
            -UseBasicParsing
        $finalUri = $response.BaseResponse.RequestMessage.RequestUri
        if ($finalUri.Host -ne 'assets.publishing.service.gov.uk') {
            throw "The download redirected to an unapproved host: $($finalUri.Host)"
        }

        Move-Item -LiteralPath $downloadPath -Destination $targetPath -Force
    }
    finally {
        Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
    throw "The pinned source is missing. Run this script with -Mode Download: $targetPath"
}

$file = Get-Item -LiteralPath $targetPath
if ($file.Length -lt 1024) {
    throw "The pinned source is unexpectedly small: $targetPath"
}

$stream = [System.IO.File]::OpenRead($targetPath)
try {
    $header = [byte[]]::new(5)
    if ($stream.Read($header, 0, $header.Length) -ne $header.Length -or
        [System.Text.Encoding]::ASCII.GetString($header) -ne '%PDF-') {
        throw "The pinned source is not a PDF: $targetPath"
    }
}
finally {
    $stream.Dispose()
}

$actualChecksum = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedChecksum = ([string]$source.expectedChecksum).ToLowerInvariant()
if ($actualChecksum -ne $expectedChecksum) {
    throw "SHA-256 drift detected for $filename. Expected $expectedChecksum but received $actualChecksum. Review GOV.UK changes before updating the pin."
}

Write-Host "Managing Public Money source validated: $filename"
Write-Host "SHA-256: $actualChecksum"
