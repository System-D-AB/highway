[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PassthroughArgs
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir

$exePath = Join-Path $rootDir "bin\highways.exe"
if (-not (Test-Path $exePath)) {
    $devExe = Join-Path $rootDir "src\Highway.Server.Host\bin\Release\net10.0\win-x64\publish\highways.exe"
    if (Test-Path $devExe) {
        $exePath = $devExe
    } else {
        $devDebugExe = Join-Path $rootDir "src\Highway.Server.Host\bin\Debug\net10.0\highways.exe"
        if (Test-Path $devDebugExe) {
            $exePath = $devDebugExe
        }
    }
}

if (-not (Test-Path $exePath)) {
    Write-Error "Could not find highways executable at '$exePath'."
    exit 1
}

$configPath = Join-Path $rootDir "config\highway.json"

$allArgs = @()
if ($PassthroughArgs -notcontains "--config") {
    if (Test-Path $configPath) {
        $allArgs += @("--config", $configPath)
    }
}

if ($PassthroughArgs) {
    $allArgs += $PassthroughArgs
}

& $exePath @allArgs
exit $LASTEXITCODE
