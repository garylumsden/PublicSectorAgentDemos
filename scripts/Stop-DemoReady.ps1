[CmdletBinding()]
param(
    [string]$StatePath = (Join-Path (Join-Path $PSScriptRoot '..') '.demo-ready\processes.json'),
    # Includes optional applications started by Invoke-DemoReady.ps1.
    [switch]$OwnedOnly = $true,
    [switch]$RequireManagedIdentity,
    [string[]]$Name = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-DemoReadyOwnedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    if ([string]$Record.name -in @('patriots', 'patriots-coordinate', 'tokens-and-credits')) {
        return $false
    }
    $marker = $Record.PSObject.Properties['targetCommandMarker']
    if ($null -eq $marker) {
        return $false
    }
    $directory = $Record.PSObject.Properties['workingDirectory']
    if ([string]$Record.name -ceq 'cross-government-coordinate') {
        # Match the old or new path as a pair. Process identity checks still run before stopping.
        foreach ($relative in @('src\PublicSectorAgentDemos.Demo3.Coordinate', 'demos\cross-government')) {
            $contextPath = Join-Path $RepositoryRoot $relative
            $projectPath = Join-Path $contextPath 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
            if ([string]$marker.Value -ceq $projectPath -and
                ($null -eq $directory -or [IO.Path]::GetFullPath([string]$directory.Value).Equals(
                    $contextPath, [StringComparison]::OrdinalIgnoreCase))) {
                return $true
            }
        }
        return $false
    }
    if ([string]$Record.name -ceq 'demo1-comparison') {
        return $null -ne $directory -and
            [IO.Path]::GetFullPath([string]$directory.Value).Equals(
                $RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [string]$marker.Value -ceq (Join-Path $RepositoryRoot 'src\PublicSectorAgentDemos.Demo1.Web\PublicSectorAgentDemos.Demo1.Web.csproj')
    }
    if ([string]$Record.name -cne 'presenter') {
        return $false
    }
    $expectedDirectory = $RepositoryRoot
    if ($null -ne $directory -and
        -not [IO.Path]::GetFullPath([string]$directory.Value).Equals(
            [IO.Path]::GetFullPath($expectedDirectory), [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    if ([string]$Record.name -ceq 'presenter') {
        if ([string]$marker.Value -ceq (Join-Path $RepositoryRoot 'src\PublicSectorAgentDemos.Presenter\PublicSectorAgentDemos.Presenter.csproj')) {
            return $true
        }
        if ([string]$marker.Value -cne 'PublicSectorAgentDemos.Presenter') {
            return $false
        }
        if ($null -ne $directory) {
            return $true
        }
        # Legacy records lack a working directory. Require the supervisor's absolute config path.
        $hostProcess = Get-CimInstance -ClassName Win32_Process `
            -Filter "ProcessId = $([int]$Record.pid)" -ErrorAction Stop
        $commandLine = $null -eq $hostProcess ? '' : [string]$hostProcess.CommandLine
        return $commandLine.Contains(
            (Join-Path $RepositoryRoot '.demo-ready\logs\.presenter.process-host.json'),
            [StringComparison]::OrdinalIgnoreCase)
    }
    return $false
}

function Test-DemoReadyManagedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    if ([string]$Record.name -notin @('tokens-and-credits', 'external-patriots')) {
        return Test-DemoReadyOwnedProcess -Record $Record -RepositoryRoot $RepositoryRoot
    }
    foreach ($property in @('optionalExternalOwner', 'workingDirectory', 'targetCommandMarker', 'commandMarker')) {
        if ($null -eq $Record.PSObject.Properties[$property] -or
            [string]::IsNullOrWhiteSpace([string]$Record.$property)) {
            return $false
        }
    }
    $project = [string]$Record.name -ceq 'tokens-and-credits' `
        ? (Join-Path $Record.workingDirectory 'src\TokensAndCredits.Web\TokensAndCredits.Web.csproj') `
        : (Join-Path $Record.workingDirectory 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj')
    return [IO.Path]::GetFullPath([string]$Record.optionalExternalOwner).Equals(
            $RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::IsPathFullyQualified([string]$Record.workingDirectory) -and
        [string]$Record.targetCommandMarker -ceq $project -and
        [string]$Record.commandMarker -ceq 'Start-DemoReadyProcessHost.ps1'
}

function ConvertTo-DemoReadyUtcTimestamp {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Value)

    if ($Value -is [DateTimeOffset]) {
        return $Value.ToUniversalTime()
    }
    if ($Value -is [DateTime]) {
        return [DateTimeOffset]$Value.ToUniversalTime()
    }
    return [DateTimeOffset]::Parse(
        [string]$Value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
}

function Get-DemoReadyCommandHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CommandLine)

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($CommandLine)))
}

function Test-DemoReadyProcessIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][Diagnostics.Process]$Process
    )

    if ($Process.HasExited -or
        $Process.Id -ne [int]$Record.pid -or
        $Process.ProcessName -cne [string]$Record.processName) {
        return $false
    }

    try {
        $expectedStart = ConvertTo-DemoReadyUtcTimestamp $Record.startTimeUtc
        $actualStart = [DateTimeOffset]$Process.StartTime.ToUniversalTime()
        if ($actualStart.UtcTicks -ne $expectedStart.UtcTicks) {
            return $false
        }
    }
    catch {
        return $false
    }

    try {
        if (-not [string]::IsNullOrWhiteSpace([string]$Record.executablePath) -and
            -not [IO.Path]::GetFullPath($Process.MainModule.FileName).Equals(
                [IO.Path]::GetFullPath([string]$Record.executablePath),
                [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    catch {
        return $false
    }

    try {
        $commandLine = Get-CimInstance `
            -ClassName Win32_Process `
            -Filter "ProcessId = $($Process.Id)" |
            Select-Object -ExpandProperty CommandLine
        if (-not [string]::IsNullOrWhiteSpace([string]$Record.commandMarker) -and
            ([string]::IsNullOrWhiteSpace($commandLine) -or -not $commandLine.Contains(
                [string]$Record.commandMarker,
                [StringComparison]::OrdinalIgnoreCase))) {
            return $false
        }
        $commandHash = $Record.PSObject.Properties['commandHash']
        if ($null -ne $commandHash -and
            ([string]::IsNullOrWhiteSpace($commandLine) -or
                (Get-DemoReadyCommandHash $commandLine) -cne [string]$commandHash.Value)) {
            return $false
        }
    }
    catch {
        if (-not [string]::IsNullOrWhiteSpace([string]$Record.commandMarker) -or
            $null -ne $Record.PSObject.Properties['commandHash']) {
            return $false
        }
    }
    return $true
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

function Get-DemoReadyProcessTreeSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$RootPid,
        [Parameter(Mandatory)][DateTimeOffset]$RootStartTimeUtc
    )

    $processes = @(Get-CimInstance Win32_Process |
        Select-Object ProcessId, ParentProcessId, CreationDate, Name, ExecutablePath, CommandLine)
    $capturedRoot = @($processes | Where-Object ProcessId -eq $RootPid)
    if ($capturedRoot.Count -ne 1 -or $null -eq $capturedRoot[0].CreationDate) {
        throw "The root PID $RootPid could not be captured with its creation time."
    }
    $capturedRootStart = ConvertTo-DemoReadyUtcTimestamp $capturedRoot[0].CreationDate
    # CIM creation times have microsecond precision; Process.StartTime retains 100-nanosecond ticks.
    if ($capturedRootStart.UtcTicks -ne ($RootStartTimeUtc.UtcTicks - $RootStartTimeUtc.UtcTicks % 10)) {
        throw "The root PID $RootPid changed before its identity was captured."
    }
    $ids = [Collections.Generic.HashSet[int]]::new()
    $null = $ids.Add($RootPid)
    $pending = [Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootPid)
    while ($pending.Count -gt 0) {
        $parent = $pending.Dequeue()
        $capturedParent = $processes | Where-Object ProcessId -eq $parent | Select-Object -First 1
        $parentStart = ConvertTo-DemoReadyUtcTimestamp $capturedParent.CreationDate
        foreach ($child in $processes | Where-Object ParentProcessId -eq $parent) {
            if ($null -eq $child.CreationDate) {
                throw "The child PID $($child.ProcessId) has no captured creation time."
            }
            $childStart = ConvertTo-DemoReadyUtcTimestamp $child.CreationDate
            if ($childStart.UtcTicks -lt $parentStart.UtcTicks) {
                continue
            }
            $childId = [int]$child.ProcessId
            if ($ids.Add($childId)) {
                $pending.Enqueue($childId)
            }
        }
    }

    return @($ids | ForEach-Object {
        $processId = [int]$_
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            $captured = $processes | Where-Object ProcessId -eq $processId | Select-Object -First 1
            if ([string]::IsNullOrWhiteSpace([string]$captured.ExecutablePath) -or
                [string]::IsNullOrWhiteSpace([string]$captured.CommandLine)) {
                throw "The captured PID $processId has incomplete command identity."
            }
            $capturedStart = ConvertTo-DemoReadyUtcTimestamp $captured.CreationDate
            $actualStart = [DateTimeOffset]$process.StartTime.ToUniversalTime()
            if ($capturedStart.UtcTicks -ne ($actualStart.UtcTicks - $actualStart.UtcTicks % 10)) {
                throw "The captured PID $processId changed before its identity was captured."
            }
            $record = [pscustomobject]@{
                pid = $processId
                startTimeUtc = $actualStart.ToString('O')
                processName = [IO.Path]::GetFileNameWithoutExtension([string]$captured.Name)
                executablePath = [string]$captured.ExecutablePath
                commandMarker = ''
                commandHash = Get-DemoReadyCommandHash ([string]$captured.CommandLine)
            }
            if (-not (Test-DemoReadyProcessIdentity -Record $record -Process $process)) {
                throw "The captured PID $processId changed before its identity was recorded."
            }
            $record
        }
    })
}

function Stop-DemoReadyVerifiedSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Snapshot)

    # The job can stop its supervisor before a child exits. Recheck every captured PID.
    $reverse = @($Snapshot)
    [Array]::Reverse($reverse)
    foreach ($item in $reverse) {
        $process = Get-Process -Id ([int]$item.pid) -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            continue
        }
        if (-not (Test-DemoReadyProcessIdentity -Record $item -Process $process)) {
            if ($process.HasExited -or $null -eq (Get-Process -Id ([int]$item.pid) -ErrorAction SilentlyContinue)) {
                continue
            }
            throw "The captured PID $($item.pid) no longer has its recorded identity."
        }
        try {
            Stop-Process -Id $process.Id -ErrorAction Stop
        }
        catch {
            if ($process.HasExited -or $null -eq (Get-Process -Id ([int]$item.pid) -ErrorAction SilentlyContinue)) {
                continue
            }
            throw
        }
    }
}

function Test-DemoReadyTreeStopped {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Snapshot)

    foreach ($item in $Snapshot) {
        $process = Get-Process -Id ([int]$item.pid) -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            continue
        }
        try {
            $expected = ConvertTo-DemoReadyUtcTimestamp $item.startTimeUtc
            $actual = [DateTimeOffset]$process.StartTime.ToUniversalTime()
            if ($actual.UtcTicks -eq $expected.UtcTicks) {
                return $false
            }
        }
        catch {
            return $false
        }
    }
    return $true
}

function Write-DemoReadyProcessState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Processes
    )

    $parent = Split-Path -Parent $Path
    $temporary = Join-Path `
        $parent `
        ".$([IO.Path]::GetFileName($Path)).$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [ordered]@{
            version = 1
            generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
            processes = $Processes
        } | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

if ($MyInvocation.InvocationName -eq '.') {
    return
}

$resolvedStatePath = [IO.Path]::GetFullPath($StatePath)
if (-not $OwnedOnly -and $Name.Count -eq 0) {
    throw 'An explicit -Name is required when -OwnedOnly is false.'
}
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not (Test-Path -LiteralPath $resolvedStatePath -PathType Leaf)) {
    Write-Host 'No demo-ready process state exists.'
    return
}
$state = Get-Content -LiteralPath $resolvedStatePath -Raw | ConvertFrom-Json
$remaining = [Collections.Generic.List[object]]::new()
Initialize-DemoReadyJobApi
foreach ($record in @($state.processes)) {
    $isOptionalManaged = [string]$record.name -in @('tokens-and-credits', 'external-patriots')
    if ([string]$record.name -in @('patriots', 'patriots-coordinate') -or
        ($Name.Count -gt 0 -and [string]$record.name -cnotin $Name) -or
        ($RequireManagedIdentity -and
            -not (Test-DemoReadyManagedProcess -Record $record -RepositoryRoot $repositoryRoot)) -or
        (-not $RequireManagedIdentity -and $OwnedOnly -and
            -not (Test-DemoReadyOwnedProcess -Record $record -RepositoryRoot $repositoryRoot)) -or
        (-not $RequireManagedIdentity -and -not $OwnedOnly -and $isOptionalManaged -and
            -not (Test-DemoReadyManagedProcess -Record $record -RepositoryRoot $repositoryRoot))) {
        $remaining.Add($record)
        continue
    }
    $process = Get-Process -Id ([int]$record.pid) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        if ($null -ne $record.PSObject.Properties['stopSnapshot']) {
            try {
                Stop-DemoReadyVerifiedSnapshot -Snapshot @($record.stopSnapshot)
                if (-not (Test-DemoReadyTreeStopped -Snapshot @($record.stopSnapshot))) {
                    throw 'Captured child processes are still running.'
                }
            }
            catch {
                Write-Warning "Retained the process record for $($record.name): $($_.Exception.Message)"
                $remaining.Add($record)
                continue
            }
        }
        elseif ($null -ne $record.PSObject.Properties['targetPid'] -and
            $null -ne (Get-Process -Id ([int]$record.targetPid) -ErrorAction SilentlyContinue)) {
            Write-Warning "The supervisor exited but target PID $($record.targetPid) remains. Retained the record for explicit review."
            $record.status = 'orphan-unverified'
            $remaining.Add($record)
            continue
        }
        Write-Host "Removed the stale process record for $($record.name) at PID $($record.pid)."
        continue
    }
    if (-not (Test-DemoReadyProcessIdentity -Record $record -Process $process)) {
        Write-Warning "Skipped PID $($record.pid) because its identity did not match $($record.name)."
        $record.status = 'identity-unverified'
        $remaining.Add($record)
        continue
    }
    if ([string]$record.jobName -notmatch '^Local\\PublicSectorAgentDemos-[0-9]+-[0-9a-f]{32}$') {
        Write-Warning "Skipped PID $($record.pid) because its process job was invalid."
        $record.status = 'job-unverified'
        $remaining.Add($record)
        continue
    }

    $snapshot = Get-DemoReadyProcessTreeSnapshot -RootPid $process.Id `
        -RootStartTimeUtc (ConvertTo-DemoReadyUtcTimestamp $record.startTimeUtc)
    $record | Add-Member -NotePropertyName stopSnapshot -NotePropertyValue $snapshot -Force
    try {
        [DemoReadyJobObject]::Terminate([string]$record.jobName)
    }
    catch {
        Write-Warning "The named job for PID $($record.pid) could not be stopped. Checking its captured process identities."
    }
    try {
        Stop-DemoReadyVerifiedSnapshot -Snapshot $snapshot
    }
    catch {
        Write-Warning "The process tree for PID $($record.pid) could not be stopped: $($_.Exception.Message)"
        $record.status = 'stop-failed'
        $remaining.Add($record)
        continue
    }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt 10 -and
        -not (Test-DemoReadyTreeStopped -Snapshot $snapshot))
    if (-not (Test-DemoReadyTreeStopped -Snapshot $snapshot)) {
        Write-Warning "The process tree for PID $($record.pid) did not stop within 10 seconds."
        $record.status = 'stop-unverified'
        $remaining.Add($record)
    }
    else {
        Write-Host "Stopped the $($record.name) process tree at root PID $($record.pid)."
    }
}

Write-DemoReadyProcessState -Path $resolvedStatePath -Processes @($remaining)

Write-Host 'Azure resources were not changed.'
