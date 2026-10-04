# Developing AltStable Companion

For players, the [README](../README.md) is the place to start. This page is how the app
works and how it is built and released. The working rules for anyone changing it, human or
agent, are in [`AGENTS.md`](../AGENTS.md).

## Why it exists

The addon photographs your character twice, on a black and on a white backdrop
(`/alts portrait`). Turning that pair into a cutout needs image processing, and a
World of Warcraft addon can neither write an image file nor read the Screenshots
folder. This app does that part outside the game:

1. It watches your WoW `Screenshots` folder and the addon's saved capture records.
2. It pairs each capture with its two screenshots and recovers exact transparency:
   `alpha = 1 - (white - black)`, `colour = black / alpha`.
3. It writes `Interface\AddOns\AltStableCutouts\`, which the Roster loads.

What it reads and writes is specified in
[`docs/PORTRAIT-CONTRACT.md`](https://github.com/Spotnick2/AltStable/blob/main/docs/PORTRAIT-CONTRACT.md)
in the addon's repository.

## Shape

.NET 10 and Avalonia; a single self-contained Windows executable. The file on the
releases page is built by GitHub Actions from the tagged commit
(`.github/workflows/release.yml`), with a `SHA256SUMS` beside it. A single-file executable
unpacks the native libraries it carries (Skia, HarfBuzz, ANGLE) into
`%TEMP%\.net\AltStableCompanion` the first time it starts; that is .NET's doing, not the
app's.

The layout, the build and test commands and the conventions are in
[`AGENTS.md`](../AGENTS.md).

## Releasing (for the maintainer)

A release is a tag.
1. Set `<Version>` in `Directory.Build.props`.
2. Write that version's section in `CHANGELOG.md`.
3. Merge, and wait for CI.
4. Tag that commit `v<version>` and push the tag.

`release.yml` then runs the tests, builds the exe through `Tools/Release.ps1`, and
publishes the GitHub Release with the exe and `SHA256SUMS`. `Release.ps1` checks the tag
against the props and takes the notes from the CHANGELOG.

A version with a `-` (`0.2.0-rc1`) is a pre-release. To rehearse, run the workflow by hand
from the Actions tab on any branch: it does everything but publish. Either way, the built
files are kept as the run's artifact.

If the publish step fails after the tag is up:
1. Look under the releases page's Drafts first. `gh release create` makes a draft before
   it uploads the files, so a failed upload can leave one, and it must go before the
   re-run.
2. Re-run the failed job from the Actions tab. Pushing the same tag again does nothing,
   because it is no push event and starts no run.

If the fix needs a new commit, the tag moves: `git push --delete origin v<version>`, then
tag the new commit and push it.

Link to the releases page as `/releases`, not `/releases/latest`. GitHub's "latest" skips
pre-releases: while every release is one, the API's `releases/latest` is a 404. The web page
currently redirects to `/releases` instead (measured 2026-10-04), but that is GitHub's
behaviour to change, not ours.
