#requires -Version 7.0
<#
Run in the original 01-start PowerShell window before closing it.
Reads only error-record metadata for the trial runner. Never prints exception
messages, source lines, command lines, environment, or raw server logs.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runnerPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Invoke-SharedDesktopTrial.ps1'))
$records = @((Get-Variable -Name Error -Scope Global -ValueOnly))
$found = 0
foreach ($record in $records) {
    if ($record -isnot [Management.Automation.ErrorRecord] -or $null -eq $record.InvocationInfo -or
        -not [string]::Equals($record.InvocationInfo.ScriptName, $runnerPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $causes = @()
    $exception = $record.Exception
    for ($depth = 0; $null -ne $exception -and $depth -lt 4; $depth++) {
        $cause = @{ type = $exception.GetType().FullName; hresult = $exception.HResult }
        if ($exception -is [ComponentModel.Win32Exception]) { $cause.nativeErrorCode = $exception.NativeErrorCode }
        $causes += $cause
        $exception = $exception.InnerException
    }
    [pscustomobject]@{ kind = 'trial_error_metadata'; scriptLine = $record.InvocationInfo.ScriptLineNumber; causes = $causes } |
        ConvertTo-Json -Depth 6 -Compress
    $found++
    if ($found -ge 12) { break }
}
if ($found -eq 0) {
    [pscustomobject]@{ kind = 'trial_error_metadata_unavailable'; reason = 'ORIGINAL_CONSOLE_REQUIRED_OR_ERRORS_EXPIRED' } |
        ConvertTo-Json -Compress
}
