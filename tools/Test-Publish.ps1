[CmdletBinding()]
param([string]$PublishDirectory = 'artifacts/publish')

$ErrorActionPreference = 'Stop'
$publishPath = (Resolve-Path $PublishDirectory).Path
$requiredFiles = @(
    'host.json', 'worker.config.json', 'extensions.json',
    'HealthSync.Functions.dll', 'HealthSync.Functions.runtimeconfig.json',
    'HealthSync.Core.dll', 'Microsoft.Azure.Functions.Worker.Extensions.Timer.dll'
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path (Join-Path $publishPath $requiredFile) -PathType Leaf)) {
        throw "Publish output is missing $requiredFile"
    }
}
if (-not (Test-Path (Join-Path $publishPath '.azurefunctions') -PathType Container)) {
    throw 'Publish output is missing the Functions host extensions directory'
}
$files = @(Get-ChildItem $publishPath -Recurse -Force -File)
$forbidden = $files | Where-Object {
    $relative = [System.IO.Path]::GetRelativePath($publishPath, $_.FullName)
    $_.Name -match '^local\.settings.*\.json$|^google-client.*\.json$|^client_secret.*\.json$|^\.env($|\.)|\.(pfx|p12|key|pem|crt|cer)$|^google-refresh-token\.|^health_sync_latest\.' -or
    $relative -match '(^|[/\\])private[/\\]'
}
if ($forbidden) { throw 'Publish output contains private configuration or credentials' }
foreach ($file in $files) {
    if ($file.Extension -eq '.json') {
        if ($file.Name -notin @('host.json', 'worker.config.json', 'extensions.json', 'functions.metadata') -and
            $file.Name -notmatch '\.(deps|runtimeconfig)\.json$') {
            throw 'Publish output contains an unexpected JSON file'
        }
        $text = [System.IO.File]::ReadAllText($file.FullName)
        if ($text -match '"(?:access_token|refresh_token|client_secret)"\s*:\s*"[^"\s]+' -or
            $text -match '"(?:query_completed_at_utc|record_counts|collection_statuses)"\s*:') {
            throw 'Publish output contains credential or health-data fields'
        }
    }
    elseif ($file.Name -ne 'HealthSync.Functions' -and $file.Extension -notin @('.dll', '.pdb')) {
        throw 'Publish output contains an unexpected file type'
    }
}
$worker = Get-Content (Join-Path $publishPath 'worker.config.json') -Raw | ConvertFrom-Json
if ($worker.description.language -ne 'dotnet-isolated' -or $worker.description.workerIndexing -ne 'true') {
    throw 'Publish output must use the isolated worker with generated worker indexing'
}
$runtime = Get-Content (Join-Path $publishPath 'HealthSync.Functions.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.tfm -ne 'net10.0') { throw 'Publish output must target .NET 10' }
Write-Output 'PASS: .NET 10 isolated publish output includes host extensions and excludes private configuration'