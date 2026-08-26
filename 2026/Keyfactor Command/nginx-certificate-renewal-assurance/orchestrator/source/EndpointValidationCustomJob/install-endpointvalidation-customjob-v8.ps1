$ErrorActionPreference = "Stop"

$SourceRoot = "C:\KeyfactorScripts\EndpointValidationCustomJob"
$BuildOutput = Join-Path $SourceRoot "bin\Release\net10.0"
$InstallRoot = "C:\Program Files\Keyfactor\Keyfactor Orchestrator\extensions\EndpointValidation"
$Timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$BackupRoot = "C:\KeyfactorScripts\EndpointValidation\Backups\EndpointValidationCustomJob-v8-$Timestamp"

New-Item -Path $BackupRoot -ItemType Directory -Force | Out-Null
New-Item -Path $InstallRoot -ItemType Directory -Force | Out-Null

if (Test-Path -LiteralPath $InstallRoot) {
    Copy-Item -Path $InstallRoot -Destination (Join-Path $BackupRoot "InstalledEndpointValidation") -Recurse -Force
}

Write-Host "Building EndpointValidation custom job extension..." -ForegroundColor Cyan
Push-Location $SourceRoot
try {
    dotnet build -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$DllPath = Join-Path $BuildOutput "MatadorTech.Keyfactor.EndpointValidationCustomJob.dll"
$PdbPath = Join-Path $BuildOutput "MatadorTech.Keyfactor.EndpointValidationCustomJob.pdb"
$ManifestPath = Join-Path $SourceRoot "manifest\manifest.json"

if (-not (Test-Path -LiteralPath $DllPath)) {
    throw "Built DLL not found: $DllPath"
}

$Services = Get-Service | Where-Object {
    $_.Name -like "*Keyfactor*" -or $_.DisplayName -like "*Keyfactor*"
}

$OrchestratorService = $Services | Where-Object {
    $_.DisplayName -like "*Orchestrator*" -or $_.Name -like "*Orchestrator*"
} | Select-Object -First 1

if (-not $OrchestratorService) {
    Write-Host "Could not identify the Universal Orchestrator service automatically." -ForegroundColor Yellow
    $Services | Format-Table Name, DisplayName, Status -AutoSize
    throw "Set the service name manually in this script and rerun."
}

Write-Host "Stopping service: $($OrchestratorService.Name)" -ForegroundColor Cyan
Stop-Service -Name $OrchestratorService.Name -Force

Copy-Item -Path $DllPath -Destination $InstallRoot -Force
if (Test-Path -LiteralPath $PdbPath) {
    Copy-Item -Path $PdbPath -Destination $InstallRoot -Force
}
Copy-Item -Path $ManifestPath -Destination (Join-Path $InstallRoot "manifest.json") -Force

Write-Host "Starting service: $($OrchestratorService.Name)" -ForegroundColor Cyan
Start-Service -Name $OrchestratorService.Name

Write-Host "Installed EndpointValidation custom job extension v8." -ForegroundColor Green
Write-Host "Backup: $BackupRoot" -ForegroundColor Green
