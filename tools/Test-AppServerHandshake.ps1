[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CodexPath,

    [ValidateRange(1, 60)]
    [int]$TimeoutSeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CodexPath -PathType Leaf)) {
    throw "Codex executable was not found: $CodexPath"
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = (Resolve-Path -LiteralPath $CodexPath).Path
$startInfo.ArgumentList.Add('app-server')
$startInfo.ArgumentList.Add('--listen')
$startInfo.ArgumentList.Add('stdio://')
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
$startInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
$startInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo

function Send-RpcMessage {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$TargetProcess,

        [Parameter(Mandatory = $true)]
        [hashtable]$Message
    )

    $json = $Message | ConvertTo-Json -Compress -Depth 50
    $TargetProcess.StandardInput.WriteLine($json)
    $TargetProcess.StandardInput.Flush()
}

function Read-RpcMessage {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$TargetProcess,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutMilliseconds
    )

    $readTask = $TargetProcess.StandardOutput.ReadLineAsync()
    $timeoutTask = [System.Threading.Tasks.Task]::Delay($TimeoutMilliseconds)
    $completedTask = [System.Threading.Tasks.Task]::WhenAny($readTask, $timeoutTask).GetAwaiter().GetResult()

    if ($completedTask -ne $readTask) {
        throw "Timed out waiting for an app-server JSONL message after $TimeoutMilliseconds ms."
    }

    $line = $readTask.GetAwaiter().GetResult()
    if ($null -eq $line) {
        throw 'App-server closed stdout before the expected response arrived.'
    }

    return $line | ConvertFrom-Json -Depth 100
}

function Read-RpcResponse {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$TargetProcess,

        [Parameter(Mandatory = $true)]
        [object]$RequestId,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutMilliseconds,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$ObservedMessages
    )

    $deadline = [System.DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([System.DateTimeOffset]::UtcNow -lt $deadline) {
        $remaining = [Math]::Max(1, [int]($deadline - [System.DateTimeOffset]::UtcNow).TotalMilliseconds)
        $message = Read-RpcMessage -TargetProcess $TargetProcess -TimeoutMilliseconds $remaining
        $ObservedMessages.Add($message)
        if ($message.PSObject.Properties.Name -contains 'id' -and $message.id -eq $RequestId) {
            return $message
        }
    }

    throw "Timed out waiting for JSON-RPC response id '$RequestId'."
}

$observedMessages = [System.Collections.Generic.List[object]]::new()
$stderrTask = $null

try {
    if (-not $process.Start()) {
        throw 'Failed to start codex app-server.'
    }

    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timeoutMilliseconds = $TimeoutSeconds * 1000

    Send-RpcMessage -TargetProcess $process -Message @{
        method = 'thread/list'
        id = 'phase0:before-initialize'
        params = @{}
    }
    $preInitialize = Read-RpcResponse -TargetProcess $process -RequestId 'phase0:before-initialize' -TimeoutMilliseconds $timeoutMilliseconds -ObservedMessages $observedMessages

    Send-RpcMessage -TargetProcess $process -Message @{
        method = 'initialize'
        id = 'phase0:initialize'
        params = @{
            clientInfo = @{
                name = 'codex_control_phase0'
                title = 'Codex Control Phase 0 Probe'
                version = '0.1.0'
            }
        }
    }
    $initialize = Read-RpcResponse -TargetProcess $process -RequestId 'phase0:initialize' -TimeoutMilliseconds $timeoutMilliseconds -ObservedMessages $observedMessages

    Send-RpcMessage -TargetProcess $process -Message @{
        method = 'initialized'
    }

    Send-RpcMessage -TargetProcess $process -Message @{
        method = 'thread/list'
        id = 'phase0:thread-list'
        params = @{
            limit = 1
        }
    }
    $threadList = Read-RpcResponse -TargetProcess $process -RequestId 'phase0:thread-list' -TimeoutMilliseconds $timeoutMilliseconds -ObservedMessages $observedMessages

    $preInitializeRejected = $preInitialize.PSObject.Properties.Name -contains 'error'
    $initializeSucceeded = $initialize.PSObject.Properties.Name -contains 'result'
    $threadListSucceeded = $threadList.PSObject.Properties.Name -contains 'result'

    if (-not $preInitializeRejected) {
        throw 'App-server unexpectedly accepted thread/list before initialize.'
    }
    if (-not $initializeSucceeded) {
        throw 'App-server initialize did not return a result.'
    }
    if (-not $threadListSucceeded) {
        throw 'App-server thread/list did not return a result after initialization.'
    }

    [pscustomobject]@{
        codexPath = $startInfo.FileName
        preInitializeError = $preInitialize.error
        initializeResult = $initialize.result
        threadListReturned = @($threadList.result.data).Count
        observedNotificationMethods = @(
            $observedMessages |
                Where-Object { $_.PSObject.Properties.Name -contains 'method' } |
                ForEach-Object { $_.method } |
                Select-Object -Unique
        )
    } | ConvertTo-Json -Depth 50
}
finally {
    if (-not $process.HasExited) {
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(5000)) {
            $process.Kill($true)
            $process.WaitForExit()
        }
    }

    if ($null -ne $stderrTask) {
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($stderr) {
            Write-Verbose $stderr
        }
    }

    $process.Dispose()
}
