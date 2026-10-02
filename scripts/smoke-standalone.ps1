param(
    [Parameter(Mandatory = $true)]
    [string]$Binary,
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

New-Item -ItemType Directory -Path $workDir | Out-Null
try {
    Copy-Item -LiteralPath $Binary -Destination $app
    New-Item -ItemType File -Path $database | Out-Null
    @"
[Settings]
ConfigVersion = "6.12.4"
LoopSleepTimer = 5
FailedCategory = "failed"
RecheckCategory = "recheck"
PingURLS = ["one.one.one.one"]

[WebUI]
Host = "127.0.0.1"
Port = $Port
Token = "standalone-smoke-token"
AuthDisabled = true
LocalAuthEnabled = false
OIDCEnabled = false
LiveArr = false
"@ | Set-Content -LiteralPath $config -Encoding utf8

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
    } finally {
        if ($null -ne $oldConfig) { $env:TORRENTARR_CONFIG = $oldConfig } else { Remove-Item Env:TORRENTARR_CONFIG -ErrorAction SilentlyContinue }
        if ($null -ne $oldExtract) { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $oldExtract } else { Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue }
    }
} finally {
    if ($null -ne $process) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.Dispose()
    }
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
