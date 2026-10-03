#requires -Version 7.0
<# Real, explicitly authorized protocol checks. Only newly created isolated test threads.
No Desktop/service lifecycle operations, no automatic approval, no raw transcript persistence. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDirectory, [switch]$OnlyApproval)
$ErrorActionPreference='Stop'
$actors=[Collections.Generic.List[object]]::new()
$report=[ordered]@{kind='shared_lifecycle_checks';desktopExitTested=$false;desktopUiVerified=$false;automaticApproval=$false;results=[ordered]@{}}
$writer=$null;$rpc=$null;$resultPath=$null
function Emit($value) { $line=$value | ConvertTo-Json -Depth 8 -Compress; if($writer){$writer.WriteLine($line)}; Write-Output $line }
function Pump {
    foreach($actor in $actors) {
        $batch=0
        while($null -ne $actor.read -and $actor.read.IsCompleted -and $batch++ -lt 64) {
            $line=$actor.read.GetAwaiter().GetResult()
            if($null -eq $line){$actor.read=$null;break}
            $entry=$line | ConvertFrom-Json
            $actor.events.Add($entry)
            if($writer){$writer.WriteLine((@{actor=$actor.name;payload=$entry} | ConvertTo-Json -Depth 8 -Compress))}
            $actor.read=$actor.process.StandardOutput.ReadLineAsync()
        }
    }
}
function Wait-Event($actor,[int]$after,[scriptblock]$predicate,[int]$seconds=120) {
    $deadline=[DateTime]::UtcNow.AddSeconds($seconds)
    $index=$after
    while([DateTime]::UtcNow -lt $deadline) {
        Pump
        while($index -lt $actor.events.Count){$entry=$actor.events[$index++]; if(& $predicate $entry){return $entry}}
        if($actor.process.HasExited -and $null -eq $actor.read){throw 'ACTOR_EXITED'}
        Start-Sleep -Milliseconds 100
    }
    throw 'EVENT_TIMEOUT'
}
function Send($actor,[string]$command) {
    if($actor.process.HasExited){throw 'ACTOR_EXITED'}
    $actor.process.StandardInput.WriteLine($command)
    $actor.process.StandardInput.Flush()
}
function New-Actor([string]$name,[string]$workspace,[string]$marker,[string]$threadId='') {
    $si=[Diagnostics.ProcessStartInfo]::new($probeImage)
    $si.UseShellExecute=$false;$si.CreateNoWindow=$true
    $si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
    $arguments=@($(if($threadId){'observe'}else{'create-observe'}),'--endpoint',$manifest.endpoint,'--workspace',$workspace,'--marker',$marker)
    if($threadId){$arguments+=@('--thread',$threadId)}
    foreach($argument in $arguments){$si.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($si)
    $actor=@{name=$name;process=$process;events=[Collections.Generic.List[object]]::new();read=$process.StandardOutput.ReadLineAsync();workspace=$workspace;marker=$marker;threadId=$threadId;drain=$process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)}
    $actors.Add($actor)
    if(-not $threadId){$created=Wait-Event $actor 0 {param($e) $e.kind -eq 'test_thread_created'} 30;$actor.threadId=$created.threadId}
    $ready=Wait-Event $actor 0 {param($e) $e.kind -eq 'authoritative_state'} 30
    if($ready.controlAllowed -ne $true){throw 'STRICT_POLICY_REQUIRED'}
    return $actor
}
function Stop-Actor($actor) {
    if(-not $actor.process.HasExited){Send $actor 'quit';$actor.process.StandardInput.Close();if(-not $actor.process.WaitForExit(15000)){throw 'ACTOR_EXIT_UNCONFIRMED'}}
    Pump
}
function Start-Turn($actor,[string]$prompt) {
    $after=$actor.events.Count
    Send $actor ('start '+$prompt)
    $started=Wait-Event $actor $after {param($e) $e.kind -eq 'event' -and $e.method -eq 'turn/started'}
    return $started.turnId
}
function Completed($actor,[string]$turnId,[int]$after=0,[int]$seconds=120) {
    Wait-Event $actor $after {param($e) $e.kind -eq 'event' -and $e.method -eq 'turn/completed' -and $e.turnId -eq $turnId} $seconds
}
function Streaming($actor,[string]$turnId,[int]$after=0) {
    Wait-Event $actor $after {param($e) $e.kind -eq 'event' -and $e.method -eq 'item/agentMessage/delta' -and $e.turnId -eq $turnId -and $e.textCharacters -gt 0}
}
function Marker-In-Reply([string]$threadId,[string]$turnId,[string]$marker) {
    $timeout=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
    try {
        # One explicit final read of our own test reply, after its terminal event. Never history polling.
        $read=$rpc.RequestAsync('thread/read',@{threadId=$threadId;includeTurns=$true},$timeout.Token).GetAwaiter().GetResult()
        foreach($turn in $read.GetProperty('thread').GetProperty('turns').EnumerateArray()) {
            if($turn.GetProperty('id').GetString() -ne $turnId){continue}
            foreach($item in $turn.GetProperty('items').EnumerateArray()) {
                if($item.GetProperty('type').GetString() -ne 'agentMessage'){continue}
                $text=[Text.Json.JsonElement]::new()
                if($item.TryGetProperty('text',[ref]$text) -and $text.GetString().Contains($marker,[StringComparison]::Ordinal)){return $true}
            }
        }
        return $false
    } finally {$timeout.Dispose()}
}
function Verify-Service {
    $p=Get-Process -Id $manifest.server.pid
    if($p.Path -ine $manifest.codexImage -or $p.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$manifest.server.startedUtc).UtcTicks){throw 'SERVICE_IDENTITY_CHANGED'}
    $listeners=@(Get-NetTCPConnection -State Listen -LocalPort $manifest.server.port -ErrorAction Stop)
    if($listeners.Count -ne 1 -or $listeners[0].OwningProcess -ne $p.Id -or $listeners[0].LocalAddress -ne '127.0.0.1' -or $manifest.endpoint -cne "ws://127.0.0.1:$($manifest.server.port)"){throw 'LISTENER_CHANGED'}
}
try {
    $repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $run=[IO.Path]::GetFullPath($RunDirectory)
    if([IO.Path]::GetDirectoryName($run) -ine (Join-Path $repo 'out/shared-desktop-trial')){throw 'INVALID_RUN_DIRECTORY'}
    for($directory=[IO.DirectoryInfo]::new($run);$null -ne $directory;$directory=$directory.Parent){if(-not $directory.Exists -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'INVALID_RUN_DIRECTORY'}}
    $manifest=Get-Content -LiteralPath (Join-Path $run 'manifest.json') -Raw | ConvertFrom-Json
    Verify-Service
    $suffix=[guid]::NewGuid().ToString('N')
    $case=Join-Path $run ('lifecycle-'+$suffix)
    $workspaceA=Join-Path $case 'workspace-a';$workspaceB=Join-Path $case 'workspace-b'
    $null=[IO.Directory]::CreateDirectory($workspaceA);$null=[IO.Directory]::CreateDirectory($workspaceB)
    $resultPath=Join-Path $case 'result.json'
    $writer=[IO.StreamWriter]::new((Join-Path $case 'events.jsonl'),$false,[Text.UTF8Encoding]::new($false));$writer.AutoFlush=$true
    $probeImage=Join-Path $repo 'out/shared-session-probe-auto-controls/CodexControlSharedSessionProbe.exe'
    $null=[Reflection.Assembly]::LoadFrom([IO.Path]::ChangeExtension($probeImage,'.dll'))
    $timeout=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
    try {
        $rpc=[CodexControl.SharedSessionProbe.SharedRpcClient]::ConnectLoopbackAsync([uri]$manifest.endpoint,$timeout.Token).GetAwaiter().GetResult()
        $null=$rpc.RequestAsync('initialize',@{clientInfo=@{name='codex_control_acceptance_reader';version='0.1.0'};capabilities=@{experimentalApi=$true}},$timeout.Token).GetAwaiter().GetResult()
        $null=$rpc.NotifyAsync('initialized',$null,$timeout.Token).GetAwaiter().GetResult()
    } finally {$timeout.Dispose()}
    $a=New-Actor 'a-original' $workspaceA ('CC_SHARED_A_'+$suffix)
    $b=New-Actor 'b-observer' $workspaceA $a.marker $a.threadId
    $report.threadA=$a.threadId
    if (-not $OnlyApproval) {
    Emit @{kind='check_stage';stage='semantic_steer';threadId=$a.threadId}
    $turn=Start-Turn $a '不调用任何工具。请直接写一篇800字的中文天文学文章，持续输出正文。'
    $null=Streaming $b $turn
    $after=$b.events.Count;$steerMarker='STEER_CONFIRMED_'+$suffix
    Send $b ('steer 立即停止原主题，不调用工具。最终只回复这个完整标记：'+$steerMarker)
    $accepted=Wait-Event $b $after {param($e) $e.kind -eq 'rpc_accepted' -and $e.command -eq 'steer'} 30
    $terminal=Completed $b $turn
    $report.results.semanticSteer=@{passed=($accepted.returnedTurnId -eq $turn -and $terminal.terminalTurnStatus -eq 'completed' -and (Marker-In-Reply $a.threadId $turn $steerMarker));turnId=$turn;sameTurn=($accepted.returnedTurnId -eq $turn)}
    Emit @{kind='check_result';name='semanticSteer';result=$report.results.semanticSteer}

    Emit @{kind='check_stage';stage='process_disconnect_during_stream'}
    $turn=Start-Turn $a '不调用任何工具，直接写一篇800字的中文海洋科普文章，持续输出正文。'
    $null=Streaming $b $turn
    $originalPid=$a.process.Id
    Stop-Actor $a
    $after=$b.events.Count
    $nextDelta=Streaming $b $turn $after
    Verify-Service
    $finishMarker='AFTER_EXIT_'+$suffix
    Send $b ('steer 不调用工具，停止长文，最终只回复：'+$finishMarker)
    $terminal=Completed $b $turn
    $a2=New-Actor 'a-reconnected' $workspaceA $a.marker $a.threadId
    $report.results.processDisconnect=@{passed=($a.process.HasExited -and $nextDelta.turnId -eq $turn -and $terminal.terminalTurnStatus -eq 'completed' -and (Marker-In-Reply $a.threadId $turn $finishMarker));originalProcessExited=$a.process.HasExited;originalPid=$originalPid;newPid=$a2.process.Id;newProcess=($originalPid -ne $a2.process.Id);continuedSameTurn=$true;serverIdentityUnchanged=$true;turnId=$turn}
    Emit @{kind='check_result';name='processDisconnect';result=$report.results.processDisconnect}
    } else { $a2=$a }

    Emit @{kind='check_stage';stage='approval_and_concurrency'}
    $afterA=$a2.events.Count;$afterB=$b.events.Count
    $turn=Start-Turn $a2 '这是审批验证。只使用 exec_command 工具，shell 指定 pwsh、login false，在当前目录执行 Set-Content -LiteralPath ./approval-check.txt -Value APPROVAL_TEST。不要用 apply_patch，不要访问其他目录、网络或读取任何配置。如果需要审批就等待；若被拒绝或取消，只回复 DECLINED，不再尝试任何工具或其他方法。'
    $approval=Wait-Event $b $afterB {param($e) $e.kind -eq 'event' -and $e.method -in @('item/commandExecution/requestApproval','item/fileChange/requestApproval')} 120
    $approvalA=Wait-Event $a2 $afterA {param($e) $e.kind -eq 'event' -and $e.method -in @('item/commandExecution/requestApproval','item/fileChange/requestApproval')} 15
    Emit @{kind='check_stage';stage='approval_pending_in_two_clients';threadId=$a.threadId}
    $c=New-Actor 'c-other-thread' $workspaceB ('CC_SHARED_B_'+$suffix)
    $report.threadB=$c.threadId
    $otherMarker='CONCURRENT_OK_'+$suffix
    $otherTurn=Start-Turn $c ('不调用工具，只回复：'+$otherMarker)
    $otherTerminal=Completed $c $otherTurn
    Pump
    $aStillPending=@($b.events | Where-Object {$_.kind -eq 'event' -and $_.method -eq 'turn/completed' -and $_.turnId -eq $turn}).Count -eq 0
    $report.results.concurrentThreads=@{passed=($aStillPending -and $otherTerminal.terminalTurnStatus -eq 'completed' -and (Marker-In-Reply $c.threadId $otherTurn $otherMarker));threadAWaiting=$aStillPending;threadBCompleted=($otherTerminal.terminalTurnStatus -eq 'completed')}
    Emit @{kind='check_result';name='concurrentThreads';result=$report.results.concurrentThreads}

    Stop-Actor $a2
    $a3=New-Actor 'a-pending-reconnected' $workspaceA $a.marker $a.threadId
    $replayed=$null
    try {$replayed=Wait-Event $a3 0 {param($e) $e.kind -eq 'event' -and $e.method -in @('item/commandExecution/requestApproval','item/fileChange/requestApproval')} 10} catch {$report.results.pendingReplay=@{passed=$false;reason='PENDING_REQUEST_NOT_REPLAYED'}}
    if($replayed){$report.results.pendingReplay=@{passed=$true;newProcess=($a3.process.Id -ne $a2.process.Id);originalProcessExited=$a2.process.HasExited}}
    Emit @{kind='check_result';name='pendingReplay';result=$report.results.pendingReplay}
    $parallelTurn=Start-Turn $c '不调用任何工具，直接写一篇800字的中文科普文章，持续输出正文。'
    $null=Streaming $c $parallelTurn
    $decisionStart=$b.events.Count
    $negativeDecision=if('decline' -in $approval.decisions){'decline'}elseif('cancel' -in $approval.decisions){'cancel'}else{throw 'NEGATIVE_DECISION_NOT_SUPPORTED'}
    Send $b ($negativeDecision+' '+$approval.requestId)
    if($replayed -and 'cancel' -in $replayed.decisions){Send $a3 ('cancel '+$replayed.requestId)}
    $resolved=Wait-Event $b $decisionStart {param($e) $e.kind -eq 'event' -and $e.method -eq 'serverRequest/resolved' -and $e.requestId -eq $approval.requestId} 30
    $terminal=Completed $b $turn 0 90
    $parallelAfter=$c.events.Count
    $null=Streaming $c $parallelTurn $parallelAfter
    Send $c 'interrupt'
    $parallelTerminal=Completed $c $parallelTurn
    $report.results.stopIsolation=@{passed=($parallelTerminal.terminalTurnStatus -eq 'interrupted');threadBStreamedAfterATerminated=$true;threadBStoppedOnlyByOwnInterrupt=($parallelTerminal.terminalTurnStatus -eq 'interrupted')}
    Emit @{kind='check_result';name='stopIsolation';result=$report.results.stopIsolation}
    Pump
    $resolvedCount=@($b.events | Where-Object {$_.kind -eq 'event' -and $_.method -eq 'serverRequest/resolved' -and $_.requestId -eq $approval.requestId}).Count
    $terminalCount=@($b.events | Where-Object {$_.kind -eq 'event' -and $_.method -eq 'turn/completed' -and $_.turnId -eq $turn}).Count
    $report.results.approvalRejection=@{passed=($resolvedCount -eq 1 -and $terminalCount -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $workspaceA 'approval-check.txt')));decision=$negativeDecision;requestSeenByTwoClients=$true;negativeDecisionRaceAttempted=($null -ne $replayed);resolvedCount=$resolvedCount;terminalCount=$terminalCount;terminalStatus=$terminal.terminalTurnStatus;fileNotWritten=(-not (Test-Path -LiteralPath (Join-Path $workspaceA 'approval-check.txt')));approvePathTested=$false}
    Emit @{kind='check_result';name='approvalRejection';result=$report.results.approvalRejection}
    Verify-Service
    $report.serverIdentityUnchanged=$true
} catch {
    $known=@('INVALID_RUN_DIRECTORY','SERVICE_IDENTITY_CHANGED','LISTENER_CHANGED','ACTOR_EXITED','ACTOR_EXIT_UNCONFIRMED','EVENT_TIMEOUT','STRICT_POLICY_REQUIRED','NEGATIVE_DECISION_NOT_SUPPORTED')
    $report.failure=if($_.Exception.Message -in $known){$_.Exception.Message}else{'CHECK_FAILED'}
    $report.failureType=$_.Exception.GetType().FullName
} finally {
    # Interrupt only newly created test Threads, via their still-connected, policy-gated probes.
    foreach($group in @($actors | Group-Object threadId)) {
        $alive=@($group.Group | Where-Object {-not $_.process.HasExited})
        if($alive.Count -gt 0) {
            try {
                $actor=$alive[0];$after=$actor.events.Count;Send $actor 'status'
                $state=Wait-Event $actor $after {param($e) $e.kind -eq 'authoritative_state'} 20
                if($state.turnId -and $state.controlAllowed){Send $actor 'interrupt';$null=Completed $actor $state.turnId $after 30}
            } catch {$report.cleanupUnconfirmed=$true}
        }
    }
    foreach($actor in $actors){try{Stop-Actor $actor}catch{$report.cleanupUnconfirmed=$true}}
    if($rpc){$null=$rpc.DisposeAsync().AsTask().GetAwaiter().GetResult()}
    if($resultPath){$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding utf8}
    Emit $report
    if($writer){$writer.Dispose()}
}
