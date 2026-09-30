<#
.SYNOPSIS
Builds what a GitHub Release of AltStable Companion is made of, from a tag.

.DESCRIPTION
Run by the release workflow, and by hand for a rehearsal. For the tag it is given it:

  1. checks that the tag is exactly "v" + the <Version> in Directory.Build.props - the
     version lives in one place, the tag confirms it;
  2. takes the tag's section out of CHANGELOG.md ("## [<version>]" up to the next "## "),
     and fails when it is missing, doubled, or empty;
  3. publishes the app the way Tools/deploy.ps1 does, stamped with the commit;
  4. names the file AltStableCompanion-<version>-win-x64.exe, checks its product version
     reads <version>+<commit>, and writes SHA256SUMS beside it.

Nothing here talks to GitHub: the workflow does that with the files this leaves in -Out.

.PARAMETER Tag
The release tag, such as v0.1.0 or v0.2.0-rc1.

.PARAMETER Out
Where the exe, SHA256SUMS and notes.md go. Emptied first.

.PARAMETER SkipBuild
Only the checks (1 and 2): for trying tags and change logs without a four-minute publish.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Tag,
    [string]$Out = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $RepoRoot "publish\release" }
$Project = Join-Path $RepoRoot "src\AltStableCompanion.App\AltStableCompanion.App.csproj"

# ---- 1. the version, and the tag that names it
[xml]$props = Get-Content -LiteralPath (Join-Path $RepoRoot "Directory.Build.props")
$version = [string]$props.Project.PropertyGroup.Version
if (-not $version) { throw "Directory.Build.props has no <Version>." }
if ($Tag -cne "v$version") {
    throw "The tag is '$Tag' but Directory.Build.props says ${version}: the tag must be exactly 'v$version'. Change the version in the props (and the CHANGELOG) in a commit, then tag that commit."
}

# ---- 2. the change log's section for it
$changelog = Get-Content -LiteralPath (Join-Path $RepoRoot "CHANGELOG.md") -Raw
$lines = $changelog -split "`r?`n"
$starts = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -cmatch "^## \[$([regex]::Escape($version))\]" })
if ($starts.Count -eq 0) { throw "CHANGELOG.md has no '## [$version]' section." }
if ($starts.Count -gt 1) { throw "CHANGELOG.md has $($starts.Count) '## [$version]' sections; there must be one." }
$from = $starts[0] + 1
$to = $lines.Count
for ($i = $from; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $to = $i; break } }
$notes = ($lines[$from..($to - 1)] -join "`n").Trim()
if (-not $notes) { throw "CHANGELOG.md's '## [$version]' section is empty." }

if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out | Out-Null
Set-Content -LiteralPath (Join-Path $Out "notes.md") -Value $notes -NoNewline -Encoding utf8
Write-Host "Version $version, tag $Tag, notes: $(($notes -split "`n").Count) lines" -ForegroundColor Cyan
if ($SkipBuild) { return }

# ---- 3. the build, as deploy.ps1 does it
$revision = (git -C $RepoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not $revision) { throw "git rev-parse failed: a release is built from a commit." }
if (git -C $RepoRoot status --porcelain) { throw "The working tree has changes that are in no commit: a release is built from a commit." }
$stage = Join-Path $Out "stage"
Write-Host "Publishing $revision ..." -ForegroundColor Cyan
dotnet publish $Project -c Release -r win-x64 -o $stage --nologo -v q -warnaserror "-p:SourceRevisionId=$revision"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# ---- 4. the asset, its version, its hash
$built = Join-Path $stage "AltStableCompanion.exe"
if (-not (Test-Path -LiteralPath $built)) { throw "publish produced no AltStableCompanion.exe" }
$name = "AltStableCompanion-$version-win-x64.exe"
$asset = Join-Path $Out $name
Copy-Item -LiteralPath $built -Destination $asset
Remove-Item -LiteralPath $stage -Recurse -Force
$product = (Get-Item -LiteralPath $asset).VersionInfo.ProductVersion
if ($product -cne "$version+$revision") { throw "The file's product version is '$product', not '$version+$revision'." }
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $Out "SHA256SUMS") -Value "$hash  $name" -Encoding ascii
Write-Host "Built $name ($([math]::Round((Get-Item -LiteralPath $asset).Length / 1MB)) MB), product version $product" -ForegroundColor Green
Write-Host "SHA-256 $hash"
