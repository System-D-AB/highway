[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PassthroughArgs
)

$ErrorActionPreference = 'Stop'

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    Write-Host "Requesting Administrator elevation..."
    $scriptPath = $MyInvocation.MyCommand.Path
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$scriptPath`"")
    if ($PassthroughArgs) {
        $argList += $PassthroughArgs
    }
    Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs
    exit 0
}

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

$allArgs = @("--install", "--start")
if ($PassthroughArgs -notcontains "--config" -and (Test-Path $configPath)) {
    $allArgs += @("--config", $configPath)
}

if ($PassthroughArgs) {
    $allArgs += $PassthroughArgs
}

& $exePath @allArgs
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Warning "Service installation exited with code $exitCode."
}
exit $exitCode
