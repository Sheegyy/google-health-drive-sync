[CmdletBinding()]
param([string]$Project = 'tests/HealthSync.Checks')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$output = @(& dotnet list $Project package --vulnerable --include-transitive --format json --no-restore 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability lookup failed; do not treat it as a clean audit' }
$report = ($output -join "`n") | ConvertFrom-Json
if ($report.problems) { throw 'NuGet reported advisory lookup problems; audit could not be completed' }
$count = 0
foreach ($projectResult in $report.projects) {
    foreach ($framework in $projectResult.frameworks) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            foreach ($vulnerability in $package.vulnerabilities) {
                $count++
                Write-Output "$($package.id) $($package.resolvedVersion): $($vulnerability.severity) $($vulnerability.advisoryurl)"
            }
        }
    }
}
if ($count -gt 0) { throw 'Packages with known vulnerabilities must be reviewed before release' }
Write-Output 'PASS: no known NuGet vulnerabilities found, including transitive dependencies'