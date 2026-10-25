[CmdletBinding()]
param([string]$Repository = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$version = '8.30.1'
if ([OperatingSystem]::IsWindows() -and [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64') {
    $asset = "gitleaks_${version}_windows_x64.zip"
    $checksum = 'D29144DEFF3A68AA93CED33DDDF84B7FDC26070ADD4AA0F4513094C8332AFC4E'
    $executable = 'gitleaks.exe'
}
elseif ([OperatingSystem]::IsLinux() -and [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64') {
    $asset = "gitleaks_${version}_linux_x64.tar.gz"
    $checksum = '551F6FC83EA457D62A0D98237CBAD105AF8D557003051F41F3E7CA7B3F2470EB'
    $executable = 'gitleaks'
}
else { throw 'The secret-check script currently supports Windows x64 and Linux x64' }

$temporary = Join-Path ([IO.Path]::GetTempPath()) "healthsync-gitleaks-$([Guid]::NewGuid().ToString('N'))"
try {
    $null = New-Item $temporary -ItemType Directory
    $archive = Join-Path $temporary $asset
    Invoke-WebRequest "https://github.com/gitleaks/gitleaks/releases/download/v$version/$asset" -OutFile $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $checksum) { throw 'Gitleaks archive checksum mismatch' }
    if ($asset.EndsWith('.zip')) { Expand-Archive $archive $temporary }
    else {
        & tar -xzf $archive -C $temporary
        if ($LASTEXITCODE -ne 0) { throw 'Gitleaks archive extraction failed' }
    }
    $reportPath = Join-Path $temporary 'findings.json'
    $output = @(& (Join-Path $temporary $executable) git --no-banner --redact=100 --log-opts='--all --full-history' `
        --report-format json --report-path $reportPath $Repository 2>&1)
    if ($LASTEXITCODE -eq 1) {
        $findings = @(Get-Content $reportPath -Raw | ConvertFrom-Json)
        $findings | ForEach-Object { Write-Output "$($_.RuleID): $($_.File):$($_.StartLine)" }
        throw 'Potential credentials found; matched values were not printed'
    }
    if ($LASTEXITCODE -ne 0) { throw 'Gitleaks execution failed; output suppressed' }
    Write-Output "PASS: Gitleaks $version found no secrets in reachable Git history"
}
finally { if (Test-Path $temporary) { Remove-Item $temporary -Recurse -Force } }