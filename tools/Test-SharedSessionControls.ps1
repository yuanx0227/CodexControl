#requires -Version 7.0
<# Explicitly authorized real protocol test. New isolated thread only; no Desktop/service lifecycle changes. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$probeProcess = $null
$writer = $null
$resultPath = $null
$result = [ordered]@{kind='automated_control_check';passed=$false;steerAccepted=$false;interruptAccepted=$false;streamingObserved=$false;terminalStatus=$null;idleConfirmed=$false;desktopUiVerified=$false;semanticSteerVerified=$false}
try {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $run = [IO.Path]::GetFullPath($RunDirectory)
    if ([IO.Path]::GetDirectoryName($run) -ine (Join-Path $repo 'out/shared-desktop-trial')) { throw 'INVALID_RUN_DIRECTORY' }
    for ($directory=[IO.DirectoryInfo]::new($run); $null -ne $directory; $directory=$directory.Parent) {
        if (-not $directory.Exists -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'INVALID_RUN_DIRECTORY' }
    }
    $m = Get-Content -LiteralPath (Join-Path $run 'manifest.json') -Raw | ConvertFrom-Json
    $server = Get-Process -Id $m.server.pid
    if ($server.Path -ine $m.codexImage -or $server.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$m.server.startedUtc).UtcTicks) { throw 'SERVICE_IDENTITY_CHANGED' }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $m.server.port -ErrorAction Stop)
    if ($listeners.Count -ne 1 -or $listeners[0].OwningProcess -ne $server.Id -or $listeners[0].LocalAddress -ne '127.0.0.1' -or $m.endpoint -cne "ws://127.0.0.1:$($m.server.port)") { throw 'LISTENER_CHANGED' }
    $result.serverPid = $server.Id
    $result.serverStartedUtc = $server.StartTime.ToUniversalTime().ToString('o')
    $suffix = [guid]::NewGuid().ToString('N')
    $case = Join-Path $run ('controls-' + $suffix)
    $workspace = Join-Path $case 'workspace'
    $null = [IO.Directory]::CreateDirectory($workspace)
    $resultPath = Join-Path $case 'result.json'
    $writer = [IO.StreamWriter]::new((Join-Path $case 'events.jsonl'), $false, [Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $marker = 'CC_SHARED_' + $suffix
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $repo 'out/shared-session-probe-auto-controls/CodexControlSharedSessionProbe.exe'))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach($argument in @('create-observe','--endpoint',$m.endpoint,'--workspace',$workspace,'--marker',$marker)) { $start.ArgumentList.Add($argument) }
    $probeProcess = [Diagnostics.Process]::Start($start)
    $discard = $probeProcess.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
    $stage = 'ready'
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    $read = $probeProcess.StandardOutput.ReadLineAsync()
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not $read.Wait(250)) { continue }
        $line = $read.GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'PROBE_EXITED_EARLY' }
        $event = $line | ConvertFrom-Json
        # Probe output is already metadata-only: never capture server stdout or message bodies.
        $writer.WriteLine($line)
        if ($event.kind -eq 'test_thread_created') {
            $result.threadId=$event.threadId; $result.marker=$marker
            [pscustomobject]@{kind='control_test_started';threadId=$event.threadId;marker=$marker} | ConvertTo-Json -Compress
        }
        if ($event.kind -in @('connection_or_preflight_failed','command_rejected_or_unconfirmed','connection_lost')) {
            $result.probeFailure = $event
            throw 'PROBE_REJECTED_OR_DISCONNECTED'
        }
        if ($stage -eq 'ready' -and $event.kind -eq 'authoritative_state') {
            if ($event.controlAllowed -ne $true -or $event.status -ne 'idle') { throw 'STRICT_IDLE_THREAD_REQUIRED' }
            $probeProcess.StandardInput.WriteLine('start 不调用任何工具，不访问文件或网络。请连续写一篇约4000字的中文科普文章，主题是四季变化，直接开始正文。')
            $stage='streaming'
        }
        if ($event.kind -eq 'event' -and $event.method -eq 'turn/started') { $result.turnId=$event.turnId }
        if ($stage -eq 'streaming' -and $event.kind -eq 'event' -and $event.method -eq 'item/agentMessage/delta' -and $event.textCharacters -gt 0) {
            $result.streamingObserved=$true
            $probeProcess.StandardInput.WriteLine('steer 不调用任何工具。将主题改为海洋潮汐，用STEER_OK开头并继续详细解释约4000字。')
            $stage='steer'
        }
        if ($stage -eq 'steer' -and $event.kind -eq 'rpc_accepted' -and $event.command -eq 'steer') {
            $result.steerAccepted=$true
            $result.steerTargetTurn=$event.targetTurnId
            $result.steerReturnedTurn=$event.returnedTurnId
            if ($event.targetTurnId -ne $result.turnId -or $event.returnedTurnId -ne $result.turnId) { throw 'STEER_TURN_MISMATCH' }
            $probeProcess.StandardInput.WriteLine('interrupt')
            $stage='interrupt'
        }
        if ($event.kind -eq 'rpc_accepted' -and $event.command -eq 'interrupt') {
            $result.interruptAccepted=$true; $result.interruptTargetTurn=$event.targetTurnId
            if ($event.targetTurnId -ne $result.turnId) { throw 'INTERRUPT_TURN_MISMATCH' }
        }
        if ($event.kind -eq 'event' -and $event.method -eq 'turn/completed' -and $event.turnId -eq $result.turnId) {
            $result.terminalStatus=$event.terminalTurnStatus
        }
        if ($stage -eq 'interrupt' -and $result.interruptAccepted -and $null -ne $result.terminalStatus) {
            $probeProcess.StandardInput.WriteLine('status'); $stage='final_state'
        } elseif ($stage -eq 'final_state' -and $event.kind -eq 'authoritative_state') {
            $result.idleConfirmed=$event.status -eq 'idle' -and $null -eq $event.turnId
            $result.passed=$result.steerAccepted -and $result.interruptAccepted -and $result.terminalStatus -eq 'interrupted' -and $result.idleConfirmed
            break
        }
        $read=$probeProcess.StandardOutput.ReadLineAsync()
    }
    if (-not $result.passed) { throw 'CONTROL_CHECK_NOT_CONFIRMED' }
} catch {
    $known=@('INVALID_RUN_DIRECTORY','SERVICE_IDENTITY_CHANGED','LISTENER_CHANGED','PROBE_EXITED_EARLY','PROBE_REJECTED_OR_DISCONNECTED','STRICT_IDLE_THREAD_REQUIRED','STEER_TURN_MISMATCH','INTERRUPT_TURN_MISMATCH','CONTROL_CHECK_NOT_CONFIRMED')
    $result.failure=if($_.Exception.Message -in $known){$_.Exception.Message}else{'CONTROL_CHECK_FAILED'}
} finally {
    if ($probeProcess -and -not $probeProcess.HasExited) {
        # Stop only this test on incomplete control checks; no approvals and no server process termination.
        if (-not $result.passed -and $result.Contains('turnId') -and $null -eq $result.terminalStatus) {
            $probeProcess.StandardInput.WriteLine('interrupt')
            $result.cleanupInterruptRequested=$true
        }
        $probeProcess.StandardInput.WriteLine('quit')
        $probeProcess.StandardInput.Close()
        if (-not $probeProcess.WaitForExit(20000)) { $result.probeExitUnconfirmed=$true }
    }
    if ($writer) { $writer.Dispose() }
    if ($resultPath) { $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding utf8 }
    $result | ConvertTo-Json -Depth 8 -Compress
}
