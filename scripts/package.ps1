[CmdletBinding()]
param(
    [string]$Version = "1.0.0-preview.1",
    [string]$Rid = "win-x64",
    [string]$OutputDir = ""
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\dist"
}

if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $repoRoot $OutputDir
}

$packageName = "highway-$Version-$Rid"
$stageDir = Join-Path $repoRoot "artifacts\temp-$packageName"
$zipPath = Join-Path $OutputDir "$packageName.zip"

Write-Host "=========================================="
Write-Host " Packaging Highway Distribution"
Write-Host " Version : $Version"
Write-Host " RID     : $Rid"
Write-Host " Target  : $zipPath"
Write-Host "=========================================="

if (Test-Path $stageDir) {
    Remove-Item -Path $stageDir -Recurse -Force
}
New-Item -Path $stageDir -ItemType Directory -Force | Out-Null

if (-not (Test-Path $OutputDir)) {
    New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
}

$binDir = Join-Path $stageDir "bin"
$configDir = Join-Path $stageDir "config"
$dataDir = Join-Path $stageDir "data"
$logsDir = Join-Path $stageDir "logs"
$scriptsDir = Join-Path $stageDir "scripts"

New-Item -Path $configDir -ItemType Directory -Force | Out-Null
New-Item -Path $dataDir -ItemType Directory -Force | Out-Null
New-Item -Path $logsDir -ItemType Directory -Force | Out-Null
New-Item -Path $scriptsDir -ItemType Directory -Force | Out-Null

# 1. Publish self-contained executable
Write-Host "Publishing self-contained Highway.Server.Host..."
$projectPath = Join-Path $repoRoot "src\Highway.Server.Host\Highway.Server.Host.csproj"
& dotnet publish $projectPath -c Release -r $Rid --self-contained /p:Version=$Version -o $binDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# 2. Copy config. Both files ship: highway.json is the broker's configuration, and
# users.acl is feature 034 R3.1's nopass command allowlist — the profile that makes
# FLUSHALL, CONFIG and KEYS unreachable on a trusted network. A distribution without
# it leaves the operator to reconstruct a 30-command allowlist by hand.
foreach ($cfg in @("highway.json", "users.acl")) {
    $srcConfig = Join-Path $repoRoot "config\$cfg"
    if (Test-Path $srcConfig) {
        Copy-Item -Path $srcConfig -Destination (Join-Path $configDir $cfg) -Force
    } else {
        throw "Required configuration file not found: $srcConfig"
    }
}

# 3. Copy scripts
$scriptsToCopy = @("run.bat", "run.ps1", "install-service.bat", "install-service.ps1", "uninstall-service.ps1")
foreach ($s in $scriptsToCopy) {
    $srcScript = Join-Path $repoRoot "scripts\$s"
    if (Test-Path $srcScript) {
        Copy-Item -Path $srcScript -Destination (Join-Path $scriptsDir $s) -Force
    }
}

# 4. Copy docs and licenses
$readmeTemplate = Join-Path $repoRoot "docs\distribution\README.md"
if (Test-Path $readmeTemplate) {
    $readmeContent = Get-Content -Path $readmeTemplate -Raw
    $readmeContent = $readmeContent -replace '\{version\}', $Version
    Set-Content -Path (Join-Path $stageDir "README.md") -Value $readmeContent -Encoding UTF8
}

$licenseFile = Join-Path $repoRoot "LICENSE"
if (Test-Path $licenseFile) {
    Copy-Item -Path $licenseFile -Destination (Join-Path $stageDir "LICENSE") -Force
}

$noticesFile = Join-Path $repoRoot "THIRD-PARTY-NOTICES.md"
if (Test-Path $noticesFile) {
    Copy-Item -Path $noticesFile -Destination (Join-Path $stageDir "THIRD-PARTY-NOTICES.md") -Force
}

# 5. Create Zip archive
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Write-Host "Compressing archive to $zipPath..."
Add-Type -AssemblyName System.IO.Compression          # ZipArchive, ZipArchiveMode
Add-Type -AssemblyName System.IO.Compression.FileSystem   # ZipFile, ZipFileExtensions

# Entries are written one at a time with forward-slash names on purpose.
# ZipFile::CreateFromDirectory under Windows PowerShell 5.1 resolves to the .NET
# Framework implementation, which names entries with Path.DirectorySeparatorChar —
# backslashes. That violates the ZIP specification (APPNOTE 4.4.17: forward slash
# only). Windows opens such an archive, but `unzip` on Linux warns and macOS's
# Archive Utility can produce files literally named "bin\highways.exe" instead of a
# bin directory. This is a published release artifact, so it has to unpack anywhere.
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $stageFull = (Resolve-Path $stageDir).Path.TrimEnd('\')
    foreach ($file in Get-ChildItem -Path $stageDir -Recurse -File) {
        $entryName = $file.FullName.Substring($stageFull.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }

    # Empty directories need an explicit trailing-slash entry — writing files alone
    # would silently drop data/ and logs/. R3.1 ships both: data/ is the default
    # dataDir target and logs/ is where the scripts redirect output, so a missing
    # logs/ makes `run.bat >> logs\highway.log` fail on first use.
    foreach ($dir in Get-ChildItem -Path $stageDir -Recurse -Directory) {
        if (-not (Get-ChildItem -Path $dir.FullName -Recurse -File)) {
            $dirEntry = $dir.FullName.Substring($stageFull.Length + 1).Replace('\', '/') + '/'
            $zip.CreateEntry($dirEntry) | Out-Null
        }
    }
}
finally {
    $zip.Dispose()
}

# 6. Cleanup
Remove-Item -Path $stageDir -Recurse -Force

$zipItem = Get-Item $zipPath
$zipSizeMb = [Math]::Round($zipItem.Length / 1MB, 2)
Write-Host "Successfully generated $packageName.zip ($zipSizeMb MB)"
