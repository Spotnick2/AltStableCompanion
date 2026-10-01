# Changelog

What changed for the player, by version. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the release workflow takes a
version's section, as it stands here, for the notes of its GitHub Release.

## [Unreleased]

## [0.1.0-beta.1]

The first release, a beta: everything below is new, and the owner has run it against
their own game for a month; nobody else has yet.

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
- Enhanced portraits, off by default: with the Codex CLI installed and signed in on this
  PC, a better picture of each character of the chosen level or more, from its own portrait
  - in the look of the WoW cinematics, photorealistic, or as an animated feature. The one
  exception to "no network": the Settings page says exactly what is sent and when it
  spends. Needs an AltStable Roster that knows enhanced textures. A cancelled attempt (the
  box unticked, a quit) is tried once more; refused and failed ones are final.
- What the enhancer is doing is visible: the tray tooltip and a dot on the tray icon while a
  picture is being made, a balloon when one is written or refused, a status line under the
  switch, "Enhancing" on the character's row, and the list ordered by latest activity.
- A settings change never spends on its own: a picture made from the portrait a character
  still has is held when only the style, the model, the effort or the wording changed, and
  the Settings page says how many and offers to make them again - exactly those, and a quit
  mid-way does not take the offer back. A new capture is made on its own.
