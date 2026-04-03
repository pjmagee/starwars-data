# 003 — AI Game Master: Star Wars 5e RPG

**Status:** Draft  
**Created:** 2026-04-03  
**Author:** Patrick Magee + Claude

## Vision

An AI-powered Game Master that runs Star Wars tabletop RPG sessions using SW5e (Star Wars 5th Edition) rules, grounded in the app's existing knowledge graph, semantic search, and galaxy map. Players create characters, choose a timeline era, and play through narratively rich adventures where the GM has deep knowledge of the Star Wars universe — every NPC, planet, faction, and historical event is backed by real Wookieepedia data.

Feature-flagged to local development only during design and implementation.

## Why This Works Here

Most AI GM experiments suffer from two problems: the AI makes up a shallow world, and it doesn't follow game rules consistently. This project solves both:

1. **World depth** — 166K entities, 694K relationships, 800K article passages. The GM doesn't invent "a cantina on some planet" — it knows Chalmun's Spaceport Cantina is on Tatooine in Mos Eisley, owned by the Wookiee Chalmun, and can describe the regulars who'd be there in 0 BBY vs 10 ABY.

2. **Temporal consistency** — The KG tracks what exists when. If the campaign is set in 3 ABY, the GM knows the Empire controls Coruscant, Alderaan is destroyed, and Vader is hunting Luke. It won't introduce characters who are dead or factions that don't exist yet.

3. **Rule grounding** — SW5e is open-source structured data. Rules, stat blocks, and mechanics can be encoded as tools, not just prompt text. The GM calls `roll_ability_check("Dexterity", 15)` rather than narrating a vague outcome.

## Game Loop

```
┌─────────────────────────────────────────────┐
│              CHARACTER CREATION              │
│  Species → Class → Abilities → Background   │
│  → Equipment → Force Powers → Starting Era  │
└──────────────────┬──────────────────────────┘
                   ▼
┌─────────────────────────────────────────────┐
│              EXPLORATION PHASE              │
│  GM describes scene (grounded in KG/lore)   │
│  Player declares action ("I go to the       │
│  cantina and ask about smuggling jobs")      │
│  GM resolves with tools + narrative          │
└──────────────────┬──────────────────────────┘
                   ▼
┌─────────────────────────────────────────────┐
│           SKILL CHECK / ENCOUNTER           │
│  GM determines check type + DC              │
│  Calls roll_dice / check_ability tools      │
│  Narrates outcome based on success/failure  │
└──────────────────┬──────────────────────────┘
                   ▼
┌─────────────────────────────────────────────┐
│              COMBAT (if triggered)          │
│  Initiative → Turn order → Actions          │
│  Attack rolls, damage, conditions, HP       │
│  NPCs act based on their nature/faction     │
│  Combat ends → loot/consequences            │
└──────────────────┬──────────────────────────┘
                   ▼
┌─────────────────────────────────────────────┐
│              PROGRESSION                    │
│  XP award → Level up (if threshold)         │
│  Story consequences update campaign state   │
│  GM advances narrative                      │
└─────────────────────────────────────────────┘
```

## Data Architecture

### Collections (namespace: `game.*`)

| Collection | Purpose |
|------------|---------|
| `game.sessions` | Campaign state: current scene, quest log, timeline year, location, NPC relationships, story flags |
| `game.characters` | Player character sheets: stats, class, level, HP, inventory, Force powers, conditions |
| `game.combat` | Active combat state: initiative order, combatant HP/conditions, round/turn tracking |
| `game.log` | Immutable session log: every narrative beat, dice roll, rule citation, and GM decision with timestamps |
| `game.rules` | SW5e reference data: species traits, class features, equipment, Force powers, conditions, stat blocks |

### Character Sheet Schema

```json
{
  "userId": "keycloak-user-id",
  "sessionId": "guid",
  "name": "Kael Voss",
  "species": {
    "name": "Twi'lek",
    "source": "sw5e",
    "abilityScoreIncrease": { "Charisma": 2, "Dexterity": 1 },
    "traits": ["Darkvision", "Colorful", "Nimble Escape"]
  },
  "class": {
    "name": "Scoundrel",
    "level": 3,
    "hitDie": "d8",
    "subclass": "Sharpshooter",
    "features": ["Sneak Attack", "Bad Feeling", "Cunning Action"]
  },
  "abilityScores": {
    "Strength": 8,
    "Dexterity": 16,
    "Constitution": 12,
    "Intelligence": 14,
    "Wisdom": 10,
    "Charisma": 15
  },
  "proficiencyBonus": 2,
  "hitPoints": { "current": 21, "max": 21, "temporary": 0 },
  "armorClass": 14,
  "speed": 30,
  "skills": {
    "proficient": ["Stealth", "Deception", "Sleight of Hand", "Persuasion"],
    "expertise": ["Stealth"]
  },
  "savingThrows": ["Dexterity", "Intelligence"],
  "equipment": [
    { "name": "Blaster Pistol", "type": "weapon", "damage": "1d6 energy", "properties": ["Light", "Range (40/160)"] },
    { "name": "Leather Armor", "type": "armor", "ac": 11, "plusDex": true }
  ],
  "credits": 250,
  "forcePowers": [],
  "techPowers": ["Electroshock", "Repair Droid"],
  "background": {
    "name": "Smuggler",
    "feature": "Underworld Connections",
    "personalityTraits": ["I always have an escape plan"],
    "ideal": "Freedom — everyone deserves to choose their own path",
    "bond": "I owe a debt to a Hutt crime lord",
    "flaw": "I can't resist a big score, even when the odds are terrible"
  },
  "xp": 900,
  "alignment": "Chaotic Good",
  "conditions": [],
  "deathSaves": { "successes": 0, "failures": 0 }
}
```

### Campaign State Schema

```json
{
  "sessionId": "guid",
  "userId": "keycloak-user-id",
  "campaignName": "Shadows of the Outer Rim",
  "era": "Galactic Civil War",
  "timelineYear": -1,
  "currentLocation": {
    "planet": "Nar Shaddaa",
    "region": "Hutt Space",
    "gridSquare": "S-12",
    "specificLocation": "Red Sector, Lower Promenade"
  },
  "questLog": [
    {
      "title": "The Hutt's Bounty",
      "status": "active",
      "description": "Gorba the Hutt wants a datapad retrieved from a crashed freighter on Ryloth.",
      "objectives": [
        { "text": "Travel to Ryloth", "complete": false },
        { "text": "Locate the crash site", "complete": false },
        { "text": "Retrieve the datapad", "complete": false }
      ]
    }
  ],
  "knownNPCs": [
    {
      "name": "Gorba Desilijic Aarrpo",
      "pageId": 12345,
      "relationship": "Quest Giver / Creditor",
      "disposition": "Neutral — expects results"
    }
  ],
  "storyFlags": {
    "met_gorba": true,
    "has_ship": false,
    "imperial_attention": 0
  },
  "partyFunds": 250,
  "sessionCount": 1,
  "createdAt": "2026-04-03T00:00:00Z",
  "lastPlayedAt": "2026-04-03T00:00:00Z"
}
```

## GM Agent Architecture

### System Prompt Layers

```
Layer 1: GM Persona
  "You are a Star Wars 5e Game Master. You narrate scenes, 
   control NPCs, adjudicate rules, and create memorable 
   adventures. You are fair but dramatic."

Layer 2: SW5e Rules Reference
  Core mechanics: ability checks, saving throws, attack rolls,
  advantage/disadvantage, proficiency, conditions, death saves,
  rest mechanics, Force/Tech point management.

Layer 3: Campaign Context (injected per session)
  Current character sheet, campaign state, recent log entries,
  active combat state (if any).

Layer 4: World Knowledge (via existing tools)
  KG queries, semantic search, galaxy map — the GM's 
  encyclopedic knowledge of the Star Wars universe.
```

### GM Toolkit (GameMasterToolkit)

**Dice & Mechanics:**
| Tool | Purpose |
|------|---------|
| `roll_dice(notation)` | Roll any dice expression (e.g., "2d6+3", "1d20", "4d6 drop lowest") |
| `ability_check(ability, dc, skill?, advantage?)` | D20 + modifier vs DC, applying proficiency/expertise |
| `saving_throw(ability, dc, advantage?)` | Saving throw with proficiency if applicable |
| `attack_roll(weapon, target_ac, advantage?)` | Attack roll → hit/miss → damage roll if hit |
| `apply_damage(amount, type)` | Reduce HP, check for unconscious/death saves |
| `apply_healing(amount)` | Restore HP up to max |
| `short_rest()` | Spend hit dice, recover resources |
| `long_rest()` | Full HP recovery, reset powers/features |

**Combat:**
| Tool | Purpose |
|------|---------|
| `start_combat(combatants)` | Roll initiative, create turn order |
| `next_turn()` | Advance to next combatant, apply start-of-turn effects |
| `end_combat(xp_award)` | Clean up combat state, award XP |
| `add_condition(target, condition, duration?)` | Apply a condition (prone, stunned, etc.) |
| `remove_condition(target, condition)` | Remove a condition |

**Character Management:**
| Tool | Purpose |
|------|---------|
| `update_inventory(action, item)` | Add/remove/equip items |
| `update_credits(amount)` | Add or deduct credits |
| `award_xp(amount)` | Grant XP, check for level up |
| `level_up(choices)` | Apply level-up: HP, new features, ability score improvement |
| `cast_power(power_name, level?)` | Use a Force/Tech power, deduct points |

**World & Lore (existing tools, re-contextualized):**
| Tool | Purpose |
|------|---------|
| `search_entities(query)` | Find NPCs, locations, factions from the KG |
| `semantic_search(query)` | Deep lore lookup for scene descriptions, NPC motivations |
| `get_entity_relationships(id)` | Who is connected to whom — faction alliances, rivalries |
| `find_entities_by_year(year, type)` | What exists in the current timeline year |
| `get_galaxy_year(year)` | Territory control, active conflicts, era context |

**Rules Reference:**
| Tool | Purpose |
|------|---------|
| `lookup_rule(topic)` | Query SW5e rules (conditions, actions, spellcasting, etc.) |
| `get_stat_block(creature)` | Get NPC/creature combat stats |
| `lookup_equipment(name)` | Equipment properties, damage, cost |
| `lookup_power(name)` | Force/Tech power details, range, duration, effects |

### Example Interaction

```
PLAYER: "I walk into the cantina and look for someone 
         who might know about smuggling work."

GM THINKS:
  1. get_galaxy_year(-1) → Galactic Civil War context,
     Empire tightening grip on Hutt Space
  2. semantic_search("Nar Shaddaa cantina smugglers underworld") 
     → lore about the moon's criminal networks
  3. search_entities("Nar Shaddaa") → find notable NPCs at this location
  4. ability_check("Wisdom", 12, skill="Perception")
     → Player rolls 14 + 0 (Wis) = 14, success

GM NARRATES:
  "The Red Sector cantina is thick with spice smoke and the 
   chatter of a dozen species. A Devaronian bartender polishes 
   glasses with three hands — the fourth rests on a blaster 
   under the counter. 

   [Perception check: 14 vs DC 12 — Success]

   Your eyes catch a Rodian in the back booth, nursing a drink 
   and watching the door. She's wearing a flight jacket with a 
   Black Sun patch, partially covered. She notices you looking 
   and tilts her head — an invitation, or a warning.

   What do you do?"
```

## SW5e Data Strategy

### Option A: SW5e API Integration

SW5e has a community API with structured data for species, classes, equipment, powers, monsters, and more. Import into `game.rules` collection.

**Pros:** Comprehensive, structured, stays up-to-date  
**Cons:** External dependency, need to handle API changes

### Option B: Embedded Rule Summaries

Encode core rules directly in the GM's system prompt and tool descriptions. Species traits, class features, and equipment as structured data in the codebase.

**Pros:** Self-contained, no external dependency, fast  
**Cons:** Manual maintenance, can't cover everything

### Option C: Hybrid (Recommended)

- **Core mechanics** (ability checks, combat, conditions, rest) → embedded in system prompt
- **Species & classes** → imported from SW5e into `game.rules`, queried by tools
- **Equipment & powers** → imported from SW5e, queried by `lookup_equipment`/`lookup_power`
- **Creatures/NPCs** → SW5e bestiary for stat blocks + KG for lore/personality
- **World knowledge** → existing KG + semantic search (already built)

## UI Design

### Campaign Page (`/rpg` — feature-flagged)

```
┌─────────────────────────────────────────────────┐
│  [Character Sheet]  [Quest Log]  [Galaxy Map]   │
├─────────────────────┬───────────────────────────┤
│                     │  CHARACTER PANEL           │
│  NARRATIVE          │  ┌─────────────────────┐  │
│  (chat-style with   │  │ Kael Voss           │  │
│   dice roll cards,  │  │ Twi'lek Scoundrel 3 │  │
│   NPC portraits,    │  │ HP: 21/21  AC: 14   │  │
│   scene transitions)│  │ Credits: 250        │  │
│                     │  └─────────────────────┘  │
│                     │                           │
│                     │  ABILITIES                │
│                     │  STR  8 (-1)              │
│                     │  DEX 16 (+3)              │
│                     │  CON 12 (+1)              │
│                     │  INT 14 (+2)              │
│                     │  WIS 10 (+0)              │
│                     │  CHA 15 (+2)              │
│                     │                           │
│                     │  INVENTORY                │
│                     │  • Blaster Pistol         │
│                     │  • Leather Armor          │
│                     │                           │
│  ┌───────────────┐  │  COMBAT (when active)     │
│  │ What do you   │  │  Initiative: 17           │
│  │ do?           │  │  Round 2, Your Turn       │
│  └───────────────┘  │  Enemies: Stormtrooper x3 │
└─────────────────────┴───────────────────────────┘
```

### Dice Roll Rendering

Dice rolls are displayed inline in the narrative as styled cards:

```
┌──────────────────────────────┐
│ 🎲 Persuasion Check         │
│ d20 (14) + CHA (+2) + Prof (+2) = 18  │
│ DC 15 — ✅ Success           │
└──────────────────────────────┘
```

### Combat Tracker

When combat is active, a persistent panel shows:

```
┌──────────────────────────────┐
│ ⚔️ COMBAT — Round 2          │
│                              │
│ ► Kael Voss (HP 21/21)      │
│   Stormtrooper 1 (HP 12/16) │
│   Stormtrooper 2 (HP 16/16) │
│   Stormtrooper 3 (HP 0/16) ☠│
│                              │
│ Your Turn — Action, Bonus    │
│ Action, Movement available   │
└──────────────────────────────┘
```

## Character Creation Flow

### Step 1: Choose Era & Location

Player selects from the existing galaxy map / timeline:
- Era: Old Republic, Clone Wars, Galactic Civil War, New Republic, etc.
- Starting planet: picked from the galaxy map or suggested by GM
- The GM uses `find_entities_by_year` to populate the world with era-appropriate factions and conflicts

### Step 2: Choose Species

Present SW5e species list, enriched with Wookieepedia lore:
- Mechanical traits from SW5e (ability bonuses, size, speed, traits)
- Lore flavor from KG (homeworld, culture, notable members)
- Not every Wookieepedia species has SW5e stats — GM can homebrew for exotic species using similar templates

### Step 3: Choose Class

SW5e classes: Berserker, Consular, Engineer, Fighter, Guardian, Monk, Operative, Scholar, Scout, Sentinel
- Each has subclasses chosen at level 3
- Class determines hit dice, proficiencies, starting equipment, and progression

### Step 4: Ability Scores

Standard array, point buy, or roll (4d6 drop lowest x6):
- Apply species bonuses
- Derive modifiers, saves, skills

### Step 5: Background & Details

- Background (Smuggler, Soldier, Entertainer, Criminal, etc.) → skill proficiencies + feature
- Personality traits, ideal, bond, flaw
- Name, appearance, backstory (GM can help generate from lore)

### Step 6: Equipment & Powers

- Starting equipment from class + background
- Force-sensitive classes: choose starting Force powers
- Tech classes: choose starting Tech powers

## Implementation Phases

### Phase 1: Foundation
- [ ] `game.*` MongoDB collections and models
- [ ] `GameMasterToolkit` with dice, ability checks, character CRUD
- [ ] GM system prompt with core SW5e rules
- [ ] Basic character creation flow (hardcoded species/class data)
- [ ] Chat-style UI at `/rpg` (feature-flagged)
- [ ] Session persistence (character + campaign state)

### Phase 2: Combat
- [ ] Initiative and turn tracking
- [ ] Attack rolls, damage, conditions
- [ ] NPC stat blocks (basic set from SW5e bestiary)
- [ ] Combat tracker UI panel
- [ ] Death saves and unconscious rules

### Phase 3: World Integration
- [ ] Wire GM to existing KG tools for NPC/location lookup
- [ ] Era-aware world state (factions, territory, conflicts)
- [ ] Galaxy map integration (travel between systems)
- [ ] Encounter generation based on location + era

### Phase 4: SW5e Data
- [ ] Import SW5e species, classes, equipment, powers from API
- [ ] `lookup_rule`, `lookup_equipment`, `lookup_power` tools
- [ ] Level-up flow with class feature selection
- [ ] Force/Tech power management

### Phase 5: Polish
- [ ] Dice roll cards with animations
- [ ] NPC portrait generation or Wookieepedia image integration
- [ ] Session summary / "last time on..." recap
- [ ] Multiple save slots per user
- [ ] Shared campaigns (multiplayer — future)

## Open Questions

1. **Multiplayer?** — Single-player first. Multiplayer adds turn coordination, shared state, and SignalR complexity. Design for it but don't build it yet.

2. **Dice trust** — Should dice be rolled server-side (trusted) or client-side (fun to watch)? Server-side with a rendered replay feels right.

3. **Rule enforcement vs flexibility** — How strict should the GM be? A real GM bends rules for fun. The AI should follow rules by default but have a "Rule of Cool" flag the player can invoke.

4. **Session length** — LLM context windows fill up. Need a summarization strategy: compress older narrative into a summary, keep recent turns in full.

5. **Cost** — Each player action requires 1-3 tool calls + narrative generation. At ~$0.002/response for gpt-5-mini, a 50-turn session costs ~$0.10-0.30. Manageable, but worth tracking.

6. **SW5e licensing** — SW5e content is CC-BY-4.0. Free to use with attribution. Must credit SW5e in the game UI.

## References

- [SW5e](https://sw5e.com) — Star Wars 5th Edition (CC-BY-4.0)
- [SW5e API](https://sw5e.com/api) — Structured game data
- [D&D 5e SRD](https://www.dndbeyond.com/sources/srd) — Base rules SW5e builds on
- Existing project: `SemanticSearchService`, `KnowledgeGraphQueryService`, `GraphRAGToolkit`
