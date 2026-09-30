# CLAUDE.md — Claude-specific overlay

`AGENTS.md` is the shared baseline (layout, build/test, conventions). This file only adds what
a Claude session here needs and cannot learn from the code. Sessions in this folder do NOT see
the memories of the AltStable addon's sessions, so the rules that matter are repeated here.

## Where things stand

- The app is AltStable#89's endgame. Its milestones - M1 core, M2 Avalonia tray shell, M3
  release workflow - are all on `main`. What is left is the backlog: #2 (diagnostics zip),
  #15 (version and About on the Help page), #16 (update checks, after releases exist), #17
  (optional Codex-enhanced portraits; a plan first, through Codex). In the addon repo: #124
  (auto-capture), #131 (a sidecar-less cutout's crop origin).
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
