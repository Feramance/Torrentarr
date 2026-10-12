param(
    [Parameter(Mandatory = $true)]
    [string]$Binary,
    [Parameter(Mandatory = $true)]
    [string]$WorkerBinary,
    [int]$Port = 6969
)

$ErrorActionPreference = 'Stop'
$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("torrentarr-smoke-" + [guid]::NewGuid().ToString('N'))
$app = Join-Path $workDir 'torrentarr.exe'
$config = Join-Path $workDir 'config.toml'
$database = Join-Path $workDir 'torrentarr.db'
$output = Join-Path $workDir 'torrentarr.log'
$errorOutput = Join-Path $workDir 'torrentarr.err.log'
$process = $null
$stub = $null
$worker = Join-Path $workDir 'torrentarr-worker.exe'
$stubPort = Join-Path $workDir 'arr-port'
$workerPid = $null

New-Item -ItemType Directory -Path $workDir | Out-Null
try {
    Copy-Item -LiteralPath $Binary -Destination $app
    Copy-Item -LiteralPath $WorkerBinary -Destination $worker
    $stub = Start-Process -FilePath 'node' -ArgumentList @(('"' + (Join-Path $PSScriptRoot 'smoke-arr.mjs') + '"'), ('"' + $stubPort + '"')) -PassThru
    for ($attempt = 0; $attempt -lt 60 -and -not (Test-Path $stubPort); $attempt++) { Start-Sleep -Milliseconds 100 }
    $arrPort = Get-Content -LiteralPath $stubPort -Raw
    New-Item -ItemType File -Path $database | Out-Null
    @"
[Settings]
ConfigVersion = "6.14.6"
FFprobeAutoUpdate = false
LoopSleepTimer = 5
FailedCategory = "failed"
RecheckCategory = "recheck"
PingURLS = ["http://127.0.0.1:$arrPort"]

[WebUI]
Host = "127.0.0.1"
Port = $Port
Token = "standalone-smoke-token"
AuthDisabled = true
LocalAuthEnabled = false
OIDCEnabled = false
LiveArr = false
[qBit]
Disabled = true
[Radarr-Smoke]
Managed = true
URI = "http://127.0.0.1:$arrPort"
APIKey = "smoke"
Category = "smoke"
SearchOnly = true
[Radarr-Smoke.Search]
SearchMissing = false
"@ | Set-Content -LiteralPath $config -Encoding utf8

    $oldWorker = $env:TORRENTARR_WORKER_PATH
    $env:TORRENTARR_WORKER_PATH = $worker
    $oldConfig = $env:TORRENTARR_CONFIG
    $oldExtract = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    $env:TORRENTARR_CONFIG = $config
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $workDir 'extract'
    try {
        & $app --repair-database
        if ($LASTEXITCODE -ne 0) { throw "Database repair failed with exit code $LASTEXITCODE" }

        $process = Start-Process -FilePath $app -RedirectStandardOutput $output -RedirectStandardError $errorOutput -PassThru
        $baseUrl = "http://127.0.0.1:$Port"
        $healthy = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try {
                $health = Invoke-WebRequest -Uri "$baseUrl/health" -UseBasicParsing
                if ($health.StatusCode -eq 200) { $healthy = $true; break }
            } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $healthy) {
            $diagnostics = @(Get-Content $output, $errorOutput -Raw -ErrorAction SilentlyContinue) -join "`n"
            throw "Standalone executable did not become healthy. $diagnostics"
        }

        $health = Invoke-WebRequest -Uri "$baseUrl/health" -UseBasicParsing
        if ($health.Content -notmatch '"healthy"') { throw 'Health endpoint did not report healthy.' }
        $index = (Invoke-WebRequest -Uri "$baseUrl/ui/" -UseBasicParsing).Content
        if ($index -notmatch '<html') { throw 'Embedded UI did not return HTML.' }
        if ($index -notmatch 'src="(\/assets\/[^" ]+\.js)"') { throw 'No hashed WebUI asset found.' }
        Invoke-WebRequest -Uri "$baseUrl$($matches[1])" -UseBasicParsing | Out-Null
        $openApi = Invoke-WebRequest -Uri "$baseUrl/api/openapi.json" -Headers @{ Authorization = 'Bearer standalone-smoke-token' } -UseBasicParsing
        $null = $openApi.Content | ConvertFrom-Json
        $statusPath = Join-Path $workDir 'workers/Radarr-Smoke.json'
        $status = $null
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if (Test-Path $statusPath) {
                $status = Get-Content $statusPath -Raw | ConvertFrom-Json
                if ($status.torrent.lastSuccess -and $status.search.lastSuccess) { break }
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $status.torrent.lastSuccess -or -not $status.search.lastSuccess) { throw 'Worker did not complete both processing loops' }
        if ($status.instance -ne 'Radarr-Smoke' -or $status.pid -eq $process.Id -or -not $status.torrent -or -not $status.search) { throw 'Invalid worker heartbeat' }
        $workerPid = $status.pid
        Invoke-WebRequest -Method Post -Uri "$baseUrl/web/processes/Radarr-Smoke/worker/restart" -UseBasicParsing | Out-Null
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $status = Get-Content $statusPath -Raw | ConvertFrom-Json
            if ($status.pid -ne $workerPid -and $status.torrent.lastSuccess -and $status.search.lastSuccess) { break }
            Start-Sleep -Milliseconds 500
        }
        if ($status.pid -eq $workerPid -or -not $status.torrent.lastSuccess -or -not $status.search.lastSuccess -or (Get-Process -Id $workerPid -ErrorAction SilentlyContinue)) { throw 'Worker restart failed' }
        $workerPid = $status.pid
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
        for ($attempt = 0; $attempt -lt 60 -and (Get-Process -Id $workerPid -ErrorAction SilentlyContinue); $attempt++) { Start-Sleep -Milliseconds 500 }
        if (Get-Process -Id $workerPid -ErrorAction SilentlyContinue) { throw 'Worker survived Host shutdown' }
    } finally {
        if ($null -ne $oldWorker) { $env:TORRENTARR_WORKER_PATH = $oldWorker } else { Remove-Item Env:TORRENTARR_WORKER_PATH -ErrorAction SilentlyContinue }
        if ($null -ne $oldConfig) { $env:TORRENTARR_CONFIG = $oldConfig } else { Remove-Item Env:TORRENTARR_CONFIG -ErrorAction SilentlyContinue }
        if ($null -ne $oldExtract) { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $oldExtract } else { Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue }
    }
} finally {
    if ($null -ne $process) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.Dispose()
    }
    if ($null -ne $workerPid) { Stop-Process -Id $workerPid -Force -ErrorAction SilentlyContinue }
    if ($null -ne $stub) { Stop-Process -Id $stub.Id -Force -ErrorAction SilentlyContinue; $stub.Dispose() }
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
