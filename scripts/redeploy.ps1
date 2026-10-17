#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Rebuilds the MatSMTP container and redeploys the dev stack (the project's live-reload / test workflow).
.DESCRIPTION
    Always rebuilds the image from the current source and recreates the container; the data volume (database, queue, settings) stays.
    The version of a local build is "local-<builddate>".
.PARAMETER Fresh
    Also deletes the data volume first (clean start, the setup page appears again).
.EXAMPLE
    ./scripts/redeploy.ps1
.EXAMPLE
    ./scripts/redeploy.ps1 -Fresh
#>
param(
    [switch]$Fresh
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:MATSMTP_CHANNEL = 'local'
    $env:MATSMTP_BUILD_DATE = (Get-Date).ToUniversalTime().ToString('yyyyMMdd')

    $compose = @('compose', '-f', 'docker-compose.dev.yml')

    # Windows PowerShell 5.1 turns the progress lines docker writes to stderr into errors; judge by the exit code instead.
    $ErrorActionPreference = 'Continue'

    if ($Fresh) {
        Write-Host 'Removing the stack and its volumes ...' -ForegroundColor Yellow
        docker @compose down -v
    }

    docker @compose up -d --build --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw 'docker compose failed.' }

    Write-Host ''
    Write-Host "MatSMTP local-$($env:MATSMTP_BUILD_DATE) is running on http://localhost:2525" -ForegroundColor Green
    Write-Host 'SMTP: localhost:2025 (STARTTLS), 2587 (submission), 2465 (TLS)  |  sqlite-web: http://localhost:2526  |  Mailpit: http://localhost:2527' -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
