[CmdletBinding()]
param(
    [string]$GoogleConfigurationPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'publish\local-preview\app'
}
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$outputExecutable = Join-Path $outputDirectory 'CodexPulse.exe'
$runningPreview = @(Get-Process -Name CodexPulse -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $outputExecutable })
if ($runningPreview.Count -gt 0) {
    throw 'Close Codex Pulse in the local output folder before rebuilding it.'
}
if ([string]::IsNullOrWhiteSpace($GoogleConfigurationPath)) {
    $GoogleConfigurationPath = Join-Path $projectRoot 'publish\local-preview\state\google-oauth-client.json'
}
$configurationPath = (Resolve-Path -LiteralPath $GoogleConfigurationPath).Path
$configuration = Get-Content -LiteralPath $configurationPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $configuration.installed -or [string]::IsNullOrWhiteSpace($configuration.installed.client_id)) {
    throw 'A desktop Google OAuth configuration is required for this local review build.'
}

# This script only prepares a local review folder. It does not create a release.
Push-Location $projectRoot
try {
    dotnet build UsageRemaining.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -m:1 -t:Rebuild --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Local preview rebuild failed.' }
    dotnet publish src/UsageRemaining.Widget/UsageRemaining.Widget.csproj -c Release -p:Platform=x64 --no-build --no-restore --self-contained false -o $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Local preview publish failed.' }

    # The app already supports configuration beside its EXE, including direct launches.
    $destinationPath = Join-Path $outputDirectory 'google-oauth-client.json'
    if ($configurationPath -ne $destinationPath) {
        Copy-Item -LiteralPath $configurationPath -Destination $destinationPath -Force
    }
    if ((Get-FileHash -LiteralPath $configurationPath).Hash -ne (Get-FileHash -LiteralPath $destinationPath).Hash) {
        throw 'Local Google configuration copy verification failed.'
    }
    Get-Item -LiteralPath $outputExecutable, (Join-Path $outputDirectory 'CodexPulse.dll') |
        Select-Object FullName, Length, LastWriteTimeUtc
}
finally { Pop-Location }
