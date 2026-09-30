# AGENTS.md — AltStable Companion

A Windows tray app that turns AltStable's in-game portrait captures into the cutouts the
addon's Roster scene draws. The addon lives in `C:\Projects\AltStable`
(`Spotnick2/AltStable`); the work is tracked in AltStable#89.

## The contract is the source of truth

Everything this app reads and writes is specified in the addon's
`docs/PORTRAIT-CONTRACT.md`. Read it before touching the readers or writers. When the two
disagree, the contract wins — and a change here that the contract does not allow is a
change to the contract first, made in the addon's repo, with a `version` bump if a reader
could misparse.

The reference implementation is the addon's own `Tools/RenderCutout/make-cutout.py` and
`Update-Cutouts.ps1`. Their numbers (matte, floors, thresholds, canvas sizes, manifest
format) are measured on the live client; port them, do not re-derive them. Where this app
deliberately differs (pass-wide screenshot claims, no superseded-file deletion, GUID
identity, epoch ordering) the contract says so.

## Layout

```
AltStableCompanion.slnx
src/AltStableCompanion.Core/        no UI: install, records, pairing, matte, TGA, manifest, watcher, controller
src/AltStableCompanion.App/         Avalonia tray shell: window, Win32 tray icon, single instance
tests/AltStableCompanion.Core.Tests xunit
Tools/Publish-Exe.ps1               the one publish both of the next two ship
Tools/deploy.ps1                    put the exe where the owner runs it from
Tools/Release.ps1                   what a tag builds: version check, CHANGELOG notes, exe, SHA256SUMS
.github/workflows/                  ci.yml (build + test), release.yml (a v* tag -> a GitHub Release)
CHANGELOG.md                        one section per version; the release notes come from it
```

## Build & test

```
dotnet build -warnaserror
dotnet test
```

CI (`.github/workflows/ci.yml`) runs exactly these two on every push and PR; a release
(`release.yml`, on a `v*` tag) runs them again, then `Tools/Release.ps1`.

Warnings are errors (`Directory.Build.props`). Tests generate their own images and
SavedVariables files in temp folders — there are no binary fixtures, and none should be
added.

The shell has no tests of its own. What it decides - the status line, the balloon, the
command line, which install a run uses - lives in Core (`PassText`, `StartupOptions`,
`ResolvedInstall`) and is tested there, and so does `Controller`, which holds the locks and the
order things happen in: its tests run it for real, on temporary installs. What is left in the
shell is drawing and Win32, checked by hand.

Running the app while developing: always with `--wow-dir` and `--data-dir` pointed at a copy.
Without them it detects the real install and its first pass deletes the screenshots it
converts.

## Deploy

```
pwsh Tools/deploy.ps1               # -Shortcut for the Start menu, -Destination for another folder
```

`Tools/deploy.ps1` puts one self-contained `AltStableCompanion.exe` in
`%LOCALAPPDATA%\Programs\AltStableCompanion`. It refuses while the copy it would replace is
running, and it does not start the app. An exe built from a working tree with uncommitted
changes says `-dirty` in its version.

## Conventions

- **Right-size for a single maintainer.** No DI container, no plugin system, no i18n, no
  auto-update. An interface with one implementation is speculation.
- **No network, ever - with one exception the player switches on.** It is a promise in the
  README. The app touches only screenshots it matched to a capture, the `AltStableCutouts`
  folder and `%APPDATA%\AltStableCompanion`. The published single-file exe also unpacks its
  own native libraries under `%TEMP%\.net`; that is the .NET host, it is stated in the README,
  and nothing else may be added to it. The exception (#17, the contract's section 3): enhanced
  portraits, **off by default**, hand a character's cropped portrait and its race, gender and
  class to the Codex CLI on this PC, which sends them to the account it is signed in to; the
  app writes `Cutouts\Enhanced\` for it and reads Codex's own `generated_images` folder,
  never cleaning it. Every word the app says about this is exact, and it is off unless the
  player chose it.
- **Never delete what you did not match.** Screenshots belong to the player; only the two
  files a capture consumed may go, and only after its cutout is on disk.
- **Hand-rolled images.** The TGA codec and the resampler are ours (no ImageSharp): small,
  exact, and unit-tested. WoW writes RLE TGA (type 10), 32-bit, top-left origin; cutouts
  are written uncompressed, bottom-left origin, like the files known to load.
- **Mutation-test claims.** A test that passes with the behaviour broken proves nothing;
  break it and confirm the suite goes red.
- Branch per milestone, PR into `main`. Commit or push only when asked. The owner runs
  reviews.
