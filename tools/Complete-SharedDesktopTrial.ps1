# Run from Explorer or an independent PowerShell 7 terminal after ending the test Turn.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [ValidateRange(1, 900)][int]$WaitSeconds = 900
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$cleanupClaim = $null

function Get-DesktopProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='ChatGPT.exe' OR Name='Codex.exe'" -Property Name,ProcessId,ExecutablePath -OperationTimeoutSec 3 |
        Where-Object { $_.ExecutablePath -match '\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(ChatGPT|Codex)\.exe$' })
}

function Get-TrialTcpRows([int]$Port) {
    # An empty filtered result is safe only after the underlying query succeeded.
    @(Get-NetTCPConnection -ErrorAction Stop | Where-Object { $_.LocalPort -eq $Port -or $_.RemotePort -eq $Port })
}

try {
    if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) { throw 'POWERSHELL_7_REQUIRED' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'NON_ELEVATED_TERMINAL_REQUIRED' }
    $ancestors = @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name)
    $ancestorId = $PID
    $visited = [Collections.Generic.HashSet[uint32]]::new()
    while ($ancestorId -and $visited.Add([uint32]$ancestorId)) {
        $ancestor = $ancestors | Where-Object ProcessId -eq $ancestorId | Select-Object -First 1
        if (-not $ancestor) { break }
        if ($ancestor.Name -in @('ChatGPT.exe', 'Codex.exe')) { throw 'EXTERNAL_TERMINAL_REQUIRED' }
        $ancestorId = $ancestor.ParentProcessId
    }

    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $runsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'out/shared-desktop-trial'))
    $runPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RunDirectory))
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($runPath), $runsRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($runPath) -notmatch '^[A-Za-z0-9_-]+$') { throw 'INVALID_RUN_DIRECTORY' }
    for ($directory = [IO.DirectoryInfo]::new($runPath); $null -ne $directory; $directory = $directory.Parent) {
        if (-not $directory.Exists -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'UNSAFE_RUN_DIRECTORY' }
    }
    $manifestFile = Get-Item -LiteralPath (Join-Path $runPath 'manifest.json') -Force
    if ($manifestFile.PSIsContainer -or ($manifestFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'UNSAFE_RUN_DIRECTORY' }
    $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) { throw 'UNSUPPORTED_MANIFEST' }
    $desktopImage = [IO.Path]::GetFullPath([string]$manifest.desktopImage)
    $codexImage = [IO.Path]::GetFullPath([string]$manifest.codexImage)
    $probeImage = [IO.Path]::GetFullPath((Join-Path $repoRoot 'tools/CodexControl.SharedSessionProbe/bin/Debug/net8.0/CodexControlSharedSessionProbe.exe'))
    if ($desktopImage -notmatch '\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(ChatGPT|Codex)\.exe$' -or
        -not (Test-Path -LiteralPath $desktopImage -PathType Leaf) -or
        -not (Test-Path -LiteralPath $probeImage -PathType Leaf)) { throw 'TRIAL_IMAGES_UNAVAILABLE' }

    Write-Host 'End the test Turn and exit Desktop normally. This script will not close it for you.'
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    while (@(Get-DesktopProcesses).Count -gt 0) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'DESKTOP_STILL_RUNNING' }
        Start-Sleep -Seconds 1
    }

    # The runner keeps this handle throughout its own cleanup. Serialize the two owners.
    $claimFile = Get-Item -LiteralPath (Join-Path $runPath 'runner.claim') -Force
    if ($claimFile.PSIsContainer -or ($claimFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'UNSAFE_RUN_DIRECTORY' }
    $claimDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while ($null -eq $cleanupClaim) {
        try { $cleanupClaim = [IO.File]::Open($claimFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $claimDeadline) { throw 'RUNNER_CLEANUP_IN_PROGRESS' }
            Start-Sleep -Milliseconds 500
        }
    }
    $latestManifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    if ($latestManifest.schemaVersion -ne 1 -or $latestManifest.desktopImage -ine $manifest.desktopImage -or
        $latestManifest.codexImage -ine $manifest.codexImage) { throw 'UNSUPPORTED_MANIFEST' }
    $manifest = $latestManifest

    if ($manifest.PSObject.Properties['server'] -and $null -ne $manifest.server) {
        $port = [int]$manifest.server.port
        if ($port -lt 1 -or $port -gt 65535 -or $manifest.endpoint -cne "ws://127.0.0.1:$port") { throw 'INVALID_TRIAL_ENDPOINT' }
        $server = Get-Process -Id ([int]$manifest.server.pid) -ErrorAction SilentlyContinue
        if ($server) {
            try {
                $null = $server.Handle
                # Preserve DateTime ticks when ConvertFrom-Json has already parsed this value.
                $expectedStart = ([DateTimeOffset]$manifest.server.startedUtc).UtcDateTime
                if ($server.StartTime.ToUniversalTime().Ticks -ne $expectedStart.Ticks -or
                    -not [string]::Equals($server.Path, $codexImage, [StringComparison]::OrdinalIgnoreCase)) { throw 'SERVICE_IDENTITY_CHANGED' }
                $listeners = @(Get-TrialTcpRows $port | Where-Object State -eq 'Listen')
                if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or
                    $listeners[0].OwningProcess -ne $server.Id) { throw 'SERVICE_LISTENER_CHANGED' }
                $checkLines = @(& $probeImage idle-check --endpoint $manifest.endpoint)
                $checkExit = $LASTEXITCODE
                $check = @($checkLines | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object kind -eq 'loaded_threads_state')
                if ($checkExit -ne 0 -or $check.Count -ne 1 -or $check[0].allIdle -ne $true) { throw 'CLEANUP_BLOCKED_ACTIVE_OR_UNKNOWN' }
                if (@(Get-DesktopProcesses).Count -gt 0) { throw 'CLEANUP_BLOCKED_DESKTOP_REOPENED' }
                $cleanupRows = @(Get-TrialTcpRows $port)
                $listeners = @($cleanupRows | Where-Object State -eq 'Listen')
                if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or
                    $listeners[0].OwningProcess -ne $server.Id) { throw 'SERVICE_LISTENER_CHANGED' }
                $connections = @($cleanupRows | Where-Object State -eq 'Established')
                if ($connections.Count -gt 0) { throw 'CLEANUP_BLOCKED_OTHER_CLIENTS' }
                if (@(Get-DesktopProcesses).Count -gt 0) { throw 'CLEANUP_BLOCKED_DESKTOP_REOPENED' }
                # The retained handle is the exact trial root process. Never kill a process tree.
                if (-not $server.HasExited) { $server.Kill(); $null = $server.WaitForExit(5000) }
            } finally { $server.Dispose() }
        }
        if (@(Get-TrialTcpRows $port | Where-Object State -eq 'Listen').Count -gt 0) {
            throw 'CLEANUP_INCOMPLETE_LISTENER_REMAINS'
        }
    }

    if (@(Get-DesktopProcesses).Count -gt 0) { throw 'CLEANUP_BLOCKED_DESKTOP_REOPENED' }
    $resultPath = Join-Path $runPath 'restore-result.json'
    if (Test-Path -LiteralPath $resultPath) {
        $resultFile = Get-Item -LiteralPath $resultPath -Force
        if ($resultFile.PSIsContainer -or ($resultFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'UNSAFE_RUN_DIRECTORY' }
    }
    $start = [Diagnostics.ProcessStartInfo]::new($desktopImage)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Environment.Remove('CODEX_APP_SERVER_WS_URL') | Out-Null
    $native = [Diagnostics.Process]::Start($start)
    $result = [ordered]@{ kind='native_desktop_launch_requested'; desktopPid=$native.Id; trialServiceClosed=$true; globalEnvironmentChanged=$false; atUtc=[DateTime]::UtcNow.ToString('o') }
    $native.Dispose()
    $result | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    $result | ConvertTo-Json -Compress
} catch {
    $allowed = @('POWERSHELL_7_REQUIRED','NON_ELEVATED_TERMINAL_REQUIRED','EXTERNAL_TERMINAL_REQUIRED','INVALID_RUN_DIRECTORY',
        'UNSAFE_RUN_DIRECTORY','UNSUPPORTED_MANIFEST','TRIAL_IMAGES_UNAVAILABLE','DESKTOP_STILL_RUNNING','INVALID_TRIAL_ENDPOINT',
        'SERVICE_IDENTITY_CHANGED','SERVICE_LISTENER_CHANGED','CLEANUP_BLOCKED_ACTIVE_OR_UNKNOWN','CLEANUP_BLOCKED_DESKTOP_REOPENED','CLEANUP_BLOCKED_OTHER_CLIENTS',
        'CLEANUP_INCOMPLETE_LISTENER_REMAINS','RUNNER_CLEANUP_IN_PROGRESS')
    $reason = if ($_.Exception.Message -in $allowed) { $_.Exception.Message } else { 'RESTORE_FAILED_UNCONFIRMED' }
    [pscustomobject]@{kind='native_restore_not_completed';reason=$reason;automaticForceStop=$false} | ConvertTo-Json -Compress
    exit 2
} finally {
    if ($cleanupClaim) { $cleanupClaim.Dispose() }
}
