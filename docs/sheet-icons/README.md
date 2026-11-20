# Google Sheet section icons

The Google Sheet export's masthead icons (#82): AltStable's own sidebar art,
`Media/Icons/account-summary.tga`, `gear-progression.tga`, `skills.tga` and `reputations.tga`
in the addon's repo, converted to upright 64 px PNGs.

The export points at them by a fixed commit
(`raw.githubusercontent.com/Spotnick2/AltStableCompanion/<commit>/docs/sheet-icons/...`,
`SheetExport.IconCommit`), so a player's sheet never changes under them. Google's servers fetch
them, not the app. To change one, add the new file under a new name or commit, and move
`IconCommit` to it.
