# World of Warcraft Forever logos

These three files are Blizzard Entertainment's official logos for World of Warcraft Forever,
as published on the Blizzard press site
(https://blizzard.gamespress.com/World-of-Warcraft-Forever-Classic, "Logos"). They are
kept here exactly as downloaded: not cropped, recoloured, or otherwise changed.

| File | What it is |
| --- | --- |
| `WoW-Forever-Logo.png` | The emblem, in colour, 2000 × 1692, transparent |
| `WoW-Forever-Wordmark-Black.png` | The wordmark in black, 2000 × 1125, transparent |
| `WoW-Forever-Wordmark-White.png` | The wordmark in white, 2000 × 1125, transparent |

They are not covered by this repository's MIT licence. World of Warcraft is a trademark or
registered trademark of Blizzard Entertainment, Inc., in the U.S. and/or other countries.
The app is a free, non-commercial fan tool and uses the emblem under Blizzard's trademark
usage guidelines
(https://www.blizzard.com/en-us/legal/38fd0408-8431-469a-99bc-2cd9eb9462c8/blizzard-entertainment-trademark-usage-guidelines),
which allow the logo to be resized and nothing else.

The only copy the app ships is `src/AltStableCompanion.App/Assets/wow-forever.png`: the
emblem, resized to 80 × 68 (twice the 34 px it is drawn at) and nothing more. To remake it:

```
python -c "from PIL import Image; im = Image.open('Reference/Blizzard/WoW-Forever-Logo.png'); im.resize((80, 68), Image.LANCZOS).save('src/AltStableCompanion.App/Assets/wow-forever.png', optimize=True)"
```
