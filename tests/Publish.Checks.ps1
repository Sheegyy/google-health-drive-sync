[CmdletBinding()]
param([string]$PublishDirectory = 'artifacts/publish')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "healthsync-publish-$([Guid]::NewGuid().ToString('N'))"
$temporary = Join-Path $temporaryRoot 'private/publish'
try {
    $null = New-Item $temporary -ItemType Directory -Force
    Get-ChildItem $PublishDirectory -Force | Copy-Item -Destination $temporary -Recurse -Force
    & (Join-Path $root 'tools/Test-Publish.ps1') -PublishDirectory $temporary
    $privateDirectory = Join-Path $temporary 'private'
    $null = New-Item $privateDirectory -ItemType Directory
    try {
        [System.IO.File]::WriteAllText((Join-Path $privateDirectory 'unexpected.dll'), 'SYNTHETIC_TEST_VALUE')
        $rejected = $false
        try { $null = & (Join-Path $root 'tools/Test-Publish.ps1') -PublishDirectory $temporary }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'Package validation accepted an embedded private directory' }
    }
    finally { Remove-Item $privateDirectory -Recurse -Force }
    foreach ($name in @('.env', 'google-refresh-token.txt', 'private-key.pem', 'health_sync_latest.json', 'unrelated.json')) {
        $path = Join-Path $temporary $name
        [System.IO.File]::WriteAllText($path, 'SYNTHETIC_TEST_VALUE')
        try {
            $rejected = $false
            try { $null = & (Join-Path $root 'tools/Test-Publish.ps1') -PublishDirectory $temporary }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Package validation accepted a forbidden file' }
        }
        finally { Remove-Item $path -Force }
    }
    $hostPath = Join-Path $temporary 'host.json'
    foreach ($content in @('{"version":"2.0","refresh_token":"SYNTHETIC_TEST_VALUE"}',
        '{"version":"2.0","record_counts":{}}')) {
        [System.IO.File]::WriteAllText($hostPath, $content)
        $rejected = $false
        try { $null = & (Join-Path $root 'tools/Test-Publish.ps1') -PublishDirectory $temporary }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'Package validation accepted sensitive fields in an allowed JSON file' }
    }
    Write-Output 'PASS: publish validation rejects environment files, keys, tokens, health exports and unexpected JSON'
}
finally { if (Test-Path $temporaryRoot) { Remove-Item $temporaryRoot -Recurse -Force } }