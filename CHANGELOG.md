# Changelog

What changed for the player, by version. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the release workflow takes a
version's section, as it stands here, for the notes of its GitHub Release.

## [Unreleased]

### Added

- Enhanced portraits, off by default: with the Codex CLI installed and signed in on this
  PC, a better picture of each character of the chosen level or more, from its own portrait
  - in the look of the WoW cinematics, photorealistic, or as an animated feature. The one
  exception to "no network": the Settings page says exactly what is sent and when it
  spends. Needs an AltStable Roster that knows enhanced textures. A cancelled attempt (the
  box unticked, a quit) is tried once more; refused and failed ones are final.

## [0.1.0]

The first release.

### Added

- Turns the AltStable addon's portrait captures (the black and white screenshot pair
  `/alts portrait` takes) into transparent cutouts, and writes the `AltStableCutouts`
  addon folder the Roster scene draws from - by itself, within seconds of a `/reload`.
- Runs in the tray. The window shows every portrait with a thumbnail, what each capture
  became, one headline with the next step, and a search box.
- The first start asks before anything is converted, and says that the two screenshots a
  portrait was made from are deleted unless "Keep original capture screenshots" is on.
- Settings: the game folder (detected, or chosen), automatic processing on or off, kept
  screenshots, and the window's look - the addon's Clear glass, Smoked glass or Flat.
- `--minimized`, `--wow-dir` and `--data-dir` on the command line; a wrong one is an
  error, never a guess.
