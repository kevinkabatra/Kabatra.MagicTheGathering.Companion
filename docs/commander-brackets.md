# Commander Brackets

How the app estimates a Commander deck's bracket. The code is in
[`CommanderBrackets.cs`](../src/Kabatra.MagicTheGathering.Companion.Core/Decks/CommanderBrackets.cs), and its card lists are in
[`BracketCardLists.cs`](../src/Kabatra.MagicTheGathering.Companion.Core/Decks/BracketCardLists.cs).

The app owns this logic. It calls no other service: the rating comes from card names alone, so the deck list can rate
every deck without looking up any cards.

## The official rules (beta, last updated February 9, 2026)

| Bracket | Game Changers | Mass land denial | Extra turns | Two-card infinite combos | Expected game length |
|---|---|---|---|---|---|
| 1 Exhibition | None | No | No | No | 9+ turns |
| 2 Core | None | No | Low quantities, not chained or looped | No | 8+ turns |
| 3 Upgraded | Up to 3 | No | Low quantities, not chained or looped | No early-game ones | 6+ turns |
| 4 Optimized | Any | Allowed | Allowed | Allowed | 4+ turns |
| 5 cEDH | Any | Allowed | Allowed | Allowed | Any turn |

- **Tutors** stopped counting on October 21, 2025. The strongest tutors are on the Game Changers list instead.
- **Brackets 1 and 5** depend on what the deck is built for. Bracket 1 is themed and ultra-casual; Bracket 5 is built for
  the cEDH metagame. The cards don't decide either one, so the app never estimates them. Moxfield, Archidekt and
  TableCommander leave them to the player too.
- **Mass land denial**, in Wizards' words, is cards that "destroy, exile, and bounce other lands, keep lands tapped, or
  change what mana is produced by four or more lands per player without replacing them". Wizards' examples are
  Armageddon, Ruination, Sunder, Winter Orb and Blood Moon. There is no official full list.

## How the app decides

The highest rule that matches wins.

1. **Bracket 4** if the deck has any of:
   - 4 or more Game Changers
   - any mass land denial
   - a two-card combo that costs 6 mana or less in total, meaning it can come together early
   - more than 3 extra-turn cards
2. **Bracket 3** if the deck has 1–3 Game Changers, or a two-card combo that costs more than 6 mana.
3. **Bracket 2** otherwise.

Only the commander and main deck count. The sideboard doesn't. Brackets are shown only for decks in the Commander format.

Three numbers are our own reading of rules that give no figure:

- "Early" combos cost 6 mana or less. Wizards' wording is "cheaply and in about the first six or so turns".
- "Low quantities" of extra turns means 3 or fewer.

Both are constants in `BracketCardLists` if you want to change them.

## Keeping it current

- **Game Changers:** the official list of 53 cards. Wizards changes it with ban-list announcements; the next one is on
  November 30, 2026. To check the count, search Scryfall for `is:gamechanger`.
- **Mass land denial, extra turns and combos:** curated by us from Wizards' definitions and well-known cards. Add cards
  as they're printed.
- After any change, update `BracketCardLists.UpdatedOn` and the count in `CommanderBracketTests`.

## Sources

- Introducing Commander Brackets Beta (Feb 11, 2025): https://magic.wizards.com/en/news/announcements/introducing-commander-brackets-beta
- Beta update, Apr 22, 2025: https://magic.wizards.com/en/news/announcements/commander-brackets-beta-update-april-22-2025
- Beta update, Oct 21, 2025 (tutors removed, 10 Game Changers removed, turn expectations added): https://magic.wizards.com/en/news/announcements/commander-brackets-beta-update-october-21-2025
- Beta update, Feb 9, 2026 (Farewell and Biorhythm added): https://magic.wizards.com/en/news/announcements/commander-brackets-beta-update-february-9-2026
- Commander format page: https://magic.wizards.com/en/formats/commander
