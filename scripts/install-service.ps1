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
$lineFile = Join-Path $rootDir "LINE"
$line = if (Test-Path $lineFile) { (Get-Content $lineFile -Raw).Trim() } else { "(unversioned)" }

# 079 R4: one Highway service, re-pointed per line. If the service already runs another line's binaries, stop it and
# remove it first, saying which line it replaces; that line's data and config stay untouched for a rollback.
$serviceName = "Highway"
$passed = @($PassthroughArgs | Where-Object { $_ })
$nameAt = [Array]::IndexOf([string[]]$passed, "--service-name")
if ($nameAt -ge 0 -and $nameAt + 1 -lt $passed.Count) { $serviceName = $passed[$nameAt + 1] }

$existing = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
if ($existing) {
    $existingExe = ($existing.PathName -replace '^"([^"]+)".*$', '$1') -replace '^(\S+).*$', '$1'
    $existingRoot = Split-Path -Parent (Split-Path -Parent $existingExe)
    if ($existingRoot -ieq $rootDir) {
        Write-Host "Service '$serviceName' already runs this line ($rootDir); reinstalling it."
    } else {
        Write-Host "Service '$serviceName' runs another line: $existingRoot"
        Write-Host "Replacing it with line $line ($rootDir). To go back later, run that line's scripts\install-service.ps1."
    }
    if ($existing.State -ne 'Stopped') {
        Write-Host "Stopping '$serviceName'..."
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
    }
    & $exePath --uninstall --service-name $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Could not remove the existing '$serviceName' service (exit $LASTEXITCODE)."
        exit 1
    }
}

# 079 R5: a port still held now belongs to a broker started by hand (or another program) - refuse, naming it.
$cfgArgs = @("--validate")
if (Test-Path $configPath) { $cfgArgs += @("--config", $configPath) }
$effective = & $exePath @cfgArgs 2>&1
$ports = @($effective | Select-String '^\s+port\s+:\s+(\d+)' | ForEach-Object { [int]$_.Matches[0].Groups[1].Value })
foreach ($port in $ports) {
    $holder = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($holder) {
        $proc = Get-Process -Id $holder.OwningProcess -ErrorAction SilentlyContinue
        Write-Error "Port $port is already in use by '$($proc.ProcessName)' (pid $($holder.OwningProcess)). Stop it, then run this script again."
        exit 1
    }
}

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
} else {
    Write-Host "Service '$serviceName' now runs Highway line $line from $rootDir."
}
exit $exitCode
