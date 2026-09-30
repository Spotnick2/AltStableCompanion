#Requires -Version 7
<#
.SYNOPSIS
Builds what a GitHub Release of AltStable Companion is made of.

.DESCRIPTION
Run by the release workflow, and by hand for a rehearsal. It:

  1. checks that the tag is exactly "v" + the <Version> in Directory.Build.props - the
     version lives in one place, the tag confirms it. With no -Tag it is a rehearsal of
     whatever version the props say;
  2. takes that version's section out of CHANGELOG.md ("## [<version>]" up to the next
     "## "), and fails when it is missing, doubled, or empty;
  3. checks the tree is a commit with nothing uncommitted, and publishes the app through
     Tools/Publish-Exe.ps1 - the same build deploy.ps1 makes - stamped with that commit;
  4. names the file AltStableCompanion-<version>-win-x64.exe, checks its product version
     reads <version>+<commit>, and writes SHA256SUMS beside it.

Nothing here talks to GitHub: the workflow does that with the files this leaves in -Out.

.PARAMETER Tag
The release tag, such as v0.1.0 or v0.2.0-rc1. Left out, the props' version is rehearsed.

.PARAMETER Out
Where the exe, SHA256SUMS and notes.md go: publish\release under the repo unless named.
Emptied first, so it must be a folder of its own - not the repo, not a folder holding it.

.PARAMETER SkipBuild
Only the checks (1 and 2): for trying tags and change logs without a four-minute publish.
#>
[CmdletBinding()]
param(
    [string]$Tag = "",
    [string]$Out = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $RepoRoot "publish\release" }
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)

# ---- the output folder: emptied, so never a root, the repo, or a folder holding it
$outFull = [IO.Path]::GetFullPath($Out)
$repoFull = [IO.Path]::GetFullPath($RepoRoot)
# A drive or share root keeps its trailing separator through every normalisation, so it is
# named for what it is rather than compared as a prefix.
if ([IO.Path]::GetPathRoot($outFull) -eq [IO.Path]::TrimEndingDirectorySeparator($outFull) -or
    [IO.Path]::GetPathRoot($outFull) -eq $outFull) {
    throw "-Out '$Out' is a drive or share root; it would be emptied. Name a folder of its own."
}
# The ancestor test wants both sides with exactly one trailing separator.
function WithSep([string]$p) { [IO.Path]::TrimEndingDirectorySeparator($p) + [IO.Path]::DirectorySeparatorChar }
if ((WithSep $repoFull).StartsWith((WithSep $outFull), [StringComparison]::OrdinalIgnoreCase)) {
    throw "-Out '$Out' is the repository or a folder above it; it would be emptied. Name a folder of its own."
}
# And only a folder that is empty, or holds nothing but what this script left there before:
# a folder named by mistake keeps whatever it has.
if (Test-Path -LiteralPath $outFull) {
    $foreign = @(Get-ChildItem -LiteralPath $outFull -Force | Where-Object {
        $_.Name -notin @("notes.md", "SHA256SUMS", "stage") -and $_.Name -notlike "AltStableCompanion-*-win-x64.exe"
    })
    if ($foreign.Count -gt 0) {
        throw "-Out '$Out' holds things that are not this script's ($($foreign[0].Name)$(if ($foreign.Count -gt 1) { ', ...' })); it would be emptied. Name an empty folder, or one only this script has used."
    }
}

# ---- 1. the version, and the tag that names it
[xml]$props = Get-Content -LiteralPath (Join-Path $RepoRoot "Directory.Build.props")
$version = ([string]$props.Project.PropertyGroup.Version).Trim()
if (-not $version) { throw "Directory.Build.props has no <Version>." }
if (-not $Tag) {
    $Tag = "v$version"
    Write-Host "No tag: rehearsing $Tag" -ForegroundColor Cyan
} elseif ($Tag -cne "v$version") {
    throw "The tag is '$Tag' but Directory.Build.props says ${version}: the tag must be exactly 'v$version'. Change the version in the props (and the CHANGELOG) in a commit, then tag that commit."
}

# ---- 2. the change log's section for it
$lines = (Get-Content -LiteralPath (Join-Path $RepoRoot "CHANGELOG.md") -Raw) -split "`r?`n"
$starts = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -cmatch "^## \[$([regex]::Escape($version))\]" })
if ($starts.Count -eq 0) { throw "CHANGELOG.md has no '## [$version]' section." }
if ($starts.Count -gt 1) { throw "CHANGELOG.md has $($starts.Count) '## [$version]' sections; there must be one." }
$from = $starts[0] + 1
$to = $lines.Count
for ($i = $from; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $to = $i; break } }
# A header followed at once by the next one has no lines between: an empty section, not a
# range that runs backwards.
$notes = if ($to -gt $from) { ($lines[$from..($to - 1)] -join "`n").Trim() } else { "" }
if (-not $notes) { throw "CHANGELOG.md's '## [$version]' section is empty." }

# ---- 3. the commit it is built from - checked before anything is written
$revision = ""
if (-not $SkipBuild) {
    $revision = ([string](git -C $RepoRoot rev-parse HEAD 2>$null)).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $revision) { throw "git rev-parse failed: a release is built from a commit." }
    if (git -C $RepoRoot status --porcelain) { throw "The working tree has changes that are in no commit: a release is built from a commit." }
}

if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out | Out-Null
[IO.File]::WriteAllText((Join-Path $Out "notes.md"), $notes + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Version $version, tag $Tag, notes: $(($notes -split "`n").Count) lines" -ForegroundColor Cyan
if ($SkipBuild) { return }

# ---- the build, the one deploy.ps1 makes
$stage = Join-Path $Out "stage"
Write-Host "Publishing $revision ..." -ForegroundColor Cyan
$built = & (Join-Path $PSScriptRoot "Publish-Exe.ps1") -Stage $stage -Revision $revision

# ---- 4. the asset, its version, its hash
$name = "AltStableCompanion-$version-win-x64.exe"
$asset = Join-Path $Out $name
Copy-Item -LiteralPath $built -Destination $asset
Remove-Item -LiteralPath $stage -Recurse -Force
$product = (Get-Item -LiteralPath $asset).VersionInfo.ProductVersion
if ($product -cne "$version+$revision") { throw "The file's product version is '$product', not '$version+$revision'." }
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
# The sha256sum format, LF-terminated, so `sha256sum -c SHA256SUMS` works everywhere.
[IO.File]::WriteAllText((Join-Path $Out "SHA256SUMS"), "$hash  $name`n", [Text.UTF8Encoding]::new($false))
Write-Host "Built $name ($([math]::Round((Get-Item -LiteralPath $asset).Length / 1MB)) MB), product version $product" -ForegroundColor Green
Write-Host "SHA-256 $hash"
