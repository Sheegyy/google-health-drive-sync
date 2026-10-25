[CmdletBinding()]
param([string]$PublishDirectory = 'artifacts/publish')

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$targetPath = Join-Path $repositoryRoot 'infra/production.example.json'
$settingsPath = Join-Path $repositoryRoot 'infra/production.settings.example.json'
$target = Get-Content $targetPath -Raw | ConvertFrom-Json
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
if ($settings.HEALTH_SYNC_SCHEDULE -ne '0 0 22 * * *' -or $settings.HEALTH_TIMEZONE -ne 'Asia/Tokyo') {
    throw 'Production must run daily at 07:00 JST using the UTC timer schedule'
}
foreach ($settingName in @('GOOGLE_CLIENT_ID', 'GOOGLE_CLIENT_SECRET', 'GOOGLE_REFRESH_TOKEN', 'DRIVE_FILE_ID')) {
    $vaultHost = [regex]::Escape("$($target.keyVault).vault.azure.net")
    if ($settings[$settingName] -notmatch "^@Microsoft.KeyVault\(SecretUri=https://$vaultHost/secrets/[a-z-]+\)$") {
        throw "Production must contain only a Key Vault reference for $settingName"
    }
}
Write-Output 'PASS: production schedule and four Key Vault references'

function az {
    $commandArguments = @($args)
    $testState = $global:HealthSyncDeploymentTestState
    $testState.Calls.Add($commandArguments)
    $global:LASTEXITCODE = 0
    $prefix = ($commandArguments | Select-Object -First 3) -join ' '
    if ($prefix -eq 'functionapp config appsettings') { return 'App settings updated' }
    if ($prefix -eq 'storage blob generate-sas') { return 'sv=test&sig=TEST_SAS_NEVER_PRINT' }
    if ($prefix -eq 'storage blob upload' -or $prefix -eq 'storage blob delete') { return 'Upload complete' }
    if ($commandArguments[0] -eq 'resource' -and $commandArguments[1] -eq 'wait') {
        if ($testState.FailRegistration) {
            $global:LASTEXITCODE = 1
            return 'ERROR: (ResourceNotFound) Timer registration is pending TEST_SAS_NEVER_PRINT'
        }
        return 'Resource exists'
    }
    if ($prefix -eq 'functionapp function list') {
        return '[{"name":"healthsync-functions-example/HealthSyncToDrive","config":{"bindings":[{"type":"timerTrigger"}]}}]'
    }
    if ($commandArguments[0] -ne 'rest') { throw 'Unexpected Azure command in deployment check' }
    $url = $commandArguments[[Array]::IndexOf($commandArguments, '--url') + 1]
    if ($url -match '/extensions/onedeploy\?') {
        $bodyPath = $commandArguments[[Array]::IndexOf($commandArguments, '--body') + 1].Substring(1)
        $testState.BodyPath = $bodyPath
        $body = Get-Content $bodyPath -Raw | ConvertFrom-Json
        if ($body.properties.packageUri -notmatch '/healthsync-source/[a-z0-9_-]+/released-package\.zip\?sv=test&sig=TEST_SAS_NEVER_PRINT$' -or
            $body.properties.remoteBuild -ne $false) { throw 'OneDeploy payload is invalid' }
        if ($testState.FailDeploy) {
            $global:LASTEXITCODE = 1
            return 'ERROR: (Forbidden) Access denied for TEST_SAS_NEVER_PRINT'
        }
        return 'Accepted'
    }
    if ($url -match '/config/configreferences/appsettings\?') {
        $values = foreach ($settingName in @('GOOGLE_CLIENT_ID', 'GOOGLE_CLIENT_SECRET', 'GOOGLE_REFRESH_TOKEN', 'DRIVE_FILE_ID')) {
            @{ name = $settingName; properties = @{ status = if ($testState.MissingSecret -or
                ($testState.NeedsRefresh -and -not $testState.RefreshRequested)) { 'SecretNotFound' } else { 'Resolved' } } }
        }
        return @{ value = @($values) } | ConvertTo-Json -Depth 5
    }
    if ($url -match '/refresh\?') {
        $testState.RefreshRequested = $true
        if (-not $testState.MissingSecret -and -not $testState.NeedsRefresh) {
            $global:LASTEXITCODE = 1
            return 'ERROR: (Conflict) Unnecessary refresh must not block deployment'
        }
        return 'Accepted'
    }
    if ($url -match '/syncfunctiontriggers\?') { return 'Accepted' }
    return @{
        properties = @{
            functionAppConfig = @{
                runtime = @{ name = 'dotnet-isolated'; version = '10.0' }
                deployment = @{ storage = @{ value = "https://$($target.storageAccount).blob.core.windows.net/$($target.deploymentContainer)" } }
            }
        }
    } | ConvertTo-Json -Depth 7
}

foreach ($scenario in @('success', 'refresh-required', 'missing-secret', 'deployment-failure', 'registration-failure')) {
    $global:HealthSyncDeploymentTestState = @{
        Calls = [System.Collections.Generic.List[object]]::new()
        MissingSecret = $scenario -eq 'missing-secret'
        NeedsRefresh = $scenario -eq 'refresh-required'
        RefreshRequested = $false
        FailDeploy = $scenario -eq 'deployment-failure'
        FailRegistration = $scenario -eq 'registration-failure'
        BodyPath = $null
    }
    $failure = $null
    $output = [System.Collections.Generic.List[string]]::new()
    try {
        & (Join-Path $repositoryRoot 'tools/Deploy-Production.ps1') -PublishDirectory $PublishDirectory -TargetPath $targetPath `
            -SettingsPath $settingsPath -ReleaseId "check-$([Guid]::NewGuid().ToString('N'))" |
            ForEach-Object { $output.Add([string]$_) }
    }
    catch { $failure = $_.Exception.Message }
    $visibleOutput = $output | Where-Object { $_ -ne '::add-mask::sv=test&sig=TEST_SAS_NEVER_PRINT' }
    if (($visibleOutput -join "`n") -match 'TEST_SAS_NEVER_PRINT' -or $failure -match 'TEST_SAS_NEVER_PRINT') {
        throw 'Deployment leaked a signed URL into its output'
    }
    if ($scenario -ne 'missing-secret' -and $env:GITHUB_ACTIONS -eq 'true' -and
        $output -notcontains '::add-mask::sv=test&sig=TEST_SAS_NEVER_PRINT') {
        throw 'GitHub Actions must mask the temporary package URL'
    }
    if ($output.Count -eq 0) { throw 'Checks must retain output emitted before an expected deployment failure' }
    if ($scenario -in @('success', 'refresh-required') -and $failure) { throw $failure }
    if ($scenario -notin @('success', 'refresh-required') -and -not $failure) { throw "Expected failure for $scenario" }
    if ($scenario -in @('success', 'deployment-failure', 'registration-failure') -and
        $global:HealthSyncDeploymentTestState.RefreshRequested) { throw 'Resolved references must not call the refresh API' }
    if ($scenario -in @('refresh-required', 'missing-secret') -and
        -not $global:HealthSyncDeploymentTestState.RefreshRequested) { throw 'Unresolved references must be refreshed and rechecked' }
    if ($scenario -eq 'deployment-failure' -and
        ($failure -notmatch 'rest put' -or $failure -notmatch '/extensions/onedeploy' -or $failure -notmatch 'code Forbidden')) {
        throw 'A deployment failure must identify its operation and allowlisted error code without printing the signed URL'
    }
    if ($scenario -eq 'missing-secret' -and ($global:HealthSyncDeploymentTestState.Calls | Where-Object { ($_ | Select-Object -First 3) -join ' ' -eq 'storage blob upload' })) {
        throw 'Missing secrets must stop deployment before uploading a package'
    }
    if ($global:HealthSyncDeploymentTestState.BodyPath -and (Test-Path $global:HealthSyncDeploymentTestState.BodyPath)) { throw 'Temporary signed deployment body was not removed' }
    if ($global:HealthSyncDeploymentTestState.Calls | Where-Object { $_ -contains '--account-key' -or $_ -contains '--connection-string' }) {
        throw 'Deployment must not use a storage account key or connection string'
    }
    if ($global:HealthSyncDeploymentTestState.Calls | Where-Object { ($_ | Select-Object -First 3) -join ' ' -eq 'storage blob delete' }) {
        throw 'The source package must remain available until the active deployment revision is verified independently'
    }
    if ($scenario -eq 'registration-failure' -and
        ($global:HealthSyncDeploymentTestState.Calls | Where-Object { ($_ | Select-Object -First 3) -join ' ' -eq 'functionapp function list' })) {
        throw 'A pending registration must not reach the final success check'
    }
    Write-Output "PASS: deployment scenario $scenario; no credentials printed; temporary signed body removed"
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) "healthsync-invalid-config-$([Guid]::NewGuid().ToString('N'))"
try {
    $null = New-Item $temporary -ItemType Directory
    foreach ($scenario in @('invalid-subscription', 'plaintext-google-secret')) {
        $invalidTarget = Get-Content $targetPath -Raw | ConvertFrom-Json -AsHashtable
        $invalidSettings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
        if ($scenario -eq 'invalid-subscription') { $invalidTarget.subscriptionId = 'not-a-subscription' }
        else { $invalidSettings.GOOGLE_CLIENT_SECRET = 'SYNTHETIC_TEST_VALUE' }
        $invalidTargetPath = Join-Path $temporary 'target.json'
        $invalidSettingsPath = Join-Path $temporary 'settings.json'
        [IO.File]::WriteAllText($invalidTargetPath, ($invalidTarget | ConvertTo-Json))
        [IO.File]::WriteAllText($invalidSettingsPath, ($invalidSettings | ConvertTo-Json))
        $global:HealthSyncDeploymentTestState = @{Calls=[Collections.Generic.List[object]]::new()}
        $rejected = $false
        try {
            $null = & (Join-Path $repositoryRoot 'tools/Deploy-Production.ps1') -PublishDirectory $PublishDirectory `
                -TargetPath $invalidTargetPath -SettingsPath $invalidSettingsPath
        }
        catch { $rejected = $true }
        if (-not $rejected -or $global:HealthSyncDeploymentTestState.Calls.Count -ne 0) {
            throw 'Invalid configuration must stop before any Azure operation'
        }
        Write-Output "PASS: $scenario rejected before any Azure operation"
    }
}
finally { if (Test-Path $temporary) { Remove-Item $temporary -Recurse -Force } }
Remove-Variable HealthSyncDeploymentTestState -Scope Global
$global:LASTEXITCODE = 0