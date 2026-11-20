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
- The game's screenshots that match a portrait capture.
- With enhanced portraits on, the pictures in Codex's own `generated_images` folder
  (`%USERPROFILE%\.codex\generated_images`, or under `CODEX_HOME`), to pick up the one
  Codex just made.

## What it keeps, and where

Only on your PC:

- **`%APPDATA%\AltStableCompanion`**: its settings, its log, the armory and enhanced
  pictures it keeps for each character, and - only if you set them up - your Blizzard API
  client and your Google sign-in. Those two are encrypted for your Windows user (Windows
  DPAPI), never written to the log, and never included in a diagnostics file. With the
  Google Sheet, the settings also hold your spreadsheet's ID and address.
- **The game's `Interface\AddOns\AltStableCutouts` folder**: the portraits it makes for the
  game (`.tga` pictures, each with a small `.json` file describing it).
- **Your Downloads folder**: a diagnostics file, only when you press Help → **Save
  diagnostics…**. It is a text file for a bug report, and you choose whether to send it.
- **Codex's `generated_images` folder**: Codex itself keeps every picture it makes for
  enhanced portraits there. The app only reads that folder.

## What leaves your PC

Nothing, except for the features you turn on or press:

| Feature | Sent to | What |
| --- | --- | --- |
| Portraits from the armory | `render.worldofwarcraft.com` (Blizzard) | a request for each character's picture, by the number in its GUID |
| Your own Blizzard API client | `oauth.battle.net`, `us.api.blizzard.com` (Blizzard) | your client's ID and secret, to the first only, for a token; then one request per character, naming it |
| Enhanced portraits | the Codex CLI on your PC, which sends it to OpenAI under the ChatGPT account it is signed in to | each portrait to redraw, with a prompt naming the character's gender, race and class and the art style you chose |
| Check for updates | `api.github.com`, `github.com` | a request for this project's list of releases; the new version's file when you press Update now |
| Google Sheet | Google (see below) | your characters' data, into your own spreadsheet |

## Google Sheet and your Google data

The Google Sheet export is off until you press **Connect** in the app's settings and sign in
with Google.

- **What the app may access.** It asks for one permission, `drive.file`: the files the app
  itself creates in your Google Drive. It cannot see, read or change any other file in your
  Drive.
- **What it reads from Google.**
  - From the spreadsheet it created, before each export: the ID and title of each tab, tabs
    you added included, and the labels it put on its own tabs, so it rewrites its own tabs
    and leaves yours alone.
  - From your Drive, when the address it kept no longer works (a new PC, a reinstall, the
    spreadsheet deleted): the IDs of the spreadsheets it created, and whether each is in the
    trash, so it can write the same one again instead of making another.
- **What it writes.** One spreadsheet, named AltStable, holding your characters as AltStable
  recorded them: name, class, faction, level, item level, rested XP, gold, last played,
  hearthstone, guild, race, realm and its ruleset, professions and skills, reputations,
  equipped items and enchants. Accounts appear as 1, 2, ...; your account names stay on your
  PC. The spreadsheet is yours, in your Drive, shared with nobody unless you share it.
- **When.** When you press Connect, Export now or Disconnect, and on its own: **Export on its
  own** is ticked when you connect, so by default the app exports 15 minutes after the game
  has closed and stayed closed, when AltStable has saved something new. The delay is yours
  to change (5 to 120 minutes), and unticking it leaves only the exports you ask for.
- **Where it goes.** The app sends it only to Google: `oauth2.googleapis.com` (the sign-in),
  `sheets.googleapis.com` (the spreadsheet) and `www.googleapis.com` (Drive, to find the
  spreadsheet it made). Nothing goes to the developer or anyone else.
- **The spreadsheet's pictures.** The spreadsheet shows pictures by their web address: the
  section icons, from this project's GitHub page (`raw.githubusercontent.com`), and, with
  armory portraits on, each character's armory picture or its race's silhouette, from
  `render.worldofwarcraft.com`, whose address holds the number in the character's GUID.
  Google fetches them, not the app, and only once you open the spreadsheet and choose
  **Allow access** there, which Google asks for once.
- **What is kept.** The sign-in (a refresh token), encrypted on your PC as said above, and the
  spreadsheet's ID and address in the app's settings.

AltStable Companion's use and transfer of information received from Google APIs adheres to
the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements. Your Google data is used only to write your
spreadsheet; it is not sold, not used for advertising, not shared with anyone, not read by
any person, and not used to train any AI model.

## Removing it

- **Disconnect** in the app deletes the sign-in from your PC and asks Google to cancel it.
  It keeps the spreadsheet's address, so connecting the same Google account again goes on
  writing the same spreadsheet. You can also remove the app's access at any time from your
  Google Account, under [Third-party apps & services](https://myaccount.google.com/connections).
- The spreadsheet stays yours: delete it from your Drive whenever you like.
- To remove everything the app keeps, quit it, then delete:
  - `%APPDATA%\AltStableCompanion` (settings, log, pictures, the spreadsheet's address and
    any sign-in);
  - the `AltStableCutouts` folder in your game's `Interface\AddOns` folder (the portraits it
    made for the game);
  - any diagnostics file you saved in Downloads (`AltStableCompanion-diagnostics-...`).

  Codex's `generated_images` folder belongs to Codex; the app never deletes from it. Delete
  the pictures there yourself if you no longer want them.

## Questions

Open an issue at <https://github.com/Spotnick2/AltStableCompanion/issues>.

Last updated: 2026-11-20.
