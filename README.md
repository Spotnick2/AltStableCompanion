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

## Licence

MIT - see [LICENSE](LICENSE).
