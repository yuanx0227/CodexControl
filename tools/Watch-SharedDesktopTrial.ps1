# Run in a separate, ordinary PowerShell 7 console. Input is explicitly entered by the user.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDirectory, [switch]$NewSubscribedTest)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try {
    if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) { throw 'POWERSHELL_7_REQUIRED' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'NON_ELEVATED_TERMINAL_REQUIRED' }
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $runsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'out/shared-desktop-trial'))
    $run = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RunDirectory))
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($run), $runsRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($run) -notmatch '^[A-Za-z0-9_-]+$') { throw 'INVALID_TRIAL_DIRECTORY' }
    for ($directory = [IO.DirectoryInfo]::new($run); $null -ne $directory; $directory = $directory.Parent) {
        if (-not $directory.Exists -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'INVALID_TRIAL_DIRECTORY' }
    }
    $manifestFile = Get-Item -LiteralPath (Join-Path $run 'manifest.json') -Force
    if ($manifestFile.PSIsContainer -or ($manifestFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'INVALID_TRIAL_DIRECTORY' }
    $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.effectivePolicyVerified -ne $true -or
        $manifest.threadId -notmatch '^[a-fA-F0-9-]{36}$' -or $manifest.marker -notmatch '^CC_SHARED_[A-Za-z0-9_-]{8,64}$') { throw 'TRIAL_NOT_READY' }
    $workspace = [IO.Path]::GetFullPath((Join-Path $run 'workspace'))
    if (-not [IO.Path]::IsPathFullyQualified([string]$manifest.workspace) -or
        -not [string]::Equals([IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([string]$manifest.workspace)),
            $workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'INVALID_TRIAL_WORKSPACE' }
    $workspaceDirectory = [IO.DirectoryInfo]::new($workspace)
    if (-not $workspaceDirectory.Exists -or ($workspaceDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'INVALID_TRIAL_WORKSPACE' }
    $port = [int]$manifest.server.port
    if ($port -lt 1 -or $port -gt 65535 -or $manifest.endpoint -cne "ws://127.0.0.1:$port") { throw 'INVALID_TRIAL_ENDPOINT' }
    $server = Get-Process -Id ([int]$manifest.server.pid) -ErrorAction Stop
    try {
        # ConvertFrom-Json may return DateTime. Casting it to string loses subsecond precision.
        $expectedStart = [DateTimeOffset]$manifest.server.startedUtc
        if ($server.StartTime.ToUniversalTime().Ticks -ne $expectedStart.UtcDateTime.Ticks -or
            -not [string]::Equals($server.Path, [string]$manifest.codexImage, [StringComparison]::OrdinalIgnoreCase)) { throw 'SERVICE_IDENTITY_CHANGED' }
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction Stop)
        if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or $listeners[0].OwningProcess -ne $server.Id) { throw 'SERVICE_LISTENER_CHANGED' }
    } finally { $server.Dispose() }
    if ($NewSubscribedTest) {
        $probe = Join-Path $repoRoot 'out/shared-session-probe-policy-diagnostics/CodexControlSharedSessionProbe.exe'
        if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'OBSERVER_BUILD_REQUIRED' }
        $suffix = [guid]::NewGuid().ToString('N')
        $workspace = Join-Path $run ('subscribed-workspace-' + $suffix)
        if (Test-Path -LiteralPath $workspace) { throw 'INVALID_TRIAL_WORKSPACE' }
        $null = [IO.Directory]::CreateDirectory($workspace)
        $marker = 'CC_SHARED_' + $suffix
        Write-Host "A new isolated test will be created. Keep this window open, then open the Desktop thread named $marker."
        Write-Host 'No input is submitted automatically. After Desktop opens it, enter status; input requires controlAllowed:true.'
        Write-Host 'Commands: help; status; start TEXT; steer TEXT; interrupt; reconnect; quit.'
        & $probe create-observe --endpoint $manifest.endpoint --workspace $workspace --marker $marker
        exit $LASTEXITCODE
    }
    $probe = Join-Path $repoRoot 'tools/CodexControl.SharedSessionProbe/bin/Debug/net8.0/CodexControlSharedSessionProbe.exe'
    Write-Host "Open the Desktop test named $($manifest.marker); confirm Thread $($manifest.threadId)."
    Write-Host 'Commands: status; start TEXT; steer TEXT; interrupt; decline ID; cancel ID; reconnect; quit.'
    Write-Host 'First use fixed-marker chat without tools. No input or approval is sent automatically.'
    & $probe observe --endpoint $manifest.endpoint --thread $manifest.threadId --workspace $workspace --marker $manifest.marker
    exit $LASTEXITCODE
} catch {
    $known = @('POWERSHELL_7_REQUIRED','NON_ELEVATED_TERMINAL_REQUIRED','INVALID_TRIAL_DIRECTORY','INVALID_TRIAL_WORKSPACE','TRIAL_NOT_READY','INVALID_TRIAL_ENDPOINT','SERVICE_IDENTITY_CHANGED','SERVICE_LISTENER_CHANGED','OBSERVER_BUILD_REQUIRED')
    $reason = if ($_.Exception.Message -in $known) { $_.Exception.Message } else { 'OBSERVER_START_FAILED' }
    [pscustomobject]@{kind='observer_not_started';reason=$reason;inputSubmitted=$false} | ConvertTo-Json -Compress
    exit 2
}
