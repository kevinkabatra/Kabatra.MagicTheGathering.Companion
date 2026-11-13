# MTG Companion

A table-side helper for playing Magic: The Gathering with friends. It runs in any web browser — put it on a phone or tablet in the middle of the table and everyone can see and tap their own life total.

**Play it now: https://kevinkabatra.github.io/Kabatra.MagicTheGathering.Companion/**

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
- **Decks:** build decks with card search from [Scryfall](https://scryfall.com), import and export Arena / MTGO / Moxfield lists, and check legality (deck size, copy limits, bans, commander rules and color identity) for Commander, Standard, Pioneer, Modern, Legacy, Vintage, Pauper, Brawl and Oathbreaker
- **Shared playgroup decks:** sign in with an email link, join your playgroup with an invite code, and everyone sees the group's decks (needs a free Supabase project; without one, decks are saved on the device)
- **Decks at the table:** pick each player's deck when starting a game; the format follows the decks and the player's card shows the commander's art

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

## Shared decks with Supabase (optional)

Without this, everything works but decks stay in the browser they were made in.

1. Create a free project at [supabase.com](https://supabase.com).
2. In the project's **SQL Editor**, run [`supabase/schema.sql`](supabase/schema.sql). It creates the `playgroups`, `playgroup_members` and `decks` tables, row-level security so only members can see a group's decks, and the `create_playgroup` / `join_playgroup` functions.
3. In **Authentication → URL Configuration**, set **Site URL** to the live site and add these **Redirect URLs**:
   - `https://kevinkabatra.github.io/Kabatra.MagicTheGathering.Companion/**`
   - `http://localhost:5243/**` (for running locally)
4. Optional but handy on phones: in **Authentication → Email Templates → Magic Link**, add `{{ .Token }}` to the email so it includes a 6-digit code. The app accepts the code too, which helps when the link opens in a different browser than the app.
5. In **Settings → API Keys**, copy the **publishable** key (it starts with `sb_publishable_`). The **Project URL** is in the dashboard's **Connect** dialog, or is `https://<project-ref>.supabase.co`. Put both into `src/Kabatra.MagicTheGathering.Companion/wwwroot/appsettings.json`:

   ```json
   { "Supabase": { "Url": "https://YOUR-PROJECT.supabase.co", "PublishableKey": "sb_publishable_..." } }
   ```

   The publishable key is meant to be public; the database's row-level security is what protects the data. **Never use a secret key** (`sb_secret_...`) or the legacy `service_role` key here: they bypass row-level security, and the app refuses to use them.

   *Legacy keys:* Supabase is retiring the old `anon` / `service_role` keys by the end of 2026. Until then, an `anon` key also works, either in `PublishableKey` or in the old `AnonKey` setting. Once the app uses the publishable key, you can disable the legacy keys under **Settings → API Keys**.

Supabase's built-in email sender is rate-limited (a few emails an hour), which is fine for a playgroup. For more, connect your own SMTP server under **Authentication → Emails**.

## Card data

Card names, images, and legality come from the [Scryfall API](https://scryfall.com/docs/api), which is free and needs no key. Lookups are cached in the browser for three days, and requests are spaced out to respect Scryfall's rate limits.

## Project layout

| Path | What's in it |
| --- | --- |
| `src/Kabatra.MagicTheGathering.Companion.Core` | Game rules and state (players, counters, turns, undo, save/load), deck rules (deck lists, legality), and the Scryfall and Supabase clients. No UI code. |
| `src/Kabatra.MagicTheGathering.Companion` | The Blazor WebAssembly app (pages, components, styles). |
| `tests/Kabatra.MagicTheGathering.Companion.Tests` | xUnit tests for the core library. |
| `supabase/schema.sql` | Database tables, security policies and functions for shared decks. |

## Hosting

Every push to `main` builds and deploys the app to GitHub Pages via `.github/workflows/deploy-pages.yml`. In the repository's **Settings → Pages**, set **Source** to **GitHub Actions** once.

## Ideas for later

- Seating / pod pairing for game nights

## Legal

Card data and images are provided by [Scryfall](https://scryfall.com). MTG Companion is not produced by or endorsed by Scryfall.

MTG Companion is unofficial Fan Content permitted under the Wizards of the Coast Fan Content Policy. Not approved or endorsed by Wizards. Portions of the materials used are property of Wizards of the Coast. © Wizards of the Coast LLC.

Code is released under the [MIT License](LICENSE).
