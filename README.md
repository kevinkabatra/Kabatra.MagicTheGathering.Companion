# Someone Else's Table

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
- **No account needed:** games, deck building, card search, scanning, list import/export, legality checks and prebuilt decks all work without signing in. A guest's decks are saved on the device
- **Optional sign-in with Google or Apple:** adds a player profile with your collection, playgroups, shared decks and play history, on every device. No passwords to remember. The first time you sign in, you can move the decks you made as a guest to your profile
- **Playgroups** (signed in): create as many as you like and share them with a join code, or add people by their player code. Everyone in a group sees its players, their decks and its play history. The group's admin (its creator, until they hand it over) renames, deletes and manages who's in it
- **Decks at the table:** pick each player's deck when starting a game; the format follows the decks and the player's card shows the commander's art
- **Player profiles** (signed in): everyone who signs in gets a profile, with the cards they own (search, scan or paste a list) and what their collection is worth, plus the decks they own. Anyone can still play anyone's deck. Friends without an account can have a profile too; whoever made it gives them its invite code, and they claim it the first time they sign in. Wrong invite codes are limited: 3 in a row means a 15-minute wait, and 10 in all blocks the account
- **Play history** (signed in): pick each seat's profile when starting a game, and the game is saved when you start the next one: who played which deck, and who won. Each profile shows games, wins and win rate, overall and per deck, and each deck shows its record across everyone who played it

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

## Set up Supabase (for sign-in)

Signing in is optional. Without Supabase, everyone uses the app as a guest and decks stay on the device. With a free [Supabase](https://supabase.com) project, people can sign in, and their profiles, playgroups, decks and play history are kept there. Supabase Auth handles sign-in.

1. Create a free project at [supabase.com](https://supabase.com).
2. In the project's **SQL Editor**, run [`supabase/schema.sql`](supabase/schema.sql). It creates:
   - `player_profiles`: one per signed-in user, plus unclaimed profiles for friends without an account
   - `playgroups` and `playgroup_profiles`: playgroups, each with one admin, and their profiles (many-to-many)
   - `decks`: decks owned by a profile, visible in every playgroup that profile is in
   - `game_records`: each playgroup's play history
   - `claim_attempts`: rate limiting for profile invite codes

   It also sets up row-level security and the functions the app calls (`create_my_profile`, `claim_profile`, `create_playgroup`, `join_playgroup`, and others).

   **Upgrading from the version with email sign-in?** The data model changed. After deploying, run [`supabase/wipe.sql`](supabase/wipe.sql) once, then `schema.sql`. **The wipe permanently deletes every player profile, playgroup, deck and recorded game.** Sign-in accounts are kept.
3. In **Authentication → URL Configuration**, set **Site URL** to the live site and add these **Redirect URLs**:
   - `https://kabatra-mtg-companion.pages.dev/**`
   - `http://localhost:5243/**` (for running locally)
4. Turn on the sign-in providers under **Authentication → Sign In / Providers**. Each provider's page in Supabase shows the **Callback URL** (`https://<project-ref>.supabase.co/auth/v1/callback`) to give the provider.
   - **Google:** in the [Google Cloud console](https://console.cloud.google.com/apis/credentials), set up the OAuth consent screen, then create an **OAuth client ID** of type *Web application*. Add the Supabase callback URL under **Authorized redirect URIs**. Paste the client ID and secret into Supabase's Google provider.
   - **Apple:** needs a paid [Apple Developer](https://developer.apple.com) account. Create a **Services ID** with *Sign in with Apple* turned on, and set its return URL to the Supabase callback URL. Then create a **Sign in with Apple key** (.p8). In Supabase's Apple provider, enter the Services ID as the client ID and generate the secret from the key, Team ID and Key ID. Apple secrets expire after at most six months, so set a reminder to make a new one.
   - **Other providers:** the app never handles passwords, but any OAuth provider Supabase supports can be added. Turn it on in Supabase, then list its id in `appsettings.json` (below), for example `"Providers": ["google", "apple", "github"]`.
   - Turn **Email** sign-in off, since the app no longer offers it.
5. In **Settings → API Keys**, copy the **publishable** key (it starts with `sb_publishable_`). The **Project URL** is in the dashboard's **Connect** dialog, or is `https://<project-ref>.supabase.co`. Put both into `src/Kabatra.MagicTheGathering.Companion/wwwroot/appsettings.json`:

   ```json
   {
     "Supabase": { "Url": "https://YOUR-PROJECT.supabase.co", "PublishableKey": "sb_publishable_..." },
     "Auth": { "Providers": ["google", "apple"] }
   }
   ```

   The publishable key is meant to be public; the database's row-level security is what protects the data. **Never use a secret key** (`sb_secret_...`) or the legacy `service_role` key here: they bypass row-level security, and the app refuses to use them.

   *Legacy keys:* Supabase is retiring the old `anon` / `service_role` keys by the end of 2026. Until then, an `anon` key also works, either in `PublishableKey` or in the old `AnonKey` setting. Once the app uses the publishable key, you can disable the legacy keys under **Settings → API Keys**.

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

Card data and images are provided by [Scryfall](https://scryfall.com). Someone Else's Table is not produced by or endorsed by Scryfall.

Someone Else's Table is unofficial Fan Content permitted under the Wizards of the Coast Fan Content Policy. Not approved or endorsed by Wizards. Portions of the materials used are property of Wizards of the Coast. © Wizards of the Coast LLC.

Code is released under the [MIT License](LICENSE).
