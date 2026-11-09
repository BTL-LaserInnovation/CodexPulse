[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$appDirectory = Join-Path $projectRoot 'publish\local-preview\app'
$executable = Join-Path $appDirectory 'CodexPulse.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Local preview build is missing. See docs/GoogleCalendar-Local.md.' }
$dataDirectory = Join-Path $projectRoot 'publish\local-preview\state'
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
$previousData = $env:USAGEREMAINING_DATA_DIR
try {
    # Keep the installed release's preferences and single-instance scope separate.
    $env:USAGEREMAINING_DATA_DIR = $dataDirectory
    Start-Process -FilePath $executable -WorkingDirectory $appDirectory -WindowStyle Normal | Out-Null
}
finally { $env:USAGEREMAINING_DATA_DIR = $previousData }
