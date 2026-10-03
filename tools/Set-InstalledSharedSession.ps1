#requires -Version 7.0
<#
Inspect is read-only. Apply/Restore require Desktop and the installed Agent to
have been exited normally by the user. Only three Agent connection fields are
changed; rollback stores those fields, never authentication or Relay settings.
#>
[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Apply', 'Restore')][string]$Action = 'Inspect',
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$DesktopImage,
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$StatePath,
    [string]$AgentImage = (Join-Path $env:LOCALAPPDATA 'Programs/CodexControl/CodexControlAgent.exe')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$selectedFields = @('sharedEndpoint', 'sharedManifestPath', 'sharedProjectRoots')
$settingsPath = Join-Path (Split-Path -Parent $AgentImage) 'data/settings.json'

function Read-Json([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 131072) { throw 'JSON metadata exceeds the allowed size.' }
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -AsHashtable
}

function Write-JsonAtomically([string]$Path, [object]$Value) {
    $temporary = $Path + '.tmp-' + [guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

function Assert-SafeDirectory([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\')) { throw 'Workspace must be an existing local absolute directory.' }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    if ($full -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetPathRoot($full))) { throw 'A whole volume cannot be authorized.' }
    for ($directory = [IO.DirectoryInfo]::new($full); $null -ne $directory; $directory = $directory.Parent) {
        if (-not $directory.Exists -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Workspace or an ancestor is missing or is a reparse point.'
        }
    }
    return $full
}

function Get-OfficialDesktopProcesses {
    $result = @()
    foreach ($process in Get-Process -Name ChatGPT -ErrorAction SilentlyContinue) {
        try {
            if ($process.MainModule.FileName -like '*\WindowsApps\OpenAI.Codex_*\app\ChatGPT.exe') { $result += $process }
        }
        catch { throw 'Cannot identify a running Desktop process; exit Desktop normally before switching.' }
    }
    return $result
}

function Get-InstalledAgentProcesses {
    $result = @()
    foreach ($process in Get-Process -Name CodexControlAgent -ErrorAction SilentlyContinue) {
        try { if ($process.MainModule.FileName -ieq $AgentImage) { $result += $process } }
        catch { throw 'Cannot identify a running Agent; exit the installed Agent normally before switching.' }
    }
    return $result
}

function Assert-ClientsExited {
    if (@(Get-OfficialDesktopProcesses).Count -gt 0 -or @(Get-InstalledAgentProcesses).Count -gt 0) {
        throw 'Desktop or the installed Agent is still running. Finish active work and exit both applications, including their tray processes. No process was stopped.'
    }
}

function Get-ServiceProof {
    $manifest = Read-Json $ManifestPath
    $uri = [uri]$manifest.endpoint
    if ($uri.Scheme -ne 'ws' -or $uri.Host -ne '127.0.0.1' -or $uri.Port -lt 1 -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') {
        throw 'Manifest must describe a credential-free ws://127.0.0.1:<port> endpoint.'
    }
    if ($manifest.Contains('server')) {
        $service = $manifest.server
        $servicePid = [int]$service.pid
        $started = [datetimeoffset]$service.startedUtc
        $image = [string]$service.image
        $version = [string]$service.version
    }
    else {
        $servicePid = [int]$manifest.processId
        $started = [datetimeoffset]$manifest.startedUtc
        $image = [string]$manifest.imagePath
        $version = [string]$manifest.version
    }
    $process = Get-Process -Id $servicePid
    if ($process.StartTime.ToUniversalTime().Ticks -ne $started.UtcTicks -or $process.MainModule.FileName -ine $image) {
        throw 'The service PID, creation time or executable no longer matches the manifest.'
    }
    $listeners = @(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $uri.Port -State Listen -ErrorAction SilentlyContinue)
    if (-not ($listeners | Where-Object OwningProcess -eq $servicePid)) { throw 'The known loopback listener does not belong to the recorded service.' }
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = [Diagnostics.ProcessStartInfo]::new($image)
    $child.StartInfo.UseShellExecute = $false
    $child.StartInfo.CreateNoWindow = $true
    $child.StartInfo.RedirectStandardOutput = $true
    $child.StartInfo.RedirectStandardError = $true
    $child.StartInfo.ArgumentList.Add('--version')
    $versionStarted = $false
    try {
        if (-not $child.Start()) { throw 'Version inspection could not start.' }
        $versionStarted = $true
        $output = $child.StandardOutput.ReadToEndAsync()
        $discard = $child.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        if (-not $child.WaitForExit(10000)) { throw 'Version inspection timed out.' }
        $actual = $output.GetAwaiter().GetResult().Trim()
        $null = $discard.GetAwaiter().GetResult()
        if ($child.ExitCode -ne 0 -or $actual -notmatch '^codex-cli [0-9][^\r\n]{0,100}$' -or $actual -ne $version) {
            throw 'The actual executable version differs from the recorded version.'
        }
    }
    finally { if ($versionStarted -and -not $child.HasExited) { $child.Kill() }; $child.Dispose() }
    return [pscustomobject]@{ Endpoint = $uri.GetLeftPart([UriPartial]::Authority); Pid = $servicePid; CreatedUtc = $started.ToString('o'); Version = $version }
}

function Start-AgentAndDesktop([bool]$Shared) {
    $desktopStart = [Diagnostics.ProcessStartInfo]::new($(if ($Shared) { $AgentImage } else { $DesktopImage }))
    $desktopStart.UseShellExecute = $false
    $desktopStart.CreateNoWindow = $true
    $desktopStart.WorkingDirectory = Split-Path -Parent $AgentImage
    $null = $desktopStart.Environment.Remove('CODEX_APP_SERVER_WS_URL')
    if ($Shared) {
        foreach ($argument in @('--shared-desktop', '--desktop-path', $DesktopImage, '--shared-endpoint', $proof.Endpoint,
            '--shared-manifest', [IO.Path]::GetFullPath($ManifestPath))) { $desktopStart.ArgumentList.Add($argument) }
    }
    $launcher = [Diagnostics.Process]::Start($desktopStart)
    if ($Shared -and (-not $launcher.WaitForExit(20000) -or $launcher.ExitCode -ne 0)) {
        throw 'Shared Desktop launch was not confirmed. Settings remain recoverable with Restore; no service was stopped.'
    }
    $agentStart = [Diagnostics.ProcessStartInfo]::new($AgentImage)
    $agentStart.UseShellExecute = $false
    $agentStart.CreateNoWindow = $true
    $null = $agentStart.Environment.Remove('CODEX_APP_SERVER_WS_URL')
    $null = [Diagnostics.Process]::Start($agentStart)
    Write-Output $(if ($Shared) { 'Shared settings applied and both launch requests completed. Verify same-service connections and a new test Thread; this is not acceptance proof.' }
        else { 'Previous Agent connection fields restored; native Desktop and Agent launch requests completed. Shared service remains running.' })
}

if (-not [IO.Path]::IsPathFullyQualified($AgentImage) -or -not (Test-Path -LiteralPath $AgentImage -PathType Leaf)) { throw 'Installed Agent image is unavailable.' }
if (-not [IO.Path]::IsPathFullyQualified($DesktopImage) -or
    $DesktopImage -notlike '*\WindowsApps\OpenAI.Codex_*\app\ChatGPT.exe' -or -not (Test-Path -LiteralPath $DesktopImage -PathType Leaf)) { throw 'Official Desktop image is unavailable.' }
$workspacePath = Assert-SafeDirectory $Workspace
$settings = Read-Json $settingsPath
if ($settings.schemaVersion -ne 1) { throw 'Unsupported Agent settings schema.' }

if ($Action -eq 'Restore') {
    Assert-ClientsExited
    $state = Read-Json $StatePath
    if ($state.settingsPath -ine [IO.Path]::GetFullPath($settingsPath)) { throw 'Rollback does not belong to this installed Agent.' }
    foreach ($name in $selectedFields) {
        if ((ConvertTo-Json -InputObject $settings[$name] -Compress -Depth 8) -cne
            (ConvertTo-Json -InputObject $state.applied[$name] -Compress -Depth 8)) { throw 'Shared settings changed after the switch; refusing to overwrite subsequent edits.' }
    }
    foreach ($name in $selectedFields) {
        if ($state.before[$name].present) { $settings[$name] = $state.before[$name].value }
        else { $null = $settings.Remove($name) }
    }
    Write-JsonAtomically $settingsPath $settings
    Start-AgentAndDesktop $false
    exit
}

$proof = Get-ServiceProof
if ($Action -eq 'Inspect') {
    [pscustomobject]@{
        Action = 'Inspect'; SettingsWillChange = $false; ClientOrServiceProcessesWillStart = $false
        AgentConfiguredForShared = $settings.Contains('sharedEndpoint') -and [bool]$settings.sharedEndpoint
        DesktopProcesses = @(Get-OfficialDesktopProcesses).Count
        InstalledAgentProcesses = @(Get-InstalledAgentProcesses).Count
        Service = $proof; ProposedWorkspace = $workspacePath; RollbackPath = [IO.Path]::GetFullPath($StatePath)
    } | ConvertTo-Json -Depth 4
    exit
}

Assert-ClientsExited
if (Test-Path -LiteralPath $StatePath) { throw 'A rollback record already exists. Restore or use a new explicit record path.' }
if ($settings.Contains('sharedEndpoint') -and $settings['sharedEndpoint']) { throw 'This migration is for an Agent still in independent mode; shared settings already exist.' }
if (-not $settings.startCoreAutomatically) { throw 'Agent automatic core startup is disabled; no setting was changed.' }
$before = [ordered]@{}
foreach ($name in $selectedFields) { $before[$name] = @{ present = $settings.Contains($name); value = $settings[$name] } }
$roots = @($settings['sharedProjectRoots'] | Where-Object { $_ })
if ($roots -notcontains $workspacePath) { $roots += $workspacePath }
foreach ($root in $roots) { $null = Assert-SafeDirectory $root }
$applied = [ordered]@{ sharedEndpoint = $proof.Endpoint; sharedManifestPath = [IO.Path]::GetFullPath($ManifestPath); sharedProjectRoots = $roots }
$record = @{ schemaVersion = 1; settingsPath = [IO.Path]::GetFullPath($settingsPath); before = $before; applied = $applied; service = $proof }
# Only selected connection fields enter the rollback record, never the complete settings file.
Write-JsonAtomically $StatePath $record
foreach ($name in $selectedFields) { $settings[$name] = $applied[$name] }
Write-JsonAtomically $settingsPath $settings
Start-AgentAndDesktop $true
