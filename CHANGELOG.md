# Changelog

What changed for the player, by version. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the release workflow takes a
version's section, as it stands here, for the notes of its GitHub Release.

## [Unreleased]

### Added

- The list follows AltStable's: a character AltStable no longer has (forgotten, deleted, or
  left over from the beta) is marked "AltStable no longer has this character" and not
  counted, and **Clean up the character list** offers to delete their files - portraits,
  armory and enhanced pictures, the screenshots of their captures - after naming them.
  Nothing is deleted without that yes. A setting leaves them out of the list instead.

### Changed

- Enhanced portraits keep the small details: the prompt now names nail color, makeup, tattoos
  and jewelry, which an armory picture shows only a few pixels wide (a Skyborne lost her blue
  nails). Pictures made with the earlier wording are offered to be made again, not remade on
  their own.

## [1.2.4]

A character you are still gearing up gets their armory picture instead of last week's capture,
"Check the armory now" says what it is doing, and the remake setting takes hours.

### Added

- "Check the armory now" says what it is doing: "Checking 3 of 8: Drucida Komakino...", then
  what it found - new pictures, unchanged ones, characters held by the remake setting and
  until when, and why it stopped if it did. The button is off while it runs.

### Changed

- The remake setting takes hours or days ("at most every 12 hours"). A setting saved in days
  stays in days.

### Fixed

- A character whose gear changed since their capture gets their armory picture as soon as the
  armory has drawn them since the capture, not only once it shows the very last change. A
  character still being geared up changed again before each new armory picture, so they kept
  a capture of last week, and an armory picture that had taken over went back to the capture
  at the next change (an enhanced picture spent for nothing). A character that has not changed
  still keeps their capture.

## [1.2.3]

Enhanced pictures that 1.2.0 lost come back, rebuilt from the copies the app kept, with no new
Codex run.

### Fixed

- A capture whose enhanced picture 1.2.0 replaced with the armory picture's gets its own
  back, rebuilt from the full-size picture the app kept, with no new Codex run. 1.2.1 made
  the capture the portrait again, but its enhanced picture stayed lost: it was drawn plain,
  with a warning, and was never made again. The same rebuild covers any capture and armory
  picture that trade places.
- A portrait whose enhanced picture is not the one drawn no longer says "enhanced" on its
  line.

## [1.2.2]

A held armory picture says when it resumes, to the minute, and can be asked for now.

### Added

- A character held by the armory's days limit says when it resumes to the minute ("armory
  updates resume tomorrow 19:29"), and offers **Check now**: the armory is asked about that
  character at once, and the picture it leaves is enhanced at once, as you asked for it.

## [1.2.1]

A capture stays your portrait until the character changes. 1.2.0 put the armory picture in
its place too soon, which dropped enhanced pictures; this brings them back.

### Fixed

- An armory picture no longer replaces a capture just because it is newer. A capture is
  sharper (the armory draws a character about half as tall) and may have an enhanced
  picture, so it stays the portrait until the character changes: their gear, or their look at
  the barber. The armory picture then takes over once it shows the change. 1.2.0 switched
  characters that had not changed to their armory picture, which dropped their enhanced
  pictures; this puts them back, with no new capture. It uses the gear AltStable records
  with each capture; a capture recorded without it falls back to the date, as before.
- An armory picture waiting for its new enhanced picture is no longer listed under Warnings.

## [1.2.0]

Portraits from the armory: a character you never captured gets a portrait, and a portrait
follows the gear on its own. The app asks once whether to use it.

### Added

- Portraits from the armory (#63). Blizzard's armory website draws each character of level
  10 or more. The app can take that picture as the portrait, so a character never captured
  has one, and a portrait follows the gear without a capture. The app asks once whether to
  use it, at the first start or after this update, and Settings can change the answer.
  - It asks Blizzard's picture server about each character once when it starts, then every
    hour from 6 to 24 hours after that character was played. No account or key is needed.
  - A new capture always shows at once; an armory picture from later play takes its place.
  - HD or SD model, in Settings.
  - For a character the armory has drawn, the addon no longer asks for a capture after a
    gear change (the spyglass button's glow), with AltStable 1.2.0 or later.
  - US region only for now.
  - Optional: your own Blizzard API client, under Settings, with a link to make one. With it,
    the app learns each character's level and last login from Blizzard's profile API, on any
    PC: a character that reached level 10 elsewhere is drawn, and play elsewhere is noticed.
    It is kept encrypted for your Windows user and never shown again.
- With enhanced portraits on, a new armory picture is enhanced on its own. Three settings
  limit that: it can be offered instead and made when you ask; at most one per character every
  7 days (1 to 90); and at most 5 a day, a limit you can untick. A picture drawn again that
  looks the same keeps its enhanced picture. A capture is still enhanced at once.

## [1.1.0]

Sharp portraits on a large screen. To get them, capture each character again: a portrait
already made keeps its old size, and a new capture is enhanced again on its own.

### Changed

- Portraits are sharp on a large screen. They were made 512 pixels tall, and at 3840x2160
  the Char page draws a character 1329 pixels tall. Now a portrait is made as tall as the
  Char page draws it on the screen it was captured on (1329 at 4K, 665 at 1080p), and an
  enhanced one keeps the size Codex made it (up to 1536). At 4K, in the `AltStableCutouts`
  folder, a plain portrait takes 4 or 8 MB and an enhanced one 8 MB, where each took 512 KB.

### Added

- Each enhanced portrait is also kept as Codex made it, full size, in the app's own folder
  (`originals`). The game never reads it.

## [1.0.0]

AltStable Companion 1.0, for World of Warcraft: Forever going live. The app is the same as
0.1.1: this release changes its version number only. If you run 0.1.1, **Help → Check for
updates** offers it, and nothing you have needs to change.

## [0.1.1]

Ready for Forever live: the app finds the game in `_forever_`.

### Changed

- WoW Forever is found in its live folder, `_forever_`, which it uses from 2026-11-04. The
  beta's `_classic_beta_` is no longer looked for. If Settings still names the beta folder,
  press Detect again, or Browse to `_forever_`.

## [0.1.0]

The first release that is not a beta. A portrait that enhancing leaves alone says why, and
the Settings page scrolls.

### Added

- A portrait that enhancing leaves alone says why on its line in the list: "not enhanced:
  hidden in WoW12", "below level 4", or a roster record without race, gender or class. A
  character hidden in another account was skipped without a word, and the game only shows
  the hidden list of the account you are logged in to.

### Fixed

- The Settings page scrolls, and the level and style of enhanced portraits come before its
  long explanation. In a small window the options below that text could not be reached.

## [0.1.0-beta.4]

Enhanced portraits say how many pictures they will make before anything is sent, and the app
finds the game you play AltStable in.

### Added

- Ticking **Enhance portraits with Codex** asks first: it says how many pictures it will make
  at once - counted the way they are then made, leaving out the ones already tried or kept
  from other settings - and nothing is sent until you press **Make N pictures**. **Cancel**
  leaves it off. Turning it on again later asks again.

### Changed

- Finding the game by itself, the app now picks the game folder AltStable is installed in,
  looking at every game in your World of Warcraft folder - not only the one the launcher
  ran last. When AltStable is installed in more than one, the dashboard names the others
  and says that Browse switches.
- **Save diagnostics…** writes to your Downloads folder, not the Desktop. A Desktop that
  OneDrive syncs - Windows' default - uploaded the file the moment it was written.

### Fixed

- The log is easier to read in a report: every line of a multi-line entry (Codex's output
  after a picture that did not come back, an error's details) now carries the time, and
  Codex's output starts at a line instead of mid-word, unless that would lose the error. A
  problem with the portraits folder, such as "Enhanced\… is not drawn", is written when it
  appears, not again each time a picture is started; if it goes away and comes back, it is
  written again.

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
