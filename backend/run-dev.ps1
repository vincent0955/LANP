#Requires -Version 5.1
<#
.SYNOPSIS
    Local dev launch script for the Game Dashboard backend.

.DESCRIPTION
    Runs a few pre-flight checks (kubectl available, cluster reachable) so
    startup problems are diagnosed before dotnet run even attempts to bind,
    then starts the backend. See README.md for full setup instructions.
#>

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$apiProject = Join-Path $scriptRoot "GameDashboard.Api"

Write-Host "== Game Dashboard Backend :: local dev launch ==" -ForegroundColor Cyan

# --- Pre-flight: kubectl present ---
$kubectlExists = Get-Command kubectl -ErrorAction SilentlyContinue
if (-not $kubectlExists) {
    Write-Host "[WARN] kubectl was not found on PATH. The backend can still start, but" -ForegroundColor Yellow
    Write-Host "       it needs a working kubeconfig to talk to a cluster." -ForegroundColor Yellow
}
else {
    # --- Pre-flight: cluster reachable ---
    $null = kubectl get nodes 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[WARN] Kubernetes cluster is not reachable right now." -ForegroundColor Yellow
        Write-Host "       Is Docker Desktop running with Kubernetes enabled?" -ForegroundColor Yellow
        Write-Host "       The backend will still start and report this via GET /api/health." -ForegroundColor Yellow
    }
    else {
        Write-Host "[OK] Kubernetes cluster is reachable." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Starting backend on http://127.0.0.1:5000 ..." -ForegroundColor Cyan
Write-Host "  Health:       http://127.0.0.1:5000/api/health"
Write-Host "  Setup status: http://127.0.0.1:5000/api/setup/status"
Write-Host ""

Push-Location $apiProject
try {
    dotnet run
}
finally {
    Pop-Location
}
