#Requires -Version 7
<#
.SYNOPSIS
Publishes AltStableCompanion.exe into a stage folder: the one build both deploy.ps1 and
Release.ps1 ship, so what the owner runs and what a player downloads are the same build.

.PARAMETER Stage
The folder the publish output goes to. Emptied first; the caller owns it.

.PARAMETER Revision
What the exe says it was built from (SourceRevisionId); nothing when unknown.

.PARAMETER GoogleClient
The app's Google OAuth client, the JSON the Cloud console downloads (#82). Embedded in the exe,
never in the repo; without it the exe has no Google Sheet export.

.OUTPUTS
The path of the built exe.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Stage,
    [string]$Revision = "",
    [string]$GoogleClient = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot "src\AltStableCompanion.App\AltStableCompanion.App.csproj"

if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
$publish = @($Project, "-c", "Release", "-r", "win-x64", "-o", $Stage, "--nologo", "-v", "q", "-warnaserror")
if ($Revision) { $publish += "-p:SourceRevisionId=$Revision" }
if ($GoogleClient) {
    if (-not (Test-Path -LiteralPath $GoogleClient -PathType Leaf)) { throw "The Google client '$GoogleClient' does not exist." }
    $publish += "-p:GoogleClientJson=$([IO.Path]::GetFullPath($GoogleClient))"
}
dotnet publish @publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$built = Join-Path $Stage "AltStableCompanion.exe"
if (-not (Test-Path -LiteralPath $built)) { throw "The build did not produce AltStableCompanion.exe" }
$built
