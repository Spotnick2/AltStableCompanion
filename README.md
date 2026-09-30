# AltStable Companion

A small Windows tray app that turns the portrait captures taken by the
[AltStable](https://github.com/Spotnick2/AltStable) World of Warcraft: Forever addon
into the transparent cutouts its Roster scene draws.

## Install

1. Have the [AltStable](https://github.com/Spotnick2/AltStable) addon installed: the
   captures come from it, and the cutouts go back to it.
2. On the [latest release](https://github.com/Spotnick2/AltStableCompanion/releases/latest),
   download `AltStableCompanion-<version>-win-x64.exe`. It is the whole app: Windows 10 or
   11, 64-bit, nothing else to install (not .NET either). Put it in a folder you will keep -
   `Documents\AltStable Companion`, say - and run it.
3. Windows may show "Windows protected your PC", because the file is not code-signed: if
   it offers **More info → Run anyway**, that is the way through. (A Windows 11 PC with
   Smart App Control turned on may not offer it; unsigned distribution is a limitation of
   this free tool for now.)
4. The window opens by the tray. Read its first-start card - it says what will be
   converted and that the two screenshots a portrait was made from are deleted unless you
   keep them - and press **Start watching**.

From then on: in the game, `/alts portrait`, then `/reload`; the portrait is made within
seconds, and a `/reload` shows it. The very first portrait needs the game restarted once,
because WoW only finds a new addon folder when it starts.

Closing the window leaves the app running in the tray; **Quit** is in the tray icon's menu.
It does not start with Windows: start it again after logging in. To upgrade, quit the old
one, then run the new file - starting a second copy only brings the running one's window
forward.

Settings (`settings.json`) and the log (`log.txt`) live in `%APPDATA%\AltStableCompanion`.
Work on the app is tracked in [AltStable#89](https://github.com/Spotnick2/AltStable/issues/89).

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

.NET 10 and Avalonia; a single self-contained Windows executable; no network access
at all. It only touches screenshots it matched to a capture, the `AltStableCutouts`
folder and its own settings in `%APPDATA%\AltStableCompanion`. The file on the releases
page is built by GitHub Actions from the tagged commit (`.github/workflows/release.yml`),
with a `SHA256SUMS` beside it; nothing in the app fetches anything.

One exception, which is .NET's and not the app's: a single-file executable unpacks the
native libraries it carries (Skia, HarfBuzz, ANGLE) into `%TEMP%\.net\AltStableCompanion`
the first time it starts.

## Command line

| Option | What it does |
|---|---|
| `--minimized` | Start in the tray, without the window. |
| `--wow-dir <folder>` | Use this flavour folder (such as `...\World of Warcraft\_classic_beta_`) for this run, and no other. Browse and Detect again are switched off. |
| `--data-dir <folder>` | Keep the settings and the log in this folder, not in `%APPDATA%\AltStableCompanion`. |

A command line that is wrong - an option it does not know, a `--wow-dir` that is not a
flavour folder, a `--data-dir` it cannot write to - is an error: the app says so and stops.
It never carries on with a guess, because the guess would be the game it detects, and a
pass deletes the screenshots it converts.

Only one instance runs at a time. Starting it again brings the running one's window forward.

## Releasing (for the maintainer)

A release is a tag. Set `<Version>` in `Directory.Build.props`, write that version's
section in `CHANGELOG.md`, merge, wait for CI, then tag that commit `v<version>` and push
the tag: `release.yml` runs the tests, builds the exe through `Tools/Release.ps1` (which
checks the tag against the props and takes the notes from the CHANGELOG), and publishes
the GitHub Release with the exe and `SHA256SUMS`. A version with a `-` (`0.2.0-rc1`) is a
pre-release. To rehearse, run the workflow by hand from the Actions tab on any branch: it
does everything but publish, and leaves the files as an artifact. If a publish fails after
the tag is up, delete the Release (and the tag, if the fix needs a new commit) and push
the tag again; `gh release create` will not add to an existing Release.

## Licence

MIT - see [LICENSE](LICENSE).

The World of Warcraft Forever emblem in the window is Blizzard Entertainment's, used
resized and otherwise unchanged under their trademark usage guidelines; the originals and the
terms are in [Reference/Blizzard](Reference/Blizzard/README.md). World of Warcraft is a
trademark or registered trademark of Blizzard Entertainment, Inc., in the U.S. and/or other
countries. This is a fan tool, not affiliated with or endorsed by Blizzard.
