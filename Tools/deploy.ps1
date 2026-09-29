<#
    deploy.ps1 - Build AltStable Companion and put it where it can be run from.

    Usage:
        pwsh Tools/deploy.ps1
        pwsh Tools/deploy.ps1 -Shortcut
        pwsh Tools/deploy.ps1 -Destination "D:\Apps\AltStableCompanion"

    One file is deployed: AltStableCompanion.exe, self-contained, so the folder
    needs nothing else and can be moved or deleted as it is. The app's settings
    and log are NOT in it - they are in %APPDATA%\AltStableCompanion - so
    deploying again changes the program and nothing the player chose.

    -Shortcut adds "AltStable Companion" to the Start menu. It does not make the
    app start with Windows: that is deliberately not offered yet.

    The app is not started. Started without arguments it finds the real game
    and converts there at once; that is for whoever runs it to decide.
#>

param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "Programs\AltStableCompanion"),
    [switch]$Shortcut
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot "src\AltStableCompanion.App"
$Exe = "AltStableCompanion.exe"

# A running copy holds its exe open, and may be in the middle of a pass. It is
# not killed from here: Quit waits for the pass, a kill does not.
$running = Get-Process -Name "AltStableCompanion" -ErrorAction SilentlyContinue
if ($running) {
    throw "AltStable Companion is running (process $($running.Id -join ', ')). Quit it from its tray icon, then deploy again."
}

# Published into the repo's own publish\ folder (ignored by git), then the exe
# alone is copied: dotnet publish leaves .pdb files beside it.
$stage = Join-Path $RepoRoot "publish\win-x64"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

Write-Host "Building AltStable Companion ..." -ForegroundColor Cyan
dotnet publish $Project -c Release -r win-x64 -o $stage --nologo -v q -warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$built = Join-Path $stage $Exe
if (-not (Test-Path $built)) { throw "The build did not produce $Exe" }

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$target = Join-Path $Destination $Exe
Copy-Item $built $target -Force

if ($Shortcut) {
    $programs = [Environment]::GetFolderPath("Programs")
    $link = Join-Path $programs "AltStable Companion.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($link)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = $Destination
    $lnk.Description = "Turns AltStable's portrait captures into the cutouts the Roster draws"
    $lnk.Save()
    Write-Host "Start menu: $link"
}

$file = Get-Item $target
$version = $file.VersionInfo.ProductVersion
Write-Host ("Deployed {0} ({1:N0} MB) to {2}" -f $version, ($file.Length / 1MB), $target) -ForegroundColor Green
Write-Host "Run it:    & `"$target`""
Write-Host "In a copy: & `"$target`" --wow-dir <flavour folder> --data-dir <folder>"
