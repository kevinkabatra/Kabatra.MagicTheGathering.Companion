# MTG Companion

A table-side helper for playing Magic: The Gathering with friends. It runs in any web browser — put it on a phone or tablet in the middle of the table and everyone can see and tap their own life total.

**Play it now: https://kabatra-mtg-companion.pages.dev/**

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
- **Scan cards:** point the camera at a physical card (or pick a photo) to add it to a deck. The card's name, set and collector number are read on the device with [Tesseract.js](https://tesseract.projectnaptha.com/), so photos never leave the phone, and matched to the exact printing on Scryfall
- **Prebuilt decks:** import any official precon (Commander decks, Duel Decks, Challenger Decks, Starter Kits and more) in one tap, with the exact printings from the box. Deck lists come from [MTGJSON](https://mtgjson.com)
- **Shared playgroup decks:** sign in with an email link, join your playgroup with an invite code, and everyone sees the group's decks, players and play history (needs a free Supabase project; without one, they're saved on the device)
- **Decks at the table:** pick each player's deck when starting a game; the format follows the decks and the player's card shows the commander's art
- **Player profiles:** a profile for each person in the playgroup, with the cards they own (search, scan or paste a list) and what their collection is worth, plus the decks they own. Anyone can still play anyone's deck. Profiles don't need an account, so friends without one can have one too
- **Play history:** pick each seat's profile when starting a game, and the game is saved when you start the next one: who played which deck, and who won. Each profile shows games, wins and win rate, overall and per deck, and each deck shows its record across everyone who played it

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

Without this, everything works but decks, player profiles and play history stay in the browser they were made in.

1. Create a free project at [supabase.com](https://supabase.com).
2. In the project's **SQL Editor**, run [`supabase/schema.sql`](supabase/schema.sql). It creates the `playgroups`, `playgroup_members`, `decks`, `player_profiles` and `game_records` tables, row-level security so only members can see a group's data, and the `create_playgroup` / `join_playgroup` functions.

   **Already set up from an older version?** Run `schema.sql` again after updating: it only adds what's missing and keeps your decks. Until you do, decks keep working but the Players page asks for the update.
3. In **Authentication → URL Configuration**, set **Site URL** to the live site and add these **Redirect URLs**:
   - `https://kabatra-mtg-companion.pages.dev/**`
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
| `src/Kabatra.MagicTheGathering.Companion.Core` | Game rules and state (players, counters, turns, undo, save/load), deck rules (deck lists, legality), player profiles and play history, and the Scryfall and Supabase clients. No UI code. |
| `src/Kabatra.MagicTheGathering.Companion` | The Blazor WebAssembly app (pages, components, styles). |
| `tests/Kabatra.MagicTheGathering.Companion.Tests` | xUnit tests for the core library. |
| `supabase/schema.sql` | Database tables, security policies and functions for shared decks, player profiles and play history. |

## Hosting

Every push to `main` builds the app and deploys it to [Cloudflare Pages](https://pages.cloudflare.com) via `.github/workflows/deploy.yml`. Cloudflare's free plan allows commercial use; GitHub Pages doesn't.

One-time setup:

1. Create a free [Cloudflare](https://dash.cloudflare.com/sign-up) account.
2. Under **My Profile → API Tokens**, create a token with the **Cloudflare Pages: Edit** permission.
3. In this repository's **Settings → Secrets and variables → Actions**, add two secrets:
   - `CLOUDFLARE_API_TOKEN`: the token
   - `CLOUDFLARE_ACCOUNT_ID`: your account ID, shown on **Workers & Pages → Overview** and in the dashboard's URL
4. Run the **Deploy to Cloudflare Pages** workflow (or push to `main`). The first run creates the `kabatra-mtg-companion` project, served at `https://kabatra-mtg-companion.pages.dev`. To use another name, add a repository variable `CLOUDFLARE_PAGES_PROJECT`.

Until the secrets are added, the workflow builds the app but skips the deploy. A custom domain can be added under the project's **Custom domains** tab; if you add one, add it to the Supabase redirect URLs too.

## Ideas for later

- Seating / pod pairing for game nights

## Legal

Card data and images are provided by [Scryfall](https://scryfall.com). MTG Companion is not produced by or endorsed by Scryfall.

MTG Companion is unofficial Fan Content permitted under the Wizards of the Coast Fan Content Policy. Not approved or endorsed by Wizards. Portions of the materials used are property of Wizards of the Coast. © Wizards of the Coast LLC.

Code is released under the [MIT License](LICENSE).
