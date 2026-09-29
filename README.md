# AltStable Companion

A small Windows tray app that turns the portrait captures taken by the
[AltStable](https://github.com/Spotnick2/AltStable) World of Warcraft: Forever addon
into the transparent cutouts its Roster scene draws.

**Status: not released yet.** Work is tracked in
[AltStable#89](https://github.com/Spotnick2/AltStable/issues/89).

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

## Planned shape

.NET 10 and Avalonia; a single self-contained Windows executable; no network access
at all. It only touches screenshots it matched to a capture, the `AltStableCutouts`
folder and its own settings in `%APPDATA%\AltStableCompanion`.

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

## Licence

MIT - see [LICENSE](LICENSE).
