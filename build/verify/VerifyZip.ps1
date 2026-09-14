$ErrorActionPreference = 'Stop'

$zipPath = "c:\Software\ai\highway\artifacts\dist\highway-1.0.0-preview.1-win-x64.zip"
if (-not (Test-Path $zipPath)) {
    throw "Zip file not found at $zipPath"
}

$tempUnpack = Join-Path ([System.IO.Path]::GetTempPath()) ("highway-verify-" + [Guid]::NewGuid().ToString("N"))
Write-Host "Unpacking $zipPath to $tempUnpack..."
Expand-Archive -Path $zipPath -DestinationPath $tempUnpack

try {
    Write-Host "`n=== 1. Directory Structure ==="
    $items = Get-ChildItem $tempUnpack | Select-Object -ExpandProperty Name
    $items | ForEach-Object { Write-Host " - $_" }

    $required = @("bin", "config", "data", "logs", "scripts", "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md")
    foreach ($req in $required) {
        if ($items -notcontains $req) {
            throw "Missing required entry in zip: $req"
        }
    }
    Write-Host "[PASS] All root entries present."

    Write-Host "`n=== 2. Scripts Directory ==="
    $scriptItems = Get-ChildItem (Join-Path $tempUnpack "scripts") | Select-Object -ExpandProperty Name
    $scriptItems | ForEach-Object { Write-Host " - $_" }
    $requiredScripts = @("run.bat", "run.ps1", "install-service.bat", "install-service.ps1", "uninstall-service.ps1")
    foreach ($rs in $requiredScripts) {
        if ($scriptItems -notcontains $rs) {
            throw "Missing script: $rs"
        }
    }
    Write-Host "[PASS] All scripts present."

    $exe = Join-Path $tempUnpack "bin\highways.exe"
    if (-not (Test-Path $exe)) {
        throw "highways.exe not found at $exe"
    }

    Write-Host "`n=== 3. Testing highways.exe --version ==="
    & $exe --version
    if ($LASTEXITCODE -ne 0) { throw "--version returned exit code $LASTEXITCODE" }
    Write-Host "[PASS] Version check passed."

    Write-Host "`n=== 4. Testing highways.exe --validate ==="
    $cfg = Join-Path $tempUnpack "config\highway.json"
    & $exe --validate --config $cfg
    if ($LASTEXITCODE -ne 0) { throw "--validate returned exit code $LASTEXITCODE" }
    Write-Host "[PASS] Config validation passed."

    Write-Host "`n=== 5. Testing highways.exe --status ==="
    & $exe --status --service-name "NonExistentServiceVerify"
    if ($LASTEXITCODE -ne 0) { throw "--status returned exit code $LASTEXITCODE" }
    Write-Host "[PASS] Status query passed."

    Write-Host "`n=== ALL VERIFICATIONS PASSED SUCCESSFULLY ==="
}
finally {
    Remove-Item -Path $tempUnpack -Recurse -Force
}
