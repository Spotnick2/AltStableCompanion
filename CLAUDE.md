# CLAUDE.md — Claude-specific overlay

`AGENTS.md` is the shared baseline (layout, build/test, conventions). This file only adds what
a Claude session here needs and cannot learn from the code. Sessions in this folder do NOT see
the memories of the AltStable addon's sessions, so the rules that matter are repeated here.

## Where things stand

- The app is AltStable#89's endgame. 1.2.1 is the current release (2026-11-12). 1.0.0
  (2026-11-04) was the first for Forever live; 1.1.0 made portraits as tall as the Char page
  draws them; 1.2.0 added portraits from the Forever armory's renders (#63); 1.2.1 fixed
  which of capture and render is the portrait. Diagnostics, About, update checks and
  Codex-enhanced portraits shipped before that.
- The armory (#63), in short:
  - Renders come from Blizzard's render CDN by GUID (US only, level 10+, the player's yes
    asked once). The schedule and measurements are in `Armory.cs`.
  - A capture stays the portrait until the character changes, and a render takes over once
    it shows the change (`PortraitJudge.cs`, the contract's section 4 as amended by
    AltStable#310). The manifest writes `armory = true` for a character the armory has drawn
    (AltStable#301), which needs AltStable 1.2.0 or later.
  - The player's own Battle.net API client is optional (`BlizzardApi.cs`, kept with DPAPI).
  - The decisions and live measurements are in this project's memory, not in the issue.
- The backlog:
  - #26: gear in the enhancement prompt.
  - #30: pet captures, with AltStable #147.
  - #51: flag deleted characters. No longer blocked on an endpoint: the profile API answers
    for Forever (`profile-forever-us`, measured with the owner's own client in #63), but it
    needs the player's own client.
  - #55: Glass Gear's external simulations, research only.
  - #59: a setting to override the portrait height.
- The window: own `Win32TrayIcon` via P/Invoke (a HIDDEN TOP-LEVEL window, not message-only -
  message-only windows never receive `WM_TASKBARCREATED`), one window with Settings and Help
  as pages, single instance. No Run key.
- Releasing: a tag `v<version>` at a commit whose `Directory.Build.props` and `CHANGELOG.md`
  agree; `Tools/Release.ps1` is what the workflow runs and can be rehearsed locally.

## Working rules (the owner's, stated explicitly)

- **The owner runs every review.** Open the PR, give the link, stop. Do not run
  `/code-review`, `/codex-consult` or any review against a PR, and do not apply review findings,
  unless asked in that turn. Their usual sequence is `/code-review` first, then Codex rounds.
- **Commit or push only when asked.** "Go ahead with M2" covers building, committing and opening
  the PR for M2; nothing more.
- **Model:** the latest Opus in the main thread; subagents Sonnet (routine) or Haiku (search).
- **Plan critiques** the owner asks for go to a different model family first (Codex via
  `/codex-consult`), Fable as the fallback. Be sceptical when a cold reviewer wants to add
  complexity — this is a single-maintainer project.
- **Measured beats reasoned.** Facts about the client and this machine (RLE screenshots, `Wow.exe`,
  the registry keys, `AltStablePortraits = nil`) were measured; they are recorded in the addon's
  `docs/PORTRAIT-CONTRACT.md` and in code comments. Do not re-derive them.
- **Mutation-test every claim.** Two tests in M1 passed with the behaviour broken until this was
  done; `if (false)` does not compile under warnings-as-errors, so write mutations that build.

## Testing against the real game

The owner's Forever install is `C:\Program Files (x86)\World of Warcraft\_forever_` (live from
2026-11-04; the beta's `_classic_beta_` and its registry `Beta` key are left over and ignored), with
two accounts under `WTF\Account`. Never run the app or a pass against it in a way that deletes
screenshots or rewrites `AltStableCutouts` without saying so first — copy what you need into the
scratchpad (M1's cross-check did exactly that). Deploying the ADDON for in-game testing is
pre-approved and done from `C:\Projects\WoW\AltStable` with `pwsh Tools/deploy.ps1`.
