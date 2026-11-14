namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

/// <summary>A pair of cards that together make an infinite loop or win the game on the spot.</summary>
/// <param name="Cost">
/// Mana needed to cast both pieces the usual way (a reanimated creature counts as the reanimation spell).
/// At or below <see cref="BracketCardLists.EarlyComboMaxCost"/> the combo can come together in the early game.
/// </param>
public sealed record TwoCardCombo(string First, string Second, int Cost, string Result)
{
    public bool IsEarly => Cost <= BracketCardLists.EarlyComboMaxCost;
}

/// <summary>
/// The card lists behind <see cref="CommanderBrackets"/>. The app keeps these itself rather than asking
/// another service, so the rating works offline and from card names alone.
/// <para>
/// Game Changers is Wizards of the Coast's official list. Wizards gives a definition and a few examples of
/// mass land denial but no full list, and no list of extra-turn cards or combos, so those lists are curated
/// here from the definition and from well-known cards. When Wizards updates the brackets, update these lists
/// and <see cref="UpdatedOn"/>.
/// </para>
/// </summary>
public static class BracketCardLists
{
    /// <summary>The Commander Brackets update these lists match.</summary>
    public static readonly DateOnly UpdatedOn = new(2026, 2, 9);

    /// <summary>
    /// The most mana a two-card combo can cost and still count as "early game". Wizards' wording for Bracket 3
    /// is "no two-card infinite combos that can happen cheaply and in about the first six or so turns".
    /// </summary>
    public const int EarlyComboMaxCost = 6;

    /// <summary>
    /// The most extra-turn cards a deck can hold and still fit Brackets 2 and 3, which allow extra turns
    /// "only in low quantities", not chained or looped. Wizards doesn't give a number. Three is a common reading.
    /// </summary>
    public const int MaxCasualExtraTurns = 3;

    /// <summary>The official Game Changers list: 53 cards as of the February 9, 2026 update.</summary>
    public static IReadOnlySet<string> GameChangers { get; } = Set(
        "Ad Nauseam",
        "Ancient Tomb",
        "Aura Shards",
        "Biorhythm",
        "Bolas's Citadel",
        "Braids, Cabal Minion",
        "Chrome Mox",
        "Coalition Victory",
        "Consecrated Sphinx",
        "Crop Rotation",
        "Cyclonic Rift",
        "Demonic Tutor",
        "Drannith Magistrate",
        "Enlightened Tutor",
        "Farewell",
        "Field of the Dead",
        "Fierce Guardianship",
        "Force of Will",
        "Gaea's Cradle",
        "Gamble",
        "Gifts Ungiven",
        "Glacial Chasm",
        "Grand Arbiter Augustin IV",
        "Grim Monolith",
        "Humility",
        "Imperial Seal",
        "Intuition",
        "Jeska's Will",
        "Lion's Eye Diamond",
        "Mana Vault",
        "Mishra's Workshop",
        "Mox Diamond",
        "Mystical Tutor",
        "Narset, Parter of Veils",
        "Natural Order",
        "Necropotence",
        "Notion Thief",
        "Opposition Agent",
        "Orcish Bowmasters",
        "Panoptic Mirror",
        "Rhystic Study",
        "Seedborn Muse",
        "Serra's Sanctum",
        "Smothering Tithe",
        "Survival of the Fittest",
        "Teferi's Protection",
        "Tergrid, God of Fright",
        "Thassa's Oracle",
        "The One Ring",
        "The Tabernacle at Pendrell Vale",
        "Underworld Breach",
        "Vampiric Tutor",
        "Worldly Tutor");

    /// <summary>
    /// Mass land denial: cards that "destroy, exile, and bounce other lands, keep lands tapped, or change what
    /// mana is produced by four or more lands per player without replacing them". Armageddon, Ruination,
    /// Sunder, Winter Orb and Blood Moon are the examples Wizards gives.
    /// </summary>
    public static IReadOnlySet<string> MassLandDenial { get; } = Set(
        "Acid Rain",
        "Apocalypse",
        "Armageddon",
        "Back to Basics",
        "Blood Moon",
        "Burning of Xinye",
        "Bust", // Boom // Bust
        "Cataclysm",
        "Catastrophe",
        "Contamination",
        "Death Cloud",
        "Decree of Annihilation",
        "Desolation Angel",
        "Destructive Force",
        "Devastation",
        "Epicenter",
        "Fall of the Thran",
        "Flashfires",
        "Global Ruin",
        "Harbinger of the Seas",
        "Hokori, Dust Drinker",
        "Impending Disaster",
        "Infernal Darkness",
        "Jokulhaups",
        "Keldon Firebombers",
        "Magus of the Moon",
        "Mana Vortex",
        "Myojin of Infinite Rage",
        "Obliterate",
        "Ravages of War",
        "Rising Waters",
        "Ruination",
        "Static Orb",
        "Stasis",
        "Sunder",
        "Tectonic Break",
        "Thoughts of Ruin",
        "Tsunami",
        "Wildfire",
        "Winter Orb",
        "Worldfire");

    /// <summary>Cards that give you extra turns. Time Walk is banned in Commander but listed for completeness.</summary>
    public static IReadOnlySet<string> ExtraTurns { get; } = Set(
        "Alrund's Epiphany",
        "Beacon of Tomorrows",
        "Capture of Jingzhou",
        "Chance for Glory",
        "Expropriate",
        "Final Fortune",
        "Karn's Temporal Sundering",
        "Last Chance",
        "Lighthouse Chronologist",
        "Lost Isle Calling",
        "Magistrate's Scepter",
        "Magosi, the Waterveil",
        "Medomai the Ageless",
        "Nexus of Fate",
        "Notorious Throng",
        "Part the Waterveil",
        "Plea for Power",
        "Ral Zarek",
        "Sage of Hours",
        "Savor the Moment",
        "Second Chance",
        "Seedtime",
        "Stitch in Time",
        "Temporal Extortion",
        "Temporal Manipulation",
        "Temporal Mastery",
        "Temporal Trespass",
        "Time Stretch",
        "Time Vault",
        "Time Walk",
        "Time Warp",
        "Timestream Navigator",
        "Ugin's Nexus",
        "Walk the Aeons",
        "Wanderwine Prophets",
        "Warrior's Oath");

    /// <summary>Well-known two-card infinite combos and instant wins.</summary>
    public static IReadOnlyList<TwoCardCombo> TwoCardCombos { get; } =
    [
        // Instant wins with an empty library
        new("Thassa's Oracle", "Demonic Consultation", 3, "wins the game"),
        new("Thassa's Oracle", "Tainted Pact", 4, "wins the game"),
        new("Laboratory Maniac", "Demonic Consultation", 4, "wins the game"),
        new("Laboratory Maniac", "Tainted Pact", 5, "wins the game"),
        new("Jace, Wielder of Mysteries", "Demonic Consultation", 5, "wins the game"),
        new("Jace, Wielder of Mysteries", "Tainted Pact", 6, "wins the game"),

        // Infinite mana
        new("Isochron Scepter", "Dramatic Reversal", 4, "infinite mana (with mana rocks)"),
        new("Grim Monolith", "Power Artifact", 4, "infinite colorless mana"),
        new("Basalt Monolith", "Power Artifact", 5, "infinite colorless mana"),
        new("Basalt Monolith", "Rings of Brighthearth", 6, "infinite colorless mana"),
        new("Basalt Monolith", "Kinnan, Bonder Prodigy", 5, "infinite colorless mana"),
        new("Pili-Pala", "Grand Architect", 5, "infinite mana"),
        new("Devoted Druid", "Vizier of Remedies", 4, "infinite green mana"),
        new("Deadeye Navigator", "Peregrine Drake", 11, "infinite mana"),
        new("Deadeye Navigator", "Palinchron", 13, "infinite mana"),

        // Infinite creatures or copies
        new("Kiki-Jiki, Mirror Breaker", "Corridor Monitor", 6, "infinite hasty copies"),
        new("Kiki-Jiki, Mirror Breaker", "Pestermite", 7, "infinite hasty copies"),
        new("Kiki-Jiki, Mirror Breaker", "Deceiver Exarch", 7, "infinite hasty copies"),
        new("Kiki-Jiki, Mirror Breaker", "Village Bell-Ringer", 7, "infinite hasty copies"),
        new("Kiki-Jiki, Mirror Breaker", "Felidar Guardian", 8, "infinite hasty copies"),
        new("Kiki-Jiki, Mirror Breaker", "Restoration Angel", 8, "infinite hasty copies"),
        new("Kiki-Jiki, Mirror Breaker", "Zealous Conscripts", 9, "infinite hasty copies"),
        new("Splinter Twin", "Pestermite", 7, "infinite hasty copies"),
        new("Splinter Twin", "Deceiver Exarch", 7, "infinite hasty copies"),
        new("Saheeli Rai", "Felidar Guardian", 7, "infinite hasty cats"),
        new("Dualcaster Mage", "Twinflame", 5, "infinite hasty copies"),
        new("Dualcaster Mage", "Heat Shimmer", 6, "infinite hasty copies"),
        new("Earthcraft", "Squirrel Nest", 4, "infinite squirrels"),
        new("Godo, Bandit Warlord", "Helm of the Host", 10, "infinite combat steps"),

        // Infinite damage, drain or mill
        new("Heliod, Sun-Crowned", "Walking Ballista", 5, "infinite damage"),
        new("Mikaeus, the Unhallowed", "Triskelion", 12, "infinite damage"),
        new("Painter's Servant", "Grindstone", 3, "mills each library"),
        new("Mindcrank", "Duskmantle Guardian", 5, "mills each library"),
        new("Niv-Mizzet, Parun", "Curiosity", 7, "infinite damage"),
        new("Niv-Mizzet, the Firebrand", "Curiosity", 7, "infinite damage"),
        new("Niv-Mizzet, Parun", "Ophidian Eye", 9, "infinite damage"),
        new("Exquisite Blood", "Sanguine Bond", 10, "infinite drain"),
        new("Exquisite Blood", "Vito, Thorn of the Dusk Rose", 8, "infinite drain"),
        new("Exquisite Blood", "Marauding Blight-Priest", 8, "infinite drain"),
        new("Exquisite Blood", "Cliffhaven Vampire", 9, "infinite drain"),
        new("Sanguine Bond", "Bloodthirsty Conqueror", 10, "infinite drain"),
        new("Food Chain", "Squee, the Immortal", 6, "infinite creature mana"),
        new("Food Chain", "Eternal Scourge", 6, "infinite creature mana"),
        new("Food Chain", "Misthollow Griffin", 7, "infinite creature mana"),

        // Infinite loops through the graveyard
        new("Worldgorger Dragon", "Animate Dead", 2, "infinite mana and ETB triggers"),
        new("Worldgorger Dragon", "Dance of the Dead", 2, "infinite mana and ETB triggers"),
        new("Worldgorger Dragon", "Necromancy", 3, "infinite mana and ETB triggers"),
    ];

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
}
