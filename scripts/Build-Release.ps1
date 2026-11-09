[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$PackageSource,
    [string]$GoogleConfigurationPath,
    [switch]$RequireGoogleConfiguration,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

function Assert-ReleasePayload {
    param([Parameter(Mandatory = $true)][string]$Directory)

    # Inspect names only: personal credential files must never be read or packaged.
    $root = Get-Item -LiteralPath $Directory -Force
    $pending = New-Object 'Collections.Generic.Stack[string]'
    $pending.Push($root.FullName)
    while ($pending.Count -gt 0) {
        $current = Get-Item -LiteralPath $pending.Pop() -Force
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Release payload must not contain symbolic links or junctions.'
        }
        foreach ($entry in Get-ChildItem -LiteralPath $current.FullName -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Release payload must not contain symbolic links or junctions.'
            }
            $name = $entry.Name
            $privateDirectory = $entry.PSIsContainer -and ($name -in @('personaldata', 'google-auth', 'state', 'preview', 'local-preview') -or $name -like '.env*')
            $privateFile = -not $entry.PSIsContainer -and (
                $name -like '*.dpapi' -or $name -like 'widget-state.json*' -or
                $name -like 'auth.json*' -or $name -like '.env*' -or $name -like 'client_secret_*.json' -or
                $name -like '*token*.json' -or $name -like 'credentials*.json' -or
                ($name -eq 'google-oauth-client.json' -and $entry.DirectoryName -ne $root.FullName))
            if ($privateDirectory -or $privateFile) {
                throw 'Release payload contains a personal state, credential, or preview entry.'
            }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
        }
    }
}

function Get-PublicGoogleConfiguration {
    param([Parameter(Mandatory = $true)][string]$Path)

    try { $configuration = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw 'Google desktop registration must be readable valid JSON.' }
    if ($configuration -isnot [pscustomobject] -or
        $configuration.installed -isnot [pscustomobject] -or
        $null -ne $configuration.web -or $configuration.type -eq 'service_account') {
        throw 'Google registration must describe an installed desktop application.'
    }
    $clientId = $configuration.installed.client_id
    $clientSecret = $configuration.installed.client_secret
    if ($clientId -isnot [string] -or $clientId -notmatch '^\d+-[A-Za-z0-9_-]+\.apps\.googleusercontent\.com$') {
        throw 'Google desktop registration has an invalid client_id.'
    }
    $hasClientSecret = $null -ne $configuration.installed.PSObject.Properties['client_secret']
    if ($hasClientSecret -and ($clientSecret -isnot [string] -or [string]::IsNullOrWhiteSpace($clientSecret))) {
        throw 'Google desktop registration has an invalid client_secret.'
    }
    $installed = [ordered]@{ client_id = $clientId }
    if ($hasClientSecret) { $installed.client_secret = $clientSecret }
    return ([ordered]@{ installed = $installed } | ConvertTo-Json -Depth 3)
}

$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    [xml]$project = Get-Content -LiteralPath 'src/UsageRemaining.Widget/UsageRemaining.Widget.csproj'
    if (-not $Version) { $Version = $project.Project.PropertyGroup.Version | Select-Object -First 1 }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must be a stable three-part version.' }
    if (-not $GoogleConfigurationPath) {
        $defaultGoogleConfiguration = Join-Path $projectRoot 'config/google-desktop-client.json'
        if (Test-Path -LiteralPath $defaultGoogleConfiguration -PathType Leaf) {
            $GoogleConfigurationPath = $defaultGoogleConfiguration
        }
    }
    $publicGoogleConfiguration = $null
    if ($GoogleConfigurationPath) {
        $publicGoogleConfiguration = Get-PublicGoogleConfiguration -Path $GoogleConfigurationPath
    }
    elseif ($RequireGoogleConfiguration) {
        throw 'Google desktop registration is required; supply -GoogleConfigurationPath or config/google-desktop-client.json.'
    }
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "publish/$Version/releases" }
    $installedDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot "publish/$Version/installed"))
    $publishRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'publish')) + [IO.Path]::DirectorySeparatorChar
    if (-not $installedDirectory.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed publish directory must remain inside the project publish directory.'
    }
    $ancestor = $installedDirectory
    while ($ancestor -ne $projectRoot) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Installed publish path must not traverse symbolic links or junctions.'
            }
        }
        $ancestor = Split-Path $ancestor -Parent
    }
    if (-not $SkipPublish) {
        # A fresh publish cannot inherit sidecars or other files left by a preview.
        if (Test-Path -LiteralPath $installedDirectory) {
            Assert-ReleasePayload -Directory $installedDirectory
            Remove-Item -LiteralPath $installedDirectory -Recurse -Force
        }
        $publishArgs = @('publish', 'src/UsageRemaining.Widget', '-c', 'Release', '-p:PublishProfile=Installed', "-p:Version=$Version")
        if ($PackageSource) { $publishArgs += @('--source', $PackageSource) }
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw 'Installed publish failed.' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installedDirectory 'CodexPulse.exe'))) {
        throw 'Installed publish output is missing.'
    }
    Assert-ReleasePayload -Directory $installedDirectory
    $googleSidecar = Join-Path $installedDirectory 'google-oauth-client.json'
    if ($null -ne $publicGoogleConfiguration) {
        [IO.File]::WriteAllText($googleSidecar, $publicGoogleConfiguration, (New-Object Text.UTF8Encoding($false)))
    }
    elseif (Test-Path -LiteralPath $googleSidecar) {
        throw 'Installed output contains a stale Google registration; supply its approved source or republish into a clean directory.'
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
