#requires -Version 7.0
<# Launch manually outside Desktop. Waits for normal exit; never closes Desktop or the service. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$actors=[Collections.Generic.List[object]]::new()
$writer=$null;$rpc=$null;$resultPath=$null;$original=$null;$actor=$null
$drains=[Collections.Generic.List[object]]::new()
$report=[ordered]@{kind='real_desktop_exit_check';passed=$false;desktopExited=$false;desktopReconnected=$false;desktopSameThreadUiVerified=$false;automaticApproval=$false}
function Desktop-Processes {
    @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,ExecutablePath,CreationDate |
        Where-Object ExecutablePath -Match '\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(ChatGPT|Codex)\.exe$')
}
try {
    if(-not $IsWindows){throw 'WINDOWS_REQUIRED'}
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'NORMAL_USER_REQUIRED'}
    $processes=@(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name)
    $ancestorId=$PID;$visited=[Collections.Generic.HashSet[int]]::new()
    while($ancestorId -and $visited.Add($ancestorId)) {
        $ancestor=$processes | Where-Object ProcessId -eq $ancestorId | Select-Object -First 1
        if(-not $ancestor){break}
        if($ancestor.Name -in @('ChatGPT.exe','Codex.exe')){throw 'EXTERNAL_TERMINAL_REQUIRED'}
        $ancestorId=[int]$ancestor.ParentProcessId
    }
    if(-not ('DesktopExitCheckNative' -as [type])) {
        Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class DesktopExitCheckNative { [DllImport("kernel32.dll", SetLastError=true)] public static extern bool IsProcessInJob(IntPtr p, IntPtr j, out bool value); }'
    }
    $inJob=$false
    if(-not [DesktopExitCheckNative]::IsProcessInJob([Diagnostics.Process]::GetCurrentProcess().Handle,[IntPtr]::Zero,[ref]$inJob) -or $inJob){throw 'EXTERNAL_NON_JOB_TERMINAL_REQUIRED'}
    $repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $run=[IO.Path]::GetFullPath($RunDirectory)
    if([IO.Path]::GetDirectoryName($run) -ine (Join-Path $repo 'out/shared-desktop-trial')){throw 'INVALID_RUN_DIRECTORY'}
    for($directory=[IO.DirectoryInfo]::new($run);$null -ne $directory;$directory=$directory.Parent){if(-not $directory.Exists -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'INVALID_RUN_DIRECTORY'}}
    $manifest=Get-Content -LiteralPath (Join-Path $run 'manifest.json') -Raw | ConvertFrom-Json
    $original=Get-Process -Id $manifest.desktop.pid
    if($original.Path -ine $manifest.desktopImage -or $original.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$manifest.desktop.startedUtc).UtcTicks){throw 'DESKTOP_IDENTITY_CHANGED'}
    # Import only reusable function definitions, never the lifecycle suite's executable test body.
    $tokens=$null;$parseErrors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-SharedSessionLifecycle.ps1'),[ref]$tokens,[ref]$parseErrors)
    if($parseErrors){throw 'HELPER_PARSE_FAILED'}
    $names=@('Emit','Pump','Wait-Event','Send','New-Actor','Stop-Actor','Start-Turn','Completed','Streaming','Marker-In-Reply','Verify-Service')
    foreach($name in $names){$function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$false);if(-not $function){throw 'HELPER_MISSING'};. ([scriptblock]::Create($function.Extent.Text))}
    Verify-Service
    $suffix=[guid]::NewGuid().ToString('N');$case=Join-Path $run ('desktop-exit-'+$suffix);$workspace=Join-Path $case 'workspace'
    $null=[IO.Directory]::CreateDirectory($workspace)
    $resultPath=Join-Path $case 'result.json'
    $writer=[IO.StreamWriter]::new((Join-Path $case 'events.jsonl'),$false,[Text.UTF8Encoding]::new($false));$writer.AutoFlush=$true
    $probeImage=Join-Path $repo 'out/shared-session-probe-auto-controls/CodexControlSharedSessionProbe.exe'
    $null=[Reflection.Assembly]::LoadFrom([IO.Path]::ChangeExtension($probeImage,'.dll'))
    $timeout=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
    try {
        $rpc=[CodexControl.SharedSessionProbe.SharedRpcClient]::ConnectLoopbackAsync([uri]$manifest.endpoint,$timeout.Token).GetAwaiter().GetResult()
        $null=$rpc.RequestAsync('initialize',@{clientInfo=@{name='codex_control_desktop_exit';version='0.1.0'};capabilities=@{experimentalApi=$true}},$timeout.Token).GetAwaiter().GetResult()
        $null=$rpc.NotifyAsync('initialized',$null,$timeout.Token).GetAwaiter().GetResult()
    } finally {$timeout.Dispose()}
    $actor=New-Actor 'surviving-probe' $workspace ('CC_SHARED_EXIT_'+$suffix)
    $report.threadId=$actor.threadId;$report.serverPid=$manifest.server.pid;$report.originalDesktopPid=$original.Id
    $turn=Start-Turn $actor '不调用任何工具，请直接写一篇2000字的中文科普文章，持续输出正文。'
    $report.turnId=$turn
    $null=Streaming $actor $turn
    Emit @{kind='exit_desktop_now';threadId=$actor.threadId;turnId=$turn}
    Write-Host 'Now exit Desktop normally, including its tray. Keep 01 and this window open. Do not run 03 during this check.'
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    while(-not $original.HasExited -or @(Desktop-Processes).Count -gt 0) {
        Pump
        if([DateTime]::UtcNow -ge $deadline){throw 'DESKTOP_EXIT_WAIT_EXPIRED'}
        if(@($actor.events | Where-Object {$_.kind -eq 'event' -and $_.method -eq 'turn/completed' -and $_.turnId -eq $turn}).Count -gt 0){throw 'TURN_FINISHED_BEFORE_DESKTOP_EXIT'}
        Start-Sleep -Milliseconds 200
    }
    $report.desktopExited=$true
    Verify-Service
    $after=$actor.events.Count;$marker='DESKTOP_GONE_'+$suffix
    Send $actor ('steer 不调用工具，停止长文，最终只回复这个完整标记：'+$marker)
    $accepted=Wait-Event $actor $after {param($e) $e.kind -eq 'rpc_accepted' -and $e.command -eq 'steer'} 30
    $terminal=Completed $actor $turn 0 180
    $report.continuedSameTurnAfterDesktopExit=$accepted.returnedTurnId -eq $turn -and $terminal.terminalTurnStatus -eq 'completed' -and (Marker-In-Reply $actor.threadId $turn $marker)
    Verify-Service
    $report.serverIdentityUnchanged=$true
} catch {
    $report.failureType=$_.Exception.GetType().FullName
    $allowed=@('WINDOWS_REQUIRED','NORMAL_USER_REQUIRED','EXTERNAL_TERMINAL_REQUIRED','EXTERNAL_NON_JOB_TERMINAL_REQUIRED','INVALID_RUN_DIRECTORY','DESKTOP_IDENTITY_CHANGED','HELPER_PARSE_FAILED','HELPER_MISSING','SERVICE_IDENTITY_CHANGED','LISTENER_CHANGED','DESKTOP_EXIT_WAIT_EXPIRED','TURN_FINISHED_BEFORE_DESKTOP_EXIT','EVENT_TIMEOUT','STRICT_POLICY_REQUIRED','ACTOR_EXITED')
    $report.failure=if($_.Exception.Message -in $allowed){$_.Exception.Message}else{'DESKTOP_EXIT_CHECK_FAILED'}
} finally {
    if($actor -and -not $actor.process.HasExited) {
        try {
            $after=$actor.events.Count;Send $actor 'status';$state=Wait-Event $actor $after {param($e) $e.kind -eq 'authoritative_state'} 20
            if($state.turnId -and $state.controlAllowed){Send $actor 'interrupt';$null=Completed $actor $state.turnId $after 30}
            Stop-Actor $actor
        } catch {$report.testCleanupUnconfirmed=$true}
    }
    if($rpc){$null=$rpc.DisposeAsync().AsTask().GetAwaiter().GetResult()}
    if($original -and $original.HasExited) {
        try {
            if(@(Desktop-Processes).Count -ne 0){throw 'DESKTOP_ALREADY_REOPENED'}
            Verify-Service
            if(-not (Test-Path -LiteralPath $manifest.desktopImage -PathType Leaf)){throw 'DESKTOP_IMAGE_CHANGED'}
            $si=[Diagnostics.ProcessStartInfo]::new([string]$manifest.desktopImage)
            $si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
            $si.WorkingDirectory=[IO.Path]::GetDirectoryName([string]$manifest.desktopImage)
            $si.Environment['CODEX_APP_SERVER_WS_URL']=[string]$manifest.endpoint
            $desktop=[Diagnostics.Process]::Start($si)
            $drains.Add($desktop.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null));$drains.Add($desktop.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null))
            $report.reopenedDesktopPid=$desktop.Id
            $deadline=[DateTime]::UtcNow.AddSeconds(30)
            while([DateTime]::UtcNow -lt $deadline -and -not $desktop.HasExited) {
                $rows=@(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,CreationDate)
                $ids=[Collections.Generic.HashSet[int]]::new();$null=$ids.Add($desktop.Id)
                do {$changed=$false;foreach($row in $rows){if($ids.Contains([int]$row.ParentProcessId) -and $row.CreationDate.ToUniversalTime() -ge $desktop.StartTime.ToUniversalTime()){if($ids.Add([int]$row.ProcessId)){$changed=$true}}}}while($changed)
                $connections=@(Get-NetTCPConnection -ErrorAction Stop | Where-Object {$_.RemoteAddress -eq '127.0.0.1' -and $_.RemotePort -eq $manifest.server.port -and $_.State -eq 'Established' -and $ids.Contains([int]$_.OwningProcess)})
                if($connections.Count -gt 0){Verify-Service;$report.desktopReconnected=$true;break}
                Start-Sleep -Milliseconds 500
            }
        } catch {$report.desktopRestartUnconfirmed=$true}
    }
    $report.passed=$report.desktopExited -and $report.continuedSameTurnAfterDesktopExit -eq $true -and $report.desktopReconnected
    if($resultPath){$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding utf8}
    $report | ConvertTo-Json -Depth 8 -Compress
    if($writer){$writer.Dispose()}
    Write-Host 'Keep this window and 01 open until the normal 03 restore procedure has completed. If Desktop was not reopened, retain the result file for diagnosis.'
}
