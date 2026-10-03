#requires -Version 7.0
<#
One explicitly launched Desktop trial. Run from Explorer in a normal user session.
This script waits for the original Desktop to exit; it never closes that Desktop.
Only the new Desktop child's environment receives the experimental WS endpoint.
No prompt is submitted. At expiry, uncertain or active work prevents service cleanup.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DesktopImage,
    [Parameter(Mandatory)][string]$CodexImage,
    [Parameter(Mandatory)][string]$ProbeImage,
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$WaitDesktopProcessId,
    [Parameter(Mandatory)][datetimeoffset]$WaitDesktopStartUtc,
    [Parameter(Mandatory)][string]$RunDirectory,
    [ValidateRange(1, 3600)][int]$MaxWaitSeconds = 900,
    [ValidateRange(10, 30)][int]$ObservationSeconds = 30,
    [ValidateRange(60, 900)][int]$MaxLifetimeSeconds = 900
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$server = $null; $desktop = $null; $claim = $null; $manifestPath = $null
$serverStarted = $false; $desktopStarted = $false
$manifest = [ordered]@{ schemaVersion = 1; phase = 'preflight'; desktopVerified = $false; inputSubmitted = $false }
$failureCode = 'preflight_failed'; $drains = [System.Collections.Generic.List[object]]::new()
$failureStep = 'preflight'

function Safe-Failure([System.Management.Automation.ErrorRecord]$Record) {
    $causes = @()
    $exception = $Record.Exception
    for ($depth = 0; $null -ne $exception -and $depth -lt 4; $depth++) {
        $cause = @{ type = $exception.GetType().FullName; hresult = $exception.HResult }
        if ($exception -is [ComponentModel.Win32Exception]) { $cause.nativeErrorCode = $exception.NativeErrorCode }
        $causes += $cause
        $exception = $exception.InnerException
    }
    return @{ step = $failureStep; scriptLine = $Record.InvocationInfo.ScriptLineNumber; causes = $causes }
}

function Save-State([string]$Stage) {
    $manifest.phase = $Stage
    $manifest.updatedUtc = [datetimeoffset]::UtcNow.ToString('o')
    if ($manifestPath) {
        try { [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6)) }
        catch { Write-Host 'Trial metadata write failed; no raw diagnostic data was saved.' }
    }
    [pscustomobject]@{ kind = 'trial_stage'; stage = $Stage } | ConvertTo-Json -Compress
}
function New-Child([string]$Image, [string[]]$Arguments, [string]$WorkingDirectory) {
    $si = [Diagnostics.ProcessStartInfo]::new()
    $si.FileName = $Image; $si.WorkingDirectory = $WorkingDirectory
    $si.UseShellExecute = $false; $si.CreateNoWindow = $true
    $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true
    $null = $si.Environment.Remove('CODEX_APP_SERVER_WS_URL')
    foreach ($argument in $Arguments) { $si.ArgumentList.Add($argument) }
    return $si
}
function Invoke-Bounded([string]$Image, [string[]]$Arguments, [int]$Seconds = 10) {
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = New-Child $Image $Arguments $PSScriptRoot
    try {
        if (-not $child.Start()) { throw 'child_start_failed' }
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        if (-not $child.WaitForExit($Seconds * 1000)) {
            $child.Kill(); $null = $child.WaitForExit(2000)
            throw 'child_timeout'
        }
        if (-not $stdout.Wait(2000)) { throw 'child_output_timeout' }
        return @{ Code = $child.ExitCode; Output = $stdout.GetAwaiter().GetResult() }
    } finally { $child.Dispose() }
}
function Read-Probe([string]$Mode, [string[]]$Extra = @()) {
    $result = Invoke-Bounded $ProbeImage (@($Mode, '--endpoint', $endpoint) + $Extra) 15
    $accepted = $null
    foreach ($line in ($result.Output -split '\r?\n')) {
        try {
            $item = $line | ConvertFrom-Json -AsHashtable -ErrorAction Stop
            if ($item.kind -in @('test_thread_created', 'loaded_threads_state')) { $accepted = $item }
            if ($item.kind -eq 'test_thread_created_but_unverified') {
                $manifest.unverifiedThreadId = $item.threadId
            }
            if ($item.kind -eq 'test_thread_policy_diagnostics') { $manifest.policyDiagnostics = $item }
            if ($item.kind -eq 'connection_or_preflight_failed') {
                $manifest.probeFailure = @{ reason = $item.reason; rpcCode = $item.rpcCode; rpcMethod = $item.rpcMethod }
            }
        } catch { }
    }
    if ($result.Code -ne 0) { throw 'probe_failed' }
    if ($accepted) { return $accepted }
    throw 'probe_result_missing'
}
function Process-Metadata([Diagnostics.Process]$Process) {
    $Process.Refresh()
    return @{ pid = $Process.Id; startedUtc = $Process.StartTime.ToUniversalTime().ToString('o'); image = $Process.MainModule.FileName }
}
function Same-Process([Diagnostics.Process]$Process, [System.Collections.IDictionary]$Identity) {
    try {
        if ($Process.HasExited) { return $false }
        $current = Process-Metadata $Process
        return $current.pid -eq $Identity.pid -and $current.startedUtc -ceq $Identity.startedUtc -and $current.image -ieq $Identity.image
    } catch { return $false }
}
function Read-Processes {
    return @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, ExecutablePath, CreationDate -OperationTimeoutSec 3)
}
function Get-DesktopRoots {
    return @(Read-Processes | Where-Object {
        $_.ExecutablePath -ieq $DesktopImage -or $_.Name -ieq [IO.Path]::GetFileName($DesktopImage)
    })
}
function Read-PortRows([int]$Port) {
    $netstat = Invoke-Bounded (Join-Path $env:SystemRoot 'System32\netstat.exe') @('-ano', '-p', 'tcp') 5
    if ($netstat.Code -ne 0) { throw 'tcp_snapshot_failed' }
    return @($netstat.Output -split '\r?\n' | ForEach-Object {
        $parts = $_.Trim() -split '\s+'
        if ($parts.Length -eq 5 -and $parts[0] -eq 'TCP' -and
            ($parts[1] -eq "127.0.0.1:$Port" -or $parts[2] -eq "127.0.0.1:$Port")) {
            [pscustomobject]@{ local = $parts[1]; remote = $parts[2]; state = $parts[3]; pid = [int]$parts[4] }
        }
    })
}
function Owns-Listener([object[]]$Rows) {
    $listeners = @($Rows | Where-Object { $_.local -eq "127.0.0.1:$port" -and $_.state -eq 'LISTENING' })
    return $listeners.Count -eq 1 -and $listeners[0].pid -eq $server.Id -and (Same-Process $server $manifest.server)
}

try {
    if (-not $IsWindows -or $PSVersionTable.PSEdition -ne 'Core') { throw 'powershell7_windows_required' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'normal_user_required' }
    if (-not ('SharedDesktopTrialNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SharedDesktopTrialNative {
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
}
'@
    }
    $inJob = $false
    if (-not [SharedDesktopTrialNative]::IsProcessInJob([Diagnostics.Process]::GetCurrentProcess().Handle, [IntPtr]::Zero, [ref]$inJob) -or $inJob) {
        throw 'outside_job_required'
    }
    foreach ($image in @($DesktopImage, $CodexImage, $ProbeImage)) {
        if (-not [IO.Path]::IsPathFullyQualified($image) -or -not [IO.File]::Exists($image)) { throw 'image_missing' }
    }
    $DesktopImage = [IO.Path]::GetFullPath($DesktopImage)
    $CodexImage = [IO.Path]::GetFullPath($CodexImage)
    $ProbeImage = [IO.Path]::GetFullPath($ProbeImage)
    $manifest.desktopImage = $DesktopImage; $manifest.codexImage = $CodexImage; $manifest.probeImage = $ProbeImage
    $ancestors = Read-Processes; $ancestorId = $PID
    for ($depth = 0; $ancestorId -ne 0 -and $depth -lt 64; $depth++) {
        $ancestor = $ancestors | Where-Object ProcessId -eq $ancestorId | Select-Object -First 1
        if (-not $ancestor) { throw 'ancestor_unverified' }
        if ($ancestor.ExecutablePath -ieq $DesktopImage -or $ancestor.Name -ieq [IO.Path]::GetFileName($DesktopImage)) { throw 'desktop_ancestor_rejected' }
        $ancestorId = [int]$ancestor.ParentProcessId
        if ($ancestorId -ne 0 -and -not ($ancestors | Where-Object ProcessId -eq $ancestorId)) { break }
    }
    $base = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\out\shared-desktop-trial'))
    $run = [IO.Path]::GetFullPath($RunDirectory).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($run) -ine $base -or [IO.Path]::GetFileName($run) -notmatch '^[A-Za-z0-9_-]{8,100}$') { throw 'run_directory_rejected' }
    if (Test-Path -LiteralPath $run) { throw 'run_already_exists' }
    $cursor = $run
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'reparse_path_rejected' }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    $manifest.originalDesktop = @{ pid = $WaitDesktopProcessId; startedUtc = $WaitDesktopStartUtc.UtcDateTime.ToString('o'); image = $DesktopImage }
    Save-State 'waiting_for_original_desktop_exit'
    Write-Host 'Waiting for the specified Desktop process to exit. This script will not close it.'
    $failureCode = 'original_desktop_wait_failed'; $wait = [Diagnostics.Stopwatch]::StartNew()
    $original = Get-Process -Id $WaitDesktopProcessId -ErrorAction SilentlyContinue
    if ($original) {
        if (-not (Same-Process $original $manifest.originalDesktop)) { throw 'original_identity_changed' }
        while (-not $original.WaitForExit(500)) {
            if ($wait.Elapsed.TotalSeconds -ge $MaxWaitSeconds) { throw 'original_exit_timeout' }
        }
        $original.Dispose()
    }
    if (@(Get-DesktopRoots).Count -ne 0) { throw 'another_desktop_present' }
    if (Test-Path -LiteralPath $run) { throw 'run_already_exists' }
    $null = [IO.Directory]::CreateDirectory($run)
    $claim = [IO.File]::Open((Join-Path $run 'runner.claim'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $manifestPath = Join-Path $run 'manifest.json'
    $workspace = Join-Path $run 'workspace'
    if (Test-Path -LiteralPath $workspace) { throw 'workspace_already_exists' }
    $null = [IO.Directory]::CreateDirectory($workspace)
    $manifest.workspace = $workspace
    $failureCode = 'trial_images_changed'
    $failureStep = 'images_after_desktop_exit'
    foreach ($image in @($DesktopImage, $CodexImage, $ProbeImage)) {
        if (-not [IO.File]::Exists($image)) { throw 'trial_images_changed' }
    }
    $failureCode = 'service_start_failed'
    $failureStep = 'service_start'
    $version = Invoke-Bounded $CodexImage @('--version') 5
    if ($version.Code -ne 0 -or $version.Output.Trim() -notmatch '^codex-cli [0-9A-Za-z.+_-]{1,80}$') { throw 'version_unverified' }
    $reservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $reservation.Start(); $port = ([Net.IPEndPoint]$reservation.LocalEndpoint).Port; $reservation.Stop()
    $endpoint = "ws://127.0.0.1:$port"
    $manifest.endpoint = $endpoint
    $server = [Diagnostics.Process]::new()
    $server.StartInfo = New-Child $CodexImage @('app-server', '--listen', $endpoint) $workspace
    if (-not $server.Start()) { throw 'service_start_failed' }
    $serverStarted = $true
    $drains.Add($server.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null))
    $drains.Add($server.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null))
    $manifest.server = Process-Metadata $server
    $manifest.server.version = $version.Output.Trim(); $manifest.server.port = $port
    $lifetime = [Diagnostics.Stopwatch]::StartNew()
    Save-State 'service_starting'
    $ready = $false
    while ($lifetime.Elapsed.TotalSeconds -lt 15 -and -not $server.HasExited) {
        if (Owns-Listener @(Read-PortRows $port)) { $ready = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'service_listener_unverified' }
    $failureCode = 'test_thread_creation_failed'
    $marker = 'CC_SHARED_' + [guid]::NewGuid().ToString('N')
    $created = Read-Probe 'create-test' @('--workspace', $workspace, '--marker', $marker)
    if ($created.kind -ne 'test_thread_created' -or $created.effectivePolicyVerified -ne $true -or $created.inputSubmitted -ne $false) { throw 'test_thread_unverified' }
    $manifest.threadId = $created.threadId; $manifest.marker = $marker; $manifest.effectivePolicyVerified = $true
    Save-State 'test_thread_created'
    if (@(Get-DesktopRoots).Count -ne 0) { throw 'another_desktop_present' }
    $failureCode = 'desktop_image_changed'
    $failureStep = 'desktop_image_recheck'
    if (-not [IO.File]::Exists($DesktopImage)) { throw 'desktop_image_changed' }
    $failureCode = 'desktop_launch_failed'
    $failureStep = 'desktop_start_info'
    $desktop = [Diagnostics.Process]::new()
    $desktop.StartInfo = New-Child $DesktopImage @() ([IO.Path]::GetDirectoryName($DesktopImage))
    $desktop.StartInfo.CreateNoWindow = $false
    $desktop.StartInfo.Environment['CODEX_APP_SERVER_WS_URL'] = $endpoint
    $failureStep = 'desktop_process_start'
    if (-not $desktop.Start()) { throw 'desktop_launch_failed' }
    $desktopStarted = $true
    $manifest.desktopStarted = $true
    $manifest.desktopLaunchPid = $desktop.Id
    $failureStep = 'desktop_output_drain'
    $drains.Add($desktop.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null))
    $drains.Add($desktop.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null))
    $failureStep = 'desktop_identity_read'
    # The image module can be temporarily unavailable immediately after CreateProcess.
    $identityDeadline = [DateTime]::UtcNow.AddSeconds(3)
    while ($true) {
        try {
            $identity = Process-Metadata $desktop
            if ($identity.image -ine $DesktopImage) { throw 'desktop_image_unconfirmed' }
            $manifest.desktop = $identity
            break
        } catch {
            if ($desktop.HasExited -or [DateTime]::UtcNow -ge $identityDeadline) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
    $failureStep = 'desktop_connection_check'
    Save-State 'desktop_connecting'
    $observation = [Diagnostics.Stopwatch]::StartNew(); $connected = $false
    while ($observation.Elapsed.TotalSeconds -lt $ObservationSeconds -and (Same-Process $desktop $manifest.desktop)) {
        $processes = Read-Processes; $ids = [Collections.Generic.HashSet[int]]::new(); $null = $ids.Add($desktop.Id)
        do {
            $changed = $false
            foreach ($p in $processes) {
                if ($ids.Contains([int]$p.ParentProcessId) -and $p.CreationDate.ToUniversalTime() -ge $desktop.StartTime.ToUniversalTime()) {
                    if ($ids.Add([int]$p.ProcessId)) { $changed = $true }
                }
            }
        } while ($changed)
        $rows = @(Read-PortRows $port)
        $connections = @($rows | Where-Object { $_.remote -eq "127.0.0.1:$port" -and $_.state -eq 'ESTABLISHED' -and $ids.Contains($_.pid) })
        if ((Owns-Listener $rows) -and $connections.Count -gt 0) {
            $manifest.desktopConnectionPids = @($connections.pid | Sort-Object -Unique); $connected = $true; break
        }
        Start-Sleep -Milliseconds 500
    }
    $manifest.connectionMetadataVerified = $connected
    Save-State $(if ($connected) { 'awaiting_manual_same_thread_check' } else { 'desktop_connection_uncertain' })
    Write-Host "Trial thread: $($manifest.threadId)"
    Write-Host "Trial name: $marker"
    Write-Host "Trial workspace: $workspace"
    Write-Host "Probe endpoint: $endpoint"
    Write-Host "Use the prepared observer command with this thread ID; no message is submitted automatically."
    Write-Host 'Use only this isolated test thread. This connection check does not prove Desktop message synchronization.'
    Write-Host 'End the test Turn, exit the trial Desktop normally, then use Complete-SharedDesktopTrial.ps1 to clean up and return to native mode.'
    while ($connected -and $lifetime.Elapsed.TotalSeconds -lt $MaxLifetimeSeconds -and -not $server.HasExited -and -not $desktop.HasExited) {
        Start-Sleep -Milliseconds 500
    }
} catch {
    $manifest.failureCode = $failureCode
    $manifest.failureDetails = Safe-Failure $_
    Save-State 'failed'
    Write-Host "Trial failed: $failureCode"
    $manifest.failureDetails | ConvertTo-Json -Depth 6 -Compress
} finally {
    if ($serverStarted -and -not $server.HasExited) {
        try {
            if (($desktopStarted -and -not $desktop.HasExited) -or @(Get-DesktopRoots).Count -ne 0) { throw 'desktop_still_running' }
            $idle = Read-Probe 'idle-check'
            $cleanupRows = @(Read-PortRows $port)
            if ($idle.kind -ne 'loaded_threads_state' -or $idle.allIdle -ne $true -or -not (Same-Process $server $manifest.server) -or -not (Owns-Listener $cleanupRows)) { throw 'cleanup_blocked' }
            if (@($cleanupRows | Where-Object state -eq 'ESTABLISHED').Count -ne 0) { throw 'client_still_connected' }
            if (@(Get-DesktopRoots).Count -ne 0) { throw 'desktop_reappeared' }
            $manifest.cleanup = @{ allIdle = $true; loadedCount = $idle.loadedCount }
            $server.Kill(); $null = $server.WaitForExit(5000)
            $remaining = @(Read-PortRows $port | Where-Object state -eq 'LISTENING')
            Save-State $(if ($remaining.Count -eq 0 -and $server.HasExited) { 'service_stopped' } else { 'cleanup_incomplete' })
        } catch {
            $manifest.cleanupFailureDetails = Safe-Failure $_
            Save-State 'cleanup_blocked'
            Write-Host 'Service remains running: Desktop, activity, or identity could not be safely excluded. Close trial work and Desktop normally, then use the prepared restore procedure.'
        }
    }
    if ($claim) { $claim.Dispose() }
    if ($manifestPath) { Write-Host "Trial metadata: $manifestPath" }
}
