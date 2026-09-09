[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigurationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
if ([string]$configuration.jobName -notmatch '^Local\\PublicSectorAgentDemos-[0-9]+-[0-9a-f]{32}$') {
    throw 'The process job name is invalid.'
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class DemoReadyProcessJob
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSetQuota = 0x0100;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static IntPtr CreateAndAssign(string name, int processId)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, name);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr process = OpenProcess(ProcessTerminate | ProcessSetQuota, false, processId);
        if (process == IntPtr.Zero)
        {
            CloseHandle(job);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            if (!AssignProcessToJobObject(job, process))
            {
                CloseHandle(job);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CloseHandle(process);
        }
        return job;
    }

    public static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }
}
'@

$jobHandle = [IntPtr]::Zero
$process = $null
try {
    $jobHandle = [DemoReadyProcessJob]::CreateAndAssign(
        [string]$configuration.jobName,
        $PID)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = [string]$configuration.filePath
    $startInfo.WorkingDirectory = [string]$configuration.workingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @($configuration.arguments)) {
        $null = $startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'The hosted process did not start.'
    }
    $sensitiveValues = @(
        'AZURE_TENANT_ID',
        'WEBIQ_API_KEY',
        'APPLICATIONINSIGHTS_CONNECTION_STRING',
        'APPINSIGHTS_CONNECTION_STRING'
    ) | ForEach-Object {
        [Environment]::GetEnvironmentVariable($_)
    } | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    }
    Set-Content -LiteralPath ([string]$configuration.stdoutPath) -Value '' -Encoding utf8NoBOM
    Set-Content -LiteralPath ([string]$configuration.stderrPath) -Value '' -Encoding utf8NoBOM
    $stdoutSubscription = Register-ObjectEvent `
        -InputObject $process `
        -EventName OutputDataReceived `
        -MessageData @{
            path = [string]$configuration.stdoutPath
            sensitive = @($sensitiveValues)
        } `
        -Action {
            if ($null -ne $EventArgs.Data) {
                $line = $EventArgs.Data
                foreach ($value in $Event.MessageData.sensitive) {
                    $line = $line.Replace($value, '******', [StringComparison]::Ordinal)
                }
                $line = [regex]::Replace(
                    $line,
                    '(?i)(client[_ -]?secret|access[_ -]?token|api[_ -]?key|authorization)\s*[:=]\s*\S+',
                    '$1=******')
                $line = [regex]::Replace(
                    $line,
                    '(?i)InstrumentationKey=[^;\s]+',
                    'InstrumentationKey=******')
                Add-Content `
                    -LiteralPath $Event.MessageData.path `
                    -Value $line `
                    -Encoding utf8NoBOM
            }
        }
    $stderrSubscription = Register-ObjectEvent `
        -InputObject $process `
        -EventName ErrorDataReceived `
        -MessageData @{
            path = [string]$configuration.stderrPath
            sensitive = @($sensitiveValues)
        } `
        -Action {
            if ($null -ne $EventArgs.Data) {
                $line = $EventArgs.Data
                foreach ($value in $Event.MessageData.sensitive) {
                    $line = $line.Replace($value, '******', [StringComparison]::Ordinal)
                }
                $line = [regex]::Replace(
                    $line,
                    '(?i)(client[_ -]?secret|access[_ -]?token|api[_ -]?key|authorization)\s*[:=]\s*\S+',
                    '$1=******')
                $line = [regex]::Replace(
                    $line,
                    '(?i)InstrumentationKey=[^;\s]+',
                    'InstrumentationKey=******')
                Add-Content `
                    -LiteralPath $Event.MessageData.path `
                    -Value $line `
                    -Encoding utf8NoBOM
            }
        }
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()

    [ordered]@{
        targetPid = $process.Id
        targetStartTimeUtc = $process.StartTime.ToUniversalTime().ToString('O')
    } | ConvertTo-Json |
        Set-Content -LiteralPath ([string]$configuration.startedPath) -Encoding utf8NoBOM

    while (-not $process.WaitForExit(200)) {
        Wait-Event -Timeout 0.05 | Out-Null
    }
    $process.WaitForExit()
    Wait-Event -Timeout 0.1 | Out-Null
    Unregister-Event -SourceIdentifier $stdoutSubscription.Name -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier $stderrSubscription.Name -ErrorAction SilentlyContinue
    exit $process.ExitCode
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
    }
    [DemoReadyProcessJob]::Close($jobHandle)
}
