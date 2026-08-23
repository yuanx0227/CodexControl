[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$InnoCompiler,
    [string]$SignToolCommand
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $workspace 'out\release'))
$allowedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $workspace 'out'))
if (-not $outputRoot.StartsWith($allowedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing release output outside workspace out directory: $outputRoot"
}

if (Test-Path -LiteralPath $outputRoot) {
    $resolved = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $outputRoot).Path)
    if ($resolved -ne $outputRoot) {
        throw "Refusing to clean unexpected release directory: $resolved"
    }

    # Windows 不允许删除另一个终端正在作为 cwd 使用的目录。Agent 发布目录本身保留，
    # 但其内容必须全部删除，避免旧文件混入新的 single-file Release。
    $preservedAgentDirectory = [System.IO.Path]::GetFullPath((Join-Path $outputRoot 'agent-win-x64'))
    foreach ($child in Get-ChildItem -Force -LiteralPath $resolved) {
        $childPath = [System.IO.Path]::GetFullPath($child.FullName)
        if ($child.PSIsContainer -and $childPath -eq $preservedAgentDirectory) {
            Get-ChildItem -Force -LiteralPath $childPath | Remove-Item -Recurse -Force
            continue
        }

        Remove-Item -Recurse -Force -LiteralPath $childPath
    }
}

$agentProject = Join-Path $workspace 'src\agent\CodexControl.Agent.csproj'
$relayProject = Join-Path $workspace 'src\relay\CodexControl.Relay.csproj'
$agentTests = Join-Path $workspace 'tests\CodexControl.Agent.Tests\CodexControl.Agent.Tests.csproj'
$relayTests = Join-Path $workspace 'tests\CodexControl.Relay.Tests\CodexControl.Relay.Tests.csproj'
$webRoot = Join-Path $workspace 'src\web'
$protocolSync = Join-Path $workspace 'tools\Sync-ProtocolMessageTypes.ps1'
$installerScript = Join-Path $workspace 'installer\CodexControl.iss'

& $protocolSync -Check

& dotnet build $agentTests --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Agent Release build failed.' }
& dotnet build $relayTests --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Relay Release build failed.' }

Push-Location $webRoot
try {
    & npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'PWA npm ci failed.' }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'PWA build failed.' }

    if (-not $SkipTests) {
        & npx playwright install webkit
        if ($LASTEXITCODE -ne 0) { throw 'Playwright WebKit installation failed.' }
        & npm test
        if ($LASTEXITCODE -ne 0) { throw 'PWA Playwright tests failed.' }
    }
}
finally {
    Pop-Location
}

if (-not $SkipTests) {
    & dotnet run --project $agentTests --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Agent tests failed.' }
    & dotnet run --project $relayTests --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Relay tests failed.' }
}

$agentOutput = Join-Path $outputRoot 'agent-win-x64'
$relayOutput = Join-Path $outputRoot 'relay'
& dotnet publish $agentProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $agentOutput `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
New-Item -ItemType Directory -Force -Path (Join-Path $agentOutput 'data') | Out-Null
& dotnet publish $relayProject `
    --configuration Release `
    --self-contained false `
    --output $relayOutput `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'Relay publish failed.' }

Copy-Item -Recurse -LiteralPath (Join-Path $webRoot 'dist') -Destination (Join-Path $outputRoot 'web')
$deployOutput = Join-Path $outputRoot 'deploy'
Copy-Item -Recurse -LiteralPath (Join-Path $workspace 'deploy') -Destination $deployOutput
Get-ChildItem -Recurse -File -LiteralPath $deployOutput -Filter '*.pem' | Remove-Item -Force

$installerOutput = Join-Path $outputRoot 'installer'
New-Item -ItemType Directory -Force -Path $installerOutput | Out-Null
if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $innoCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    $InnoCompiler = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or
    -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Inno Setup 6 compiler was not found. Install JRSoftware.InnoSetup or pass -InnoCompiler.'
}

$installerBaseName = 'CodexControl-Setup-UNSIGNED'
$innoArguments = @(
    "/DSourceRoot=$agentOutput",
    "/DOutputRoot=$installerOutput",
    '/DAppVersion=0.6.0',
    "/DOutputBaseName=$installerBaseName"
)
if (-not [string]::IsNullOrWhiteSpace($SignToolCommand)) {
    $installerBaseName = 'CodexControl-Setup'
    $innoArguments[3] = "/DOutputBaseName=$installerBaseName"
    $innoArguments += '/DSIGN_INSTALLER=1'
    $innoArguments += "/Scodexsign=$SignToolCommand"
}
else {
    [System.IO.File]::WriteAllText(
        (Join-Path $agentOutput 'UNSIGNED.txt'),
        "UNSIGNED INTERNAL/TEST BUILD`r`nSmartScreen and enterprise policy acceptance are not proven.`r`n")
}
$innoArguments += $installerScript
& $InnoCompiler @innoArguments
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$hashLines = Get-ChildItem -Recurse -File -LiteralPath $outputRoot |
    Where-Object { $_.Name -ne 'SHA256SUMS' } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($outputRoot, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
[System.IO.File]::WriteAllLines((Join-Path $outputRoot 'SHA256SUMS'), $hashLines)

Write-Output "Release created: $outputRoot"
