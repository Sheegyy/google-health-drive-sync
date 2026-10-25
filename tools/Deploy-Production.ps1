[CmdletBinding()]
param(
    [string]$PublishDirectory = 'artifacts/publish',
    [string]$TargetPath = 'infra/production.json',
    [string]$SettingsPath = 'infra/production.settings.json',
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$ReleaseId = [Guid]::NewGuid().ToString('N')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$targetPath = if ([IO.Path]::IsPathRooted($TargetPath)) { $TargetPath } else { Join-Path $repositoryRoot $TargetPath }
$settingsPath = if ([IO.Path]::IsPathRooted($SettingsPath)) { $SettingsPath } else { Join-Path $repositoryRoot $SettingsPath }
$target = Get-Content $targetPath -Raw | ConvertFrom-Json
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
foreach ($identifier in @($target.subscriptionId, $target.tenantId)) {
    if ($identifier -notmatch '^[0-9a-fA-F-]{36}$' -or -not [Guid]::TryParse($identifier, [ref][Guid]::Empty)) {
        throw 'Deployment subscription and tenant must be GUID identifiers'
    }
}
foreach ($name in @('resourceGroup', 'functionApp', 'keyVault', 'storageAccount', 'deploymentContainer')) {
    if (-not $target.$name -or $target.$name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{1,89}$') { throw "Invalid deployment target field: $name" }
}
foreach ($entry in @{GOOGLE_CLIENT_ID='google-client-id';GOOGLE_CLIENT_SECRET='google-client-secret';
    GOOGLE_REFRESH_TOKEN='google-refresh-token';DRIVE_FILE_ID='drive-file-id'}.GetEnumerator()) {
    $expected = "@Microsoft.KeyVault(SecretUri=https://$($target.keyVault).vault.azure.net/secrets/$($entry.Value))"
    if ($settings[$entry.Key] -cne $expected) { throw "Expected a versionless Key Vault reference for $($entry.Key)" }
}
& (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $PublishDirectory

function Invoke-Azure {
    param([string[]]$CommandArguments, [switch]$Text, [switch]$NoResult)
    $format = if ($Text) { 'tsv' } else { 'json' }
    $captured = @(& az @CommandArguments --only-show-errors --output $format 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $exitCode = $LASTEXITCODE
        $operation = ($CommandArguments | Select-Object -First 3) -join ' '
        if ($CommandArguments[0] -eq 'rest') {
            $method = $CommandArguments[[Array]::IndexOf($CommandArguments, '--method') + 1]
            $uri = [Uri]$CommandArguments[[Array]::IndexOf($CommandArguments, '--url') + 1]
            $operation = "rest $method $($uri.AbsolutePath)"
        }
        $errorText = $captured -join "`n"
        $code = @('AuthorizationFailed', 'Forbidden', 'Unauthorized', 'ResourceNotFound', 'RequestDisallowedByPolicy',
            'InvalidApiVersionParameter', 'DeploymentFailed', 'IpForbidden', 'InvalidAuthenticationToken', 'BadRequest') |
            Where-Object { $errorText -match "\b$([regex]::Escape($_))\b" } | Select-Object -First 1
        if (-not $code) { $code = 'Unclassified' }
        throw "Azure operation '$operation' failed (exit $exitCode; code $code). Sensitive output was suppressed."
    }
    if ($NoResult) { return }
    $content = ($captured -join "`n").Trim()
    if ($Text) { return $content }
    if ($content) { return ConvertFrom-Json -InputObject $content -AsHashtable }
}

$siteId = "/subscriptions/$($target.subscriptionId)/resourceGroups/$($target.resourceGroup)/providers/Microsoft.Web/sites/$($target.functionApp)"
$armSite = "https://management.azure.com$siteId"
$site = Invoke-Azure -CommandArguments @('rest', '--method', 'get', '--url', "${armSite}?api-version=2024-04-01", '--subscription', $target.subscriptionId)
$runtime = $site.properties.functionAppConfig.runtime
if ($runtime.name -ne 'dotnet-isolated' -or $runtime.version -ne '10.0') {
    throw 'The existing app must be Flex Consumption with dotnet-isolated 10.0; no runtime or plan was changed'
}
$containerUri = "https://$($target.storageAccount).blob.core.windows.net/$($target.deploymentContainer)"
if ($site.properties.functionAppConfig.deployment.storage.value.TrimEnd('/') -ne $containerUri) {
    throw 'The configured deployment storage does not match the existing Function App'
}
$null = Invoke-Azure -NoResult -CommandArguments @(
    'functionapp', 'config', 'appsettings', 'set', '--subscription', $target.subscriptionId,
    '--resource-group', $target.resourceGroup, '--name', $target.functionApp, '--settings', "@$settingsPath"
)
function Get-UnresolvedReferences {
    $references = Invoke-Azure -CommandArguments @('rest', '--method', 'get', '--url', "${armSite}/config/configreferences/appsettings?api-version=2022-03-01", '--subscription', $target.subscriptionId)
    $referenceStatus = @{}
    foreach ($reference in $references.value) {
        $referenceStatus[($reference.name -split '/')[-1]] = $reference.properties.status
    }
    foreach ($settingName in $settings.Keys) {
        if ($settings[$settingName].StartsWith('@Microsoft.KeyVault(')) {
            $status = $referenceStatus[$settingName]
            if ($status -ne 'Resolved') { "${settingName}: $status" }
        }
    }
}
$unresolved = @(Get-UnresolvedReferences)
if ($unresolved) {
    $null = Invoke-Azure -NoResult -CommandArguments @('rest', '--method', 'post', '--url', "${armSite}/config/configreferences/appsettings/refresh?api-version=2022-03-01", '--body', '{}', '--subscription', $target.subscriptionId)
    $unresolved = @(Get-UnresolvedReferences)
}
if ($unresolved) {
    throw "Register the four Key Vault entries and check the Function identity's secret-read permission before deploying: $($unresolved -join '; ')"
}

$publishPath = (Resolve-Path $PublishDirectory).Path
$releasePath = Join-Path (Split-Path $publishPath -Parent) "releases/$ReleaseId"
$null = New-Item $releasePath -ItemType Directory -Force
$packagePath = Join-Path $releasePath 'released-package.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $packagePath)
$blobName = "healthsync-source/$ReleaseId/released-package.zip"
$null = Invoke-Azure -NoResult -CommandArguments @(
    'storage', 'blob', 'upload', '--auth-mode', 'login', '--account-name', $target.storageAccount,
    '--container-name', $target.deploymentContainer, '--name', $blobName, '--file', $packagePath,
    '--overwrite', 'false', '--subscription', $target.subscriptionId
)
$expiry = [DateTimeOffset]::UtcNow.AddMinutes(30).ToString("yyyy-MM-dd'T'HH:mm'Z'")
$sas = Invoke-Azure -Text -CommandArguments @(
    'storage', 'blob', 'generate-sas', '--auth-mode', 'login', '--as-user', '--https-only',
    '--account-name', $target.storageAccount, '--container-name', $target.deploymentContainer,
    '--name', $blobName, '--permissions', 'r', '--expiry', $expiry, '--subscription', $target.subscriptionId
)
if (-not $sas -or $sas -notmatch '(^|&)sig=') { throw 'A short-lived package read URL could not be generated' }
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::add-mask::$sas" }
$bodyFile = [System.IO.Path]::GetTempFileName()
try {
    $body = @{ properties = @{ packageUri = "$containerUri/${blobName}?$sas"; remoteBuild = $false } }
    [System.IO.File]::WriteAllText($bodyFile, ($body | ConvertTo-Json -Depth 4))
    $null = Invoke-Azure -NoResult -CommandArguments @(
        'rest', '--method', 'put', '--url', "${armSite}/extensions/onedeploy?api-version=2024-04-01",
        '--body', "@$bodyFile", '--headers', 'Content-Type=application/json', '--subscription', $target.subscriptionId
    )
}
finally {
    Remove-Item $bodyFile -Force
    $sas = $null
    $body = $null
}
$null = Invoke-Azure -NoResult -CommandArguments @(
    'resource', 'wait', '--ids', "$siteId/functions/HealthSyncToDrive", '--exists',
    '--api-version', '2024-04-01', '--interval', '5', '--timeout', '300', '--subscription', $target.subscriptionId
)
$null = Invoke-Azure -NoResult -CommandArguments @('rest', '--method', 'post', '--url', "${armSite}/syncfunctiontriggers?api-version=2024-04-01", '--body', '{}', '--subscription', $target.subscriptionId)
$functions = @(Invoke-Azure -CommandArguments @('functionapp', 'function', 'list', '--subscription', $target.subscriptionId, '--resource-group', $target.resourceGroup, '--name', $target.functionApp))
$timer = @($functions | Where-Object { ($_.name -split '/')[-1] -eq 'HealthSyncToDrive' })
if ($timer.Count -ne 1 -or -not ($timer[0].config.bindings | Where-Object type -EQ 'timerTrigger')) {
    throw 'The deployment request completed but HealthSyncToDrive is not registered as a timer; inspect Azure deployment diagnostics'
}
Write-Output "OneDeploy request accepted: $ReleaseId; HealthSyncToDrive registration is verified for 07:00 JST. The private source package is retained; Google data access and the active package revision require a separate runtime check."