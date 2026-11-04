[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$PackageSource,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    [xml]$project = Get-Content -LiteralPath 'src/UsageRemaining.Widget/UsageRemaining.Widget.csproj'
    if (-not $Version) { $Version = $project.Project.PropertyGroup.Version | Select-Object -First 1 }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must be a stable three-part version.' }
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "publish/$Version/releases" }
    $installedDirectory = Join-Path $projectRoot "publish/$Version/installed"
    if (-not $SkipPublish) {
        $publishArgs = @('publish', 'src/UsageRemaining.Widget', '-c', 'Release', '-p:PublishProfile=Installed', "-p:Version=$Version")
        if ($PackageSource) { $publishArgs += @('--source', $PackageSource) }
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw 'Installed publish failed.' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installedDirectory 'CodexPulse.exe'))) {
        throw 'Installed publish output is missing.'
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'third-party/Velopack-LICENSE.txt') -Destination (Join-Path $installedDirectory 'Velopack-LICENSE.txt')
    $vpkPath = Join-Path $projectRoot 'artifacts/tools/vpk.exe'
    if (-not (Test-Path -LiteralPath $vpkPath)) {
        & dotnet tool install vpk --tool-path (Split-Path $vpkPath -Parent) --version 1.2.161
        if ($LASTEXITCODE -ne 0) { throw 'Velopack CLI installation failed.' }
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $packArgs = @('pack', '--packId', 'BTL.CodexPulse', '--packVersion', $Version,
        '--packDir', $installedDirectory, '--mainExe', 'CodexPulse.exe', '--packTitle', 'Codex Pulse',
        '--packAuthors', 'BTL-LaserInnovation', '--runtime', 'win-x64', '--channel', 'win',
        '--framework', 'net10.0-x64-desktop', '--icon', 'src/UsageRemaining.Widget/Assets/CodexPulse.ico',
        '--outputDir', $OutputDirectory, '--delta', 'None', '--yes', '--skip-updates')
    $notes = Join-Path $projectRoot "release-notes/$Version.md"
    if (Test-Path -LiteralPath $notes) { $packArgs += @('--releaseNotes', $notes) }
    & $vpkPath @packArgs
    if ($LASTEXITCODE -ne 0) { throw 'Release packaging failed.' }
    $setup = Join-Path $OutputDirectory 'BTL.CodexPulse-win-Setup.exe'
    if (-not (Test-Path -LiteralPath $setup)) { throw "Installer is missing: $setup" }
    Write-Output "Release ${Version}: $OutputDirectory"
}
finally { Pop-Location }
