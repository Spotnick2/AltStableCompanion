# CLAUDE.md — Claude-specific overlay

`AGENTS.md` is the shared baseline (layout, build/test, conventions). This file only adds what
a Claude session here needs and cannot learn from the code. Sessions in this folder do NOT see
the memories of the AltStable addon's sessions, so the rules that matter are repeated here.

## Where things stand

- The app is AltStable#89's endgame. The approved plan (milestones M1 core, M2 Avalonia tray
  shell, M3 release workflow) is `C:\Users\nicol\.claude\plans\bubbly-singing-pine.md` — read
  its "companion app" and "Adversarial review" sections before starting a milestone.
- M1 (core + tests) is PR #1. M2 is next: Avalonia shell, own `Win32TrayIcon` via P/Invoke
  (a HIDDEN TOP-LEVEL window, not message-only — message-only windows never receive
  `WM_TASKBARCREATED`), one status window, single instance. No Run key, no `ITrayHost` yet.
- Open issues worth knowing: #2 (troubleshooting logs / diagnostics zip — leave room for its
  button in M2's window). In the addon repo: #121 (Codex-enhanced portraits), #124 (auto-capture).

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
- **Measured beats reasoned.** Facts about the client and this machine (RLE screenshots, `WowB.exe`,
  the registry keys, `AltStablePortraits = nil`) were measured; they are recorded in the addon's
  `docs/PORTRAIT-CONTRACT.md` and in code comments. Do not re-derive them.
- **Mutation-test every claim.** Two tests in M1 passed with the behaviour broken until this was
  done; `if (false)` does not compile under warnings-as-errors, so write mutations that build.

## Testing against the real game

The owner's Forever install is `C:\Program Files (x86)\World of Warcraft\_classic_beta_`, with
two accounts under `WTF\Account`. Never run the app or a pass against it in a way that deletes
screenshots or rewrites `AltStableCutouts` without saying so first — copy what you need into the
scratchpad (M1's cross-check did exactly that). Deploying the ADDON for in-game testing is
pre-approved and done from `C:\Projects\AltStable` with `pwsh Tools/deploy.ps1`.
