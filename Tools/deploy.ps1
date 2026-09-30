#Requires -Version 7
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

    -Shortcut adds "AltStable Companion" to the Start menu, or points the one
    that is there at this deployment. It does not make the app start with
    Windows: that is deliberately not offered yet.

    The app is not started. Started without arguments it finds the real game
    and converts there at once; that is for whoever runs it to decide.
#>

param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "Programs\AltStableCompanion"),
    [switch]$Shortcut,
    # Where the shortcut goes. The Start menu, unless somebody is trying the script out.
    [string]$ShortcutFolder = [Environment]::GetFolderPath("Programs")
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Exe = "AltStableCompanion.exe"

# Full paths, once, by PowerShell's own rules. A relative one means one folder to
# Copy-Item and another to the shortcut, which is resolved by somebody else.
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
$ShortcutFolder = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ShortcutFolder)
$target = Join-Path $Destination $Exe
$stage = Join-Path $RepoRoot "publish\win-x64"
$link = Join-Path $ShortcutFolder "AltStable Companion.lnk"

# The stage is emptied before the build and removed after the copy. A folder the caller
# named must survive the script: neither it nor the shortcut's folder may be the stage,
# or be inside it.
function Test-InStage([string]$path) {
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($path))
    $in = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($stage))
    return [string]::Equals($full, $in, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($in + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
foreach ($named in @($Destination, $ShortcutFolder)) {
    if (Test-InStage $named) {
        throw "$named is the folder this script builds in ($stage), which it empties and removes. Name another."
    }
}

# The copy that is being replaced holds its exe open, and may be in the middle of a
# pass. It is not killed from here: Quit waits for the pass, a kill does not.
# Only THAT copy: one run from bin\Debug against a copy of the game is in nobody's way.
function Assert-NotRunning {
    $inTheWay = Get-Process -Name "AltStableCompanion" -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and (
            [string]::Equals($_.Path, $target, [StringComparison]::OrdinalIgnoreCase) -or
            $_.Path.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
    }
    if ($inTheWay) {
        throw "AltStable Companion is running from $($inTheWay[0].Path) (process $($inTheWay.Id -join ', ')). Quit it from its tray icon, then deploy again."
    }
}

Assert-NotRunning

# What the exe says it was built from. A tree with changes that are in no commit is
# not the commit it stands on, and the exe says so.
$revision = $null
$dirty = $false
try {
    $revision = (git -C $RepoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $revision = $null }
    elseif (git -C $RepoRoot status --porcelain 2>$null) { $dirty = $true; $revision = "$revision-dirty" }
} catch {
    $revision = $null
}

Write-Host "Building AltStable Companion ..." -ForegroundColor Cyan
# The one build: Release.ps1 ships the same one.
$built = & (Join-Path $PSScriptRoot "Publish-Exe.ps1") -Stage $stage -Revision ($revision ?? "")

# The build took its time: look again. Then the new exe is copied in under another
# name and moved over the old one, so that a copy cut short leaves the old one whole.
Assert-NotRunning
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$incoming = "$target.new"
try {
    Copy-Item -LiteralPath $built -Destination $incoming -Force
    Move-Item -LiteralPath $incoming -Destination $target -Force
} finally {
    if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath $incoming -Force }
}

# The stage is 150 MB of which one file was wanted.
Remove-Item -LiteralPath $stage -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
if ($Shortcut) {
    New-Item -ItemType Directory -Path $ShortcutFolder -Force | Out-Null
    $lnk = $shell.CreateShortcut($link)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = $Destination
    $lnk.Description = "Turns AltStable's portrait captures into the cutouts the Roster draws"
    $lnk.Save()
    Write-Host "Start menu: $link"
} elseif (Test-Path -LiteralPath $link) {
    # A shortcut from an earlier deploy, to another folder: it starts the OLD exe.
    $points = $shell.CreateShortcut($link).TargetPath
    if (-not [string]::Equals($points, $target, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning "The Start menu shortcut still starts $points. Deploy with -Shortcut to point it here."
    }
}

$file = Get-Item -LiteralPath $target
$version = $file.VersionInfo.ProductVersion
Write-Host ("Deployed {0} ({1:N0} MB) to {2}" -f $version, ($file.Length / 1MB), $target) -ForegroundColor Green
if ($dirty) {
    Write-Warning "Built from a working tree with changes that are in no commit: '-dirty' in the version says so."
}
Write-Host "Run it:    & `"$target`""
Write-Host "In a copy: & `"$target`" --wow-dir <flavour folder> --data-dir <folder>"
