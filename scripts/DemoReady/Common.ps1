# Shared plumbing for the demo-ready orchestrator.
# Dot-source this file. It defines functions only.

function Protect-DemoReadyText {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$Text,
        [string[]]$SensitiveValues = @()
    )

    $protected = [string]$Text
    foreach ($value in @($SensitiveValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $protected = $protected.Replace($value, '******', [StringComparison]::Ordinal)
    }
    $protected = [regex]::Replace(
        $protected,
        '(?i)(client[_ -]?secret|access[_ -]?token|api[_ -]?key|authorization)\s*[:=]\s*\S+',
        '$1=******')
    $protected = [regex]::Replace(
        $protected,
        '(?i)InstrumentationKey=[^;\s]+',
        'InstrumentationKey=******')
    return $protected
}

function Write-DemoReadyStep {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Message)

    Write-DemoReadyStatus -Status 'step' -Message $Message
}

function Write-DemoReadyJsonAtomic {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Value
    )

    $parent = Split-Path -Parent $Path
    $null = New-Item -ItemType Directory -Path $parent -Force
    $temporaryPath = Join-Path `
        $parent `
        ".$([IO.Path]::GetFileName($Path)).$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $Value | ConvertTo-Json -Depth 20 |
            Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
        [IO.File]::Move($temporaryPath, $Path, $true)
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-DemoReadyReportPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][string]$ReportPath
    )

    $repositoryPrefix = "$RepositoryRoot$([IO.Path]::DirectorySeparatorChar)"
    $runtimePrefix = "$RuntimeRoot$([IO.Path]::DirectorySeparatorChar)"
    if ($ReportPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        -not $ReportPath.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'An in-repository report path must be under the ignored .demo-ready directory.'
    }
}

function Invoke-DemoReadyNative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)]
        [string]$WorkingDirectory,
        [string]$LogPath,
        [hashtable]$Environment = @{},
        [string[]]$SensitiveValues = @(),
        [switch]$CaptureOutput,
        [switch]$PreserveCapturedOutput,
        [switch]$Quiet
    )

    $saved = @{}
    try {
        foreach ($entry in $Environment.GetEnumerator()) {
            $saved[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
        }

        Push-Location $WorkingDirectory
        try {
            $lines = [Collections.Generic.List[string]]::new()
            $rawLines = [Collections.Generic.List[string]]::new()
            & $FilePath @Arguments 2>&1 | ForEach-Object {
                $rawLine = ($_ | Out-String).TrimEnd()
                $rawLines.Add($rawLine)
                $line = Protect-DemoReadyText -Text $rawLine -SensitiveValues $SensitiveValues
                $lines.Add($line)
                if (-not $Quiet -and -not [string]::IsNullOrWhiteSpace($line)) {
                    Write-Host $line
                }
            }
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }

        if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
            $parent = Split-Path -Parent $LogPath
            $null = New-Item -ItemType Directory -Path $parent -Force
            $lines | Set-Content -LiteralPath $LogPath -Encoding utf8NoBOM
        }
        if ($exitCode -ne 0) {
            $details = @(
                $lines |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    Select-Object -Last 5
            ) -join ' | '
            if ($details.Length -gt 1200) {
                $details = $details.Substring(0, 1200)
            }
            $suffix = [string]::IsNullOrWhiteSpace($details) ? '' : " Details: $details"
            throw "$FilePath failed with exit code $exitCode.$suffix"
        }
        if ($CaptureOutput) {
            return (($PreserveCapturedOutput ? $rawLines : $lines) -join [Environment]::NewLine)
        }
    }
    finally {
        foreach ($entry in $saved.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
}

function Invoke-DemoReadyAzd {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$WorkingDirectory,
        [string]$LogPath,
        [string[]]$SensitiveValues = @(),
        [switch]$CaptureOutput,
        [switch]$PreserveCapturedOutput,
        [switch]$Quiet
    )

    Invoke-DemoReadyNative `
        -FilePath 'azd' `
        -Arguments $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -LogPath $LogPath `
        -Environment @{ AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill' } `
        -SensitiveValues $SensitiveValues `
        -CaptureOutput:$CaptureOutput `
        -PreserveCapturedOutput:$PreserveCapturedOutput `
        -Quiet:$Quiet
}

function Test-DemoReadyTransientPackageRestoreFailure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    foreach ($marker in @(
        'multiple attempts to download the nupkg have failed',
        'Failed to download package',
        'The SSL connection could not be established',
        'Received an unexpected EOF or 0 bytes from the transport stream'
    )) {
        if ($Message.Contains($marker, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Invoke-DemoReadyAzdWithPackageRestoreRetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$WorkingDirectory,
        [string]$LogPath,
        [string[]]$SensitiveValues = @(),
        [ValidateRange(1, 5)]
        [int]$MaximumAttempts = 3,
        [ValidateRange(0, 300)]
        [int]$RetryDelaySeconds = 15
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            Invoke-DemoReadyAzd `
                -Arguments $Arguments `
                -WorkingDirectory $WorkingDirectory `
                -LogPath $LogPath `
                -SensitiveValues $SensitiveValues
            return
        }
        catch {
            if (-not [string]::IsNullOrWhiteSpace($LogPath) -and
                (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
                $attemptLogPath = Join-Path `
                    (Split-Path -Parent $LogPath) `
                    "$([IO.Path]::GetFileNameWithoutExtension($LogPath))-attempt-$attempt$([IO.Path]::GetExtension($LogPath))"
                Copy-Item -LiteralPath $LogPath -Destination $attemptLogPath -Force
            }

            if ($attempt -eq $MaximumAttempts -or
                -not (Test-DemoReadyTransientPackageRestoreFailure -Message $_.Exception.Message)) {
                throw
            }

            Write-Warning (
                "The remote package restore failed because of a transient feed error. " +
                "Retrying azd in $RetryDelaySeconds seconds " +
                "(attempt $($attempt + 1) of $MaximumAttempts)."
            )
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }
}

function Get-DemoReadyAzdValues {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContextPath,
        [Parameter(Mandatory)][string]$EnvironmentName,
        [string[]]$SensitiveValues = @()
    )

    $dotenv = Invoke-DemoReadyAzd `
        -Arguments @('env', 'get-values', '--environment', $EnvironmentName) `
        -WorkingDirectory $ContextPath `
        -SensitiveValues $SensitiveValues `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    if ([string]::IsNullOrWhiteSpace($dotenv)) {
        throw "The '$EnvironmentName' azd environment returned no values."
    }
    $values = @{}
    foreach ($line in $dotenv -split '\r?\n') {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line.StartsWith('Update available:', [StringComparison]::Ordinal) -or
            $line.StartsWith('To update, run', [StringComparison]::Ordinal)) {
            continue
        }
        $match = [regex]::Match($line, '^(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?<value>.*)$')
        if (-not $match.Success) {
            throw "The '$EnvironmentName' azd environment returned an invalid line."
        }
        $name = $match.Groups['name'].Value
        $encodedValue = $match.Groups['value'].Value
        if ($encodedValue.StartsWith('"', [StringComparison]::Ordinal) -and
            $encodedValue.EndsWith('"', [StringComparison]::Ordinal)) {
            $values[$name] = [string]($encodedValue | ConvertFrom-Json)
        }
        else {
            $values[$name] = $encodedValue
        }
    }
    return $values
}

function Get-DemoReadyAzdEnvironments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContextPath,
        [string[]]$SensitiveValues = @()
    )

    $output = Invoke-DemoReadyAzd -Arguments @('env', 'list', '--output', 'json') `
        -WorkingDirectory $ContextPath -SensitiveValues $SensitiveValues `
        -CaptureOutput -PreserveCapturedOutput -Quiet
    $json = (@($output -split '\r?\n' | Where-Object {
        -not $_.StartsWith('Update available:', [StringComparison]::Ordinal) -and
        -not $_.StartsWith('To update, run', [StringComparison]::Ordinal)
    }) -join "`n").Trim()
    if (-not $json.StartsWith('[') -or -not $json.EndsWith(']')) {
        throw 'azd env list did not return an environment array. No environment was created.'
    }
    return @($json | ConvertFrom-Json)
}

function Set-DemoReadyAzdValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContextPath,
        [Parameter(Mandatory)][string]$EnvironmentName,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [string[]]$SensitiveValues = @()
    )

    Invoke-DemoReadyAzd `
        -Arguments @('env', 'set', $Name, $Value, '--environment', $EnvironmentName) `
        -WorkingDirectory $ContextPath `
        -SensitiveValues (@($SensitiveValues) + @($Value)) `
        -Quiet
}

function Get-DemoReadyNuGetProxyUri {
    return 'https://packagefeedproxy.microsoft.io/nuget/v3/index.json'
}

function Assert-DemoReadyNuGetProxy {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $proxyUri = Get-DemoReadyNuGetProxyUri
    $nugetConfig = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'NuGet.config') -Raw
    if (-not $nugetConfig.Contains($proxyUri, [StringComparison]::Ordinal)) {
        throw 'NuGet.config must use the Microsoft package feed proxy.'
    }
    $proxyResponse = Invoke-WebRequest `
        -Uri $proxyUri `
        -Method Get `
        -UseBasicParsing `
        -TimeoutSec 30
    if ([int]$proxyResponse.StatusCode -ne 200) {
        throw 'The Microsoft NuGet package feed proxy is not reachable.'
    }
}

function Get-DemoReadyRestoreArgument {
    # Returns restore arguments that force the Microsoft package feed proxy.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryPath,
        [Parameter(Mandatory)][string]$ProjectPath
    )

    $proxyUri = Get-DemoReadyNuGetProxyUri
    $arguments = @('restore', $ProjectPath)
    $configPath = Join-Path $RepositoryPath 'NuGet.config'
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        $content = Get-Content -LiteralPath $configPath -Raw
        if (-not $content.Contains($proxyUri, [StringComparison]::Ordinal)) {
            throw "The NuGet configuration in '$RepositoryPath' must use the Microsoft package feed proxy."
        }
        return $arguments + @('--configfile', $configPath)
    }
    return $arguments + @('--source', $proxyUri)
}

function Invoke-DemoReadyProjectBuild {
    # Restores and builds one project through the Microsoft package feed proxy.
    # Startup uses this to build only the applications it starts.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryPath,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$LogRoot,
        [Parameter(Mandatory)][string]$LogName,
        [string[]]$SensitiveValues = @()
    )

    $restoreArguments = Get-DemoReadyRestoreArgument `
        -RepositoryPath $RepositoryPath `
        -ProjectPath $ProjectPath
    Invoke-DemoReadyNative `
        -FilePath 'dotnet' `
        -Arguments $restoreArguments `
        -WorkingDirectory $RepositoryPath `
        -LogPath (Join-Path $LogRoot "$LogName-restore.log") `
        -SensitiveValues $SensitiveValues
    Invoke-DemoReadyNative `
        -FilePath 'dotnet' `
        -Arguments @('build', $ProjectPath, '--no-restore') `
        -WorkingDirectory $RepositoryPath `
        -LogPath (Join-Path $LogRoot "$LogName-build.log") `
        -SensitiveValues $SensitiveValues
}

function Assert-DemoReadyPortsFree {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int[]]$Ports)

    foreach ($port in $Ports) {
        $listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        if ($null -ne $listener) {
            throw "TCP port $port is already in use."
        }
    }
}

function Initialize-DemoReadyJobApi {
    if ($null -ne ('DemoReadyJobObject' -as [type])) {
        return
    }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class DemoReadyJobObject
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSetQuota = 0x0100;
    private const uint JobObjectTerminate = 0x0008;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenJobObject(uint access, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static void CreateAndAssign(string name, int processId)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, name);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            IntPtr process = OpenProcess(
                ProcessTerminate | ProcessSetQuota,
                false,
                processId);
            if (process == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                if (!AssignProcessToJobObject(job, process))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }
        finally
        {
            CloseHandle(job);
        }
    }

    public static void Terminate(string name)
    {
        IntPtr job = OpenJobObject(JobObjectTerminate, false, name);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            if (!TerminateJobObject(job, 1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CloseHandle(job);
        }
    }
}
'@
}

function Start-DemoReadyProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][hashtable]$Environment,
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][string]$CommandMarker,
        [Parameter(Mandatory)][string[]]$Endpoints,
        [Parameter(Mandatory)][string]$ScriptRoot,
        [string]$OptionalExternalOwner = '',
        [string[]]$SensitiveValues = @()
    )

    if ($Name -in @('patriots', 'patriots-coordinate')) {
        throw 'Patriots is link-only and cannot be started by this repository.'
    }
    if (-not [string]::IsNullOrEmpty($OptionalExternalOwner) -and $Name -cne 'tokens-and-credits') {
        throw 'Only Tokens and Credits can be an explicitly managed external application.'
    }
    $jobName = "Local\PublicSectorAgentDemos-$PID-$([Guid]::NewGuid().ToString('N'))"
    $configurationPath = Join-Path $LogDirectory ".$Name.process-host.json"
    $startedPath = Join-Path $LogDirectory ".$Name.process-host.started.json"
    Remove-Item -LiteralPath $startedPath -Force -ErrorAction SilentlyContinue
    Write-DemoReadyJsonAtomic `
        -Path $configurationPath `
        -Value ([ordered]@{
            filePath = $FilePath
            arguments = $Arguments
            workingDirectory = $WorkingDirectory
            jobName = $jobName
            stdoutPath = Join-Path $LogDirectory "$Name.stdout.log"
            stderrPath = Join-Path $LogDirectory "$Name.stderr.log"
            startedPath = $startedPath
        })

    $processHostPath = Join-Path $ScriptRoot 'Start-DemoReadyProcessHost.ps1'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'pwsh'
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @(
        '-NoProfile',
        '-File',
        $processHostPath,
        '-ConfigurationPath',
        $configurationPath
    )) {
        $null = $startInfo.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[[string]$entry.Key] = [string]$entry.Value
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Unable to start the $Name process host."
    }

    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $startedPath) -and
            -not $process.HasExited -and
            $timer.Elapsed.TotalSeconds -lt 15) {
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath $startedPath -PathType Leaf)) {
            throw "The $Name process host did not start its target."
        }
        $started = Get-Content -LiteralPath $startedPath -Raw | ConvertFrom-Json
        if ([int]$started.targetPid -le 0) {
            throw "The $Name process host returned an invalid target PID."
        }
    }
    catch {
        if (-not $process.HasExited) {
            $process.Kill($true)
        }
        throw
    }
    finally {
        Remove-Item -LiteralPath $configurationPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $startedPath -Force -ErrorAction SilentlyContinue
    }

    $executablePath = try {
        $process.MainModule.FileName
    }
    catch {
        [string](Get-Command $FilePath).Source
    }
    return [pscustomobject]@{
        name = $Name
        pid = $process.Id
        processName = $process.ProcessName
        executablePath = $executablePath
        startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O')
        commandMarker = 'Start-DemoReadyProcessHost.ps1'
        targetCommandMarker = $CommandMarker
        targetPid = [int]$started.targetPid
        targetStartTimeUtc = [string]$started.targetStartTimeUtc
        workingDirectory = $WorkingDirectory
        optionalExternalOwner = $OptionalExternalOwner
        jobName = $jobName
        endpoints = $Endpoints
        status = 'running'
        process = $process
    }
}

function Test-DemoReadyEndpointStatusCode {
    [CmdletBinding()]
    param([AllowNull()][object]$StatusCode)

    if ($null -eq $StatusCode) {
        return $false
    }
    $code = [int]$StatusCode
    return $code -ge 200 -and $code -lt 400
}

function Wait-DemoReadyEndpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 120,
        [switch]$RequireSuccess,
        [switch]$SkipCertificateCheck
    )

    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            $parameters = @{
                Uri = $Uri
                Method = 'Get'
                TimeoutSec = 10
                MaximumRedirection = 0
            }
            if ($SkipCertificateCheck) {
                $parameters.SkipCertificateCheck = $true
            }
            $response = Invoke-WebRequest @parameters
            if (($RequireSuccess -and [int]$response.StatusCode -eq 200) -or
                (-not $RequireSuccess -and (Test-DemoReadyEndpointStatusCode $response.StatusCode))) {
                return
            }
        }
        catch {
            $statusCode = $null
            $responseProperty = $_.Exception.PSObject.Properties['Response']
            if ($null -ne $responseProperty -and $null -ne $responseProperty.Value) {
                $statusProperty = $responseProperty.Value.PSObject.Properties['StatusCode']
                if ($null -ne $statusProperty) {
                    $statusCode = $statusProperty.Value
                }
            }
            if (-not $RequireSuccess -and (Test-DemoReadyEndpointStatusCode $statusCode)) {
                return
            }
            if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                throw "The endpoint did not become ready: $Uri"
            }
        }
        Start-Sleep -Seconds 2
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "The endpoint did not become ready: $Uri"
}

function ConvertTo-DemoReadyProcessFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Processes,
        [object[]]$PreservedProcesses = @()
    )

    [ordered]@{
        version = 1
        generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        processes = @($Processes | ForEach-Object {
            [ordered]@{
                name = $_.name
                pid = $_.pid
                processName = $_.processName
                executablePath = $_.executablePath
                startTimeUtc = $_.startTimeUtc
                commandMarker = $_.commandMarker
                targetCommandMarker = $_.targetCommandMarker
                targetPid = $_.targetPid
                targetStartTimeUtc = $_.targetStartTimeUtc
                workingDirectory = $_.workingDirectory
                optionalExternalOwner = $null -eq $_.PSObject.Properties['optionalExternalOwner'] `
                    ? '' : $_.optionalExternalOwner
                jobName = $_.jobName
                endpoints = $_.endpoints
                status = $_.status
            }
        }) + @($PreservedProcesses)
    }
}
