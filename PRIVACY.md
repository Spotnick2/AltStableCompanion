# Privacy policy

AltStable Companion is a free, open-source Windows app made by one person (Spotnick2) for the
World of Warcraft: Forever addon AltStable. It runs on your PC. **There is no server of ours:
the developer receives nothing from the app, ever** - no account, no analytics, no telemetry,
no crash reports.

This page says what the app reads, keeps and sends. The [README](README.md#what-it-touches)
says the same with more detail, feature by feature.

## What it reads on your PC

- AltStable's saved files in your game folder (`WTF\Account\...\SavedVariables`): your
  characters, as the addon recorded them.
- The game's screenshots that match a portrait capture, and the `AltStableCutouts` addon
  folder, which it writes.
- Its own settings and log, in `%APPDATA%\AltStableCompanion`.

## What it keeps, and where

Only on your PC, in `%APPDATA%\AltStableCompanion`: its settings, its log, the portraits it
makes, and - only if you set them up - your Blizzard API client and your Google sign-in.
Those two are encrypted for your Windows user (Windows DPAPI), never written to the log, and
never included in a diagnostics file.

## What leaves your PC

Nothing, except for the features you turn on or press:

| Feature | Sent to | What |
| --- | --- | --- |
| Portraits from the armory | `render.worldofwarcraft.com` (Blizzard) | a request for each character's picture, by the number in its GUID |
| Your own Blizzard API client | `oauth.battle.net`, `us.api.blizzard.com` (Blizzard) | your client's ID and secret, to the first only, for a token; then one request per character, naming it |
| Enhanced portraits | the Codex CLI on your PC, signed in to your own ChatGPT account | each portrait to redraw |
| Check for updates | `api.github.com`, `github.com` | a request for this project's list of releases; the new version's file when you press Update now |
| Google Sheet | Google (see below) | your characters' data, into your own spreadsheet |

## Google Sheet and your Google data

The Google Sheet export is off until you press **Connect** in the app's settings and sign in
with Google.

- **What the app may access.** It asks for one permission, `drive.file`: the files the app
  itself creates in your Google Drive. It cannot see, read or change any other file in your
  Drive.
- **What it reads from Google.** Only the spreadsheet it created: whether it still exists and
  whether it is in the trash, so it can write the same one again instead of making another.
- **What it writes.** One spreadsheet, named AltStable, holding your characters as AltStable
  recorded them: name, class, faction, level, item level, rested XP, gold, last played,
  hearthstone, guild, race, realm and its ruleset, professions and skills, reputations,
  equipped items and enchants. Accounts appear as 1, 2, ...; your account names stay on your
  PC. With armory portraits on, the cells hold the addresses of the characters' armory
  pictures. The spreadsheet is yours, in your Drive, shared with nobody unless you share it.
- **When.** When you press Connect, Export now or Disconnect, and - if "Export on its own" is
  ticked - some minutes after the game closes, when AltStable has saved something new.
- **Where it goes.** Only to Google: `oauth2.googleapis.com` (the sign-in),
  `sheets.googleapis.com` (the spreadsheet) and `www.googleapis.com` (Drive, to find the
  spreadsheet it made). Nothing goes to the developer or anyone else.
- **What is kept.** The sign-in (a refresh token), encrypted on your PC as said above.

AltStable Companion's use and transfer of information received from Google APIs adheres to
the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements. Your Google data is used only to write your
spreadsheet; it is not sold, not used for advertising, not shared with anyone, not read by
any person, and not used to train any AI model.

## Removing it

- **Disconnect** in the app deletes the sign-in from your PC and asks Google to cancel it.
  You can also remove the app's access at any time from your Google Account, under
  [Third-party apps & services](https://myaccount.google.com/connections).
- The spreadsheet stays yours: delete it from your Drive whenever you like.
- Deleting `%APPDATA%\AltStableCompanion` removes everything the app keeps.

## Questions

Open an issue at <https://github.com/Spotnick2/AltStableCompanion/issues>.

Last updated: 2026-11-20.
