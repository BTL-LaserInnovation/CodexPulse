[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $projectRoot 'publish\local-preview\review\UsageRemaining.Checks.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Calendar review build is missing. See docs/GoogleCalendar-Local.md.' }
# The local review target is built as WinExe, so only its WPF window is shown.
# Its Google service and Codex data are samples, never a real account connection.
Start-Process -FilePath $executable -ArgumentList '--google-preview' -WorkingDirectory $projectRoot -WindowStyle Normal | Out-Null
