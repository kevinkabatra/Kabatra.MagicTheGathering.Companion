# MTG Companion

A table-side helper for playing Magic: The Gathering with friends. It runs in any web browser — put it on a phone or tablet in the middle of the table and everyone can see and tap their own life total.

Built in C# with Blazor WebAssembly (.NET 8), so the whole app runs in the browser with no server.

## Features

- **Life totals** for 1–8 players, with ±1 / ±5 / ±10 buttons
- **Formats:** Standard (20), Commander (40), Brawl (25), Oathbreaker (20), or custom starting life
- **Poison, energy, and experience** counters
- **Commander damage** from each opponent, tracked separately; it also takes the life off automatically
- **Knock-outs:** 0 life, 10 poison, or 21 commander damage from one commander marks a player as out; the last one standing wins
- **Turn tracker** that skips players who are out
- **Undo** for any mistake
- **Dice tray:** d4–d20, coin flip, random player
- **Game log** of everything that happened, with repeated taps merged ("-5" instead of five "-1"s)
- **Rotate** a card 180° for the player across the table
- **Auto-save:** the game survives a refresh or a locked phone

## Run it locally

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet run --project src/Kabatra.MagicTheGathering.Companion
```

Then open the URL it prints (for example `http://localhost:5000`).

Run the tests:

```bash
dotnet test
```

## Project layout

| Path | What's in it |
| --- | --- |
| `src/Kabatra.MagicTheGathering.Companion.Core` | Game rules and state (players, counters, turns, undo, save/load). No UI code. |
| `src/Kabatra.MagicTheGathering.Companion` | The Blazor WebAssembly app (pages, components, styles). |
| `tests/Kabatra.MagicTheGathering.Companion.Tests` | xUnit tests for the core library. |

## Hosting

Every push to `main` builds and deploys the app to GitHub Pages via `.github/workflows/deploy-pages.yml`. In the repository's **Settings → Pages**, set **Source** to **GitHub Actions** once.

## Ideas for later

- Card lookup (Scryfall API)
- Deck lists and format legality
- Seating / pod pairing for game nights

## Legal

MTG Companion is unofficial Fan Content permitted under the Wizards of the Coast Fan Content Policy. Not approved or endorsed by Wizards. Portions of the materials used are property of Wizards of the Coast. © Wizards of the Coast LLC.

Code is released under the [MIT License](LICENSE).
