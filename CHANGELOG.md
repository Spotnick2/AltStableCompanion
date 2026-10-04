# Changelog

What changed for the player, by version. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the release workflow takes a
version's section, as it stands here, for the notes of its GitHub Release.

## [Unreleased]

### Changed

- **Save diagnostics…** writes to your Downloads folder, not the Desktop. A Desktop that
  OneDrive syncs - Windows' default - uploaded the file the moment it was written.

## [0.1.0-beta.3]

A file to attach to a bug report, and the right advice after your first portrait.

### Added

- Help → **Save diagnostics…** writes one text file to your Desktop for a bug report: the app
  and Windows versions, the WoW folder and how it was found, the AltStable and Roster
  versions, whether enhanced portraits are on and Codex was found, how many captures, cutouts
  and enhanced pictures each character has, and the last 300 lines of the log. It says what
  is inside before it writes. Character names are in it; account folder names become
  Account1, Account2... and your user folder becomes `%USERPROFILE%`. No pictures, nothing sent.
- A crash, or an error nothing else caught, is written to `log.txt`.

### Fixed

- After your first portrait, the next ones' balloons said "Reload in game to see it" while
  the game still needed its one full restart, so a `/reload` showed nothing. Until you dismiss
  the restart notice, a portrait's balloon (enhanced ones too) says to restart the game if you
  have not since your first portrait, and to `/reload` otherwise; after that it says "If WoW is
  open, /reload to see it."

## [0.1.0-beta.2]

The beta that can update itself: this is the last one you download by hand.

### Added

- An About section under Help: the version and build this is, what the app is, and links
  to the project, its issues page (for reports; **Open log** is beside it), the addon, the
  licence and Blizzard's trademark terms. The browser opens them.
- **Check for updates**, under About: the app asks GitHub for the list of releases and
  says whether a newer one exists. **Update now** downloads it beside the running file and
  checks it against the release's `SHA256SUMS`; **Restart now** finishes the portraits in
  hand, puts the new file in place of the old one (kept as `.old` until the next start) and
  starts it. A beta sees betas and releases; a release sees only releases.
- A Settings box, off by default: **Check for updates when the window opens** (at most once
  an hour). A download is always a button press.

## [0.1.0-beta.1]

The first release, a beta: everything below is new, and so far only the owner has run it
against their own game.

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
