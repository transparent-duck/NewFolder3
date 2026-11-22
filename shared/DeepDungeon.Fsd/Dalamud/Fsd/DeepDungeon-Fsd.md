# Deep Dungeon & Necromancer Reference

> This document contains only Deep Dungeon game mechanics and observed game behavior.
>
> Confidence markers:
> - **(confirmed)** = verified in game or authoritative game data
> - **(uncertain)** = partially known, needs player confirmation
> - **(TODO)** = placeholder, not yet filled in

---

## 1. Overview

Deep Dungeon is a roguelike instanced content type in FFXIV with procedurally generated maps.
Players use a separate leveling system and special items (pomanders) inside.

"Necromancer" is the in-game title earned by clearing Palace of the Dead floors 1–200 solo.

There are 4 deep dungeons: **(confirmed)**

| Abbrev | Full Name          | Total Floors |
|--------|--------------------|--------------|
| PotD   | Palace of the Dead | 200          |
| HoH    | Heaven-on-High     | 100          |
| EO     | Eureka Orthos      | 100          |
| PT     | Pilgrim's Traverse | 100          |

Note: PotD and PT are different dungeons despite both starting with "P".

---

## 2. Floor Structure

### 2.1 Runs & Floorsets **(confirmed)**

Deep dungeon runs operate in **10-floor sets** (floorsets). A single entry covers floors N to N+9, beginning at floors 1, 11, 21, and so on.

### 2.2 Floor Types **(confirmed)**

There are 3 floor types:

1. **Mob floor** — standard floor with enemies, traps, chests, and a passage to the next floor.
2. **Boss floor** — single room with a boss fight. No passage; has an exit portal after clearing.
3. **Resulting floor** — final floor with no enemies or boss. Provides rewards and an exit portal.

### 2.3 Per-Dungeon Floor Rules **(confirmed)**

| Dungeon | Mob Floors     | Boss Floors                  | Resulting Floor |
|---------|----------------|------------------------------|-----------------|
| PotD    | 1–9, 11–19, ... 181–189, 191–199 | Every 10th (10,20,...190) | 200 (no boss) |
| HoH     | 1–9, 11–19, ... 81–89, 91–99     | Every 10th (10,20,...90)  | 100 (no boss) |
| EO      | 1–9, 11–19, ... 81–89, 91–98     | Every 10th (10,...90) + **99** | 100 (no boss) |
| PT      | 1–9, 11–19, ... 81–89, 91–98     | Every 10th (10,...90) + **99** | 100 (no boss) |

Key difference: EO and PT have floor 99 as an **extra boss floor**, while PotD and HoH do not.

---

## 3. Map Generation

### 3.1 Boss & Resulting Floors **(confirmed)**

Single room. No procedural generation. Player spawns directly in the room.

### 3.2 Mob Floor Layouts **(confirmed)**

Two layout types exist:

| Layout | Grid   | Total Slots | Valid Rooms | Corner Rooms |
|--------|--------|-------------|-------------|--------------|
| 5×5    | 5 cols × 5 rows | 25 | 21 | Four corners are always empty |
| 4×3    | 4 cols × 3 rows | 12 | 12 | None |

### 3.3 Room Connections **(confirmed)**

Rooms connect in the four cardinal directions. One room is the Home/spawn room, and another contains the passage.

### 3.4 Tileset Variants **(confirmed)**

Mob floors use multiple tileset/layout variants. Room shapes and their fixed candidate trap/hoard positions depend on the active variant.

---

## 4. Passage (Floor Exit)

### 4.1 What Is a Passage **(confirmed)**

The passage is the exit point on mob floors that teleports the player to the next floor.
It is **not an interactable object**. The player must stand inside the passage area and wait to be transported.

The wait time is approximately 3 seconds.

### 4.2 Passage Activation **(confirmed)**

The passage starts **inactive** when the player enters a mob floor.
Each enemy kill provides a **random amount** of passage progress. When enough progress accumulates,
the passage opens.

The number of kills required varies per floor because passage progress per kill is random.

Some magicite/incense effects can clear enemies or directly activate the passage; see §7.3.

### 4.3 Passage Entry Requirement **(confirmed)**

Even after the passage activates (opens), the player **cannot enter it while in combat**.
The player must be fully **disengaged** — meaning no enemies are currently aggroed or chasing the player.
If the player has attracted any mobs, they must kill all of them first before the passage will transport them.

---

## 5. Chests & Treasure

### 5.1 Chest Types **(confirmed)**

| Type    | Contents | Mimic? |
|---------|----------|--------|
| Gold    | Pomanders | Yes |
| Silver  | Weapon/armor upgrades + general items + special DD items | Yes |
| Bronze  | General items only | Yes |
| Banded  | Hoard items; hidden until revealed | No |

**All gold, silver, and bronze chests can turn into mimics** (hostile enemy instead of loot).

### 5.2 Chest Contents Detail **(confirmed)**

- **Gold**: Contains pomanders (deep dungeon–only consumable items).
- **Silver**: Contains weapon/armor upgrades (aetherpool) and general items.
  General items from silver chests **can be taken out** of the deep dungeon (unlike pomanders).
  In HoH, EO, and PT, silver chests can also contain **magicite** (called differently per dungeon — see §7.3).
- **Bronze**: Contains general items only.

### 5.3 Silver Chest Explosion **(confirmed)**

Silver chests can explode instead of giving loot. The explosion deals a **fixed 85% of the player's max HP** as damage.

### 5.4 Banded Chests (Hoards) **(confirmed)**

- Banded chests are **hidden**. To reveal one, the player stands near a hoard marker position
  for approximately **3 seconds**. No pomander is required to reveal a hoard this way.
- Trap and hoard positions have a deterministic room-local pairing. In the observed room-position ordering, a trap at location N identifies the paired hoard at location N+1. They are **not necessarily the same world coordinate**.
- Intuition reveals the hidden hoard position; it does not spawn the banded coffer immediately. The player must still stand at the hoard trigger position. Affluence affects ordinary chests on the next floor, not accursed hoards.

**Hoard generation rules per floorset (confirmed):**
- Each floor has **0 or 1** hoard. Never more than 1 per floor.
- Maximum **5 hoards per floorset** (10 floors).
- Floors 1–3, 4–6, and 7–9 of each floorset are **guaranteed at least 1 hoard** among them.
  (Floor 10 is always a boss floor, so no hoard there.)

### 5.5 Chest Interaction **(confirmed)**

- Max interact distance: 1.5m
- Cooldown between chest interactions: 3 seconds

---

## 6. Traps

### 6.1 Trap Positions **(confirmed)**

Trap positions are selected from fixed candidate locations belonging to the room/tile variant. Trap and hoard positions follow the room-local pairing described in §5.4; a trap coordinate is evidence for its paired hoard coordinate, not proof that the hoard occupies the same coordinate.

### 6.2 Trap Mechanics **(confirmed)**

Not all rooms have traps. Each room has **0 or 1** trap only.
The **Home room** (spawn room) never contains traps or hoards.
Candidate trap positions are **fixed per room/tile variant**.

Trap activation radius is approximately **1.7**.

Multiple trap types exist with different debuff effects.

Certain pomanders/magicites can **reveal all traps** on the map or **remove all traps** from the floor.
See §7.3 for details.

### 6.3 Revealing Hoards **(confirmed)**

To reveal a banded chest, the player stands near a hoard marker position for approximately **3 seconds**.

Hoard activation radius is approximately **1.7**.

The banded chest then spawns at the hoard position. **No pomander is required** for this. Intuition reveals the position but does not replace the standing trigger; Affluence does not affect accursed hoards.

---

## 7. Pomanders (Items)

### 7.1 Recovery Potions **(confirmed)**

Each dungeon has a recovery potion. These are **general items**, not pomanders — they exist outside
the pomander slot system and do not occupy pomander inventory.

- PotD: Sustaining Potion
- HoH: Empyrean Potion
- EO: Orthodox Recovery Potion
- PT: Pilgrim's Potion

### 7.2 Pomander List **(confirmed)**

Each dungeon has **16 pomander slots** (index 0–15). Slots 0–10, 13, 14 are shared across all dungeons
(same effect, different naming: PotD/HoH/PT use 魔陶器, EO uses 魔科学器). Slots 11, 12, 15 are unique.

#### Shared Pomanders (all dungeons, slot 0–10, 13, 14)

| Slot | EN Name    | CN Name      | Effect |
|------|-----------|--------------|--------|
| 0    | Safety    | 咒印解除     | Remove all traps on current floor |
| 1    | Sight     | 全景         | Reveal full map + show all trap positions on current floor (effect is current floor only) |
| 2    | Strength  | 强化自身     | +30% damage and healing |
| 3    | Steel     | 强化防御     | -40% damage taken |
| 4    | Affluence | 宝箱增加     | More chests on **next** floor |
| 5    | Flight    | 减少敌人     | Fewer enemies on **next** floor |
| 6    | Alteration| 改变敌人     | Transform enemies in a random room **next** floor to mimics/mandragoras |
| 7    | Purity    | 解咒         | Remove curse status |
| 8    | Fortune   | 运气上升     | Higher chest drop rate when defeating enemies this floor |
| 9    | Witching  | 形态变化     | Transform nearby targets to frog/kappa/chicken |
| 10   | Serenity  | 魔法效果解除 | Remove all floor enchantments (see §8) |
| 13   | Intuition | 感知宝藏     | Show the current floor's hoard position when one exists. The Intuition icon/effect persists across floors until the game deactivates it; a no-hoard result for one floor does not end the effect. |
| 14   | Raising   | 重生         | Revive first KO'd party member |

#### Unique Pomanders by Dungeon (slots 11, 12, 15)

**PotD:**

| EN Name    | CN Name  | Effect |
|------------|----------|--------|
| Rage       | 曼提克化 | Transform self to Manticore |
| Lust       | 梦魔化   | Transform self to Incubus |
| Resolution | 基路伯化 | Transform self to Cherub |

**HoH:**

| EN Name        | CN Name  | Effect |
|----------------|----------|--------|
| Frailty        | 弱化敌人 | Weaken all enemies on floor (lower ATK/DEF) |
| Concealment    | 隐形     | Party invisibility |
| Petrification | 石化敌人 | Petrify all enemies on floor |

**EO:**

| EN Name | CN Name    | Effect |
|---------|------------|--------|
| Lethargy | 缓速       | Slow all enemies on floor |
| Storms   | 大漩涡     | Reduce all enemy HP to single digits |
| Dread    | 恐慌装甲化 | Transform self to Dreadnought |

**PT:**

| EN Name      | CN Name    | Effect |
|--------------|------------|--------|
| Haste        | 加速       | Party haste buff |
| Purification | 净化护符   | Cleanse debuffs + one-time debuff shield |
| Devotion     | 朝圣的指引 | Guaranteed light candle next floor |

### 7.3 Magicite **(confirmed)**

Magicite is a separate item category from pomanders, obtained from **silver chests** in HoH, EO, and PT.
PotD does not have magicite. All magicite summon a powerful entity to fight for the player.

Each dungeon calls them differently:

| Dungeon | Type Name |
|---------|-----------|
| HoH     | 魔石 (Magic Stone) |
| EO      | 複製體 (Demiclone) |
| PT      | 杜松香 (Incense) |

#### HoH — 魔石 (summon primals)

| CN Name        | Effect |
|----------------|--------|
| 伊弗利特的魔石 | Kill all enemies on current floor (not boss). Often opens passage. |
| 泰坦的魔石     | Kill all enemies on current floor (not boss). Often opens passage. |
| 迦楼罗的魔石   | Kill all enemies on current floor (not boss). Often opens passage. |
| 奥丁的魔石     | Kill all enemies on current floor, **including boss**. |

#### EO — 複製體 (summon demiclones)

Demiclones summon a helper entity that fights alongside the player.

| CN Name            | Effect |
|--------------------|--------|
| 乌内亚灵复制体     | Summon Una demiclone (combat helper) |
| 多加亚灵复制体     | Summon Doga demiclone (combat helper) |
| 洋葱剑士亚灵复制体 | Summon Onion Knight demiclone (combat helper) |

#### PT — 杜松香 (summon wardens)

| CN Name      | EN Name     | Effect |
|--------------|-------------|--------|
| 杜松香·宿命 | Poisonfruit | Kill all enemies on the floor except a boss; often opens the passage |
| 杜松香·敏慧 | Mazeroot    | Composite: Sight + Serenity + passage activation + revive-stele activation |
| 杜松香·安宁 | Barkbalm    | +damage, +max HP, and continuous healing for the current floor |

---

## 8. Floor Enchantments **(confirmed)**

Floors can have random enchantments (魔法效果) that apply buffs or debuffs to the entire floor.
Pomander of Serenity (slot 10) removes all enchantments.

| CN Name | Effect | Type |
|---------|--------|------|
| 失明. | Reduced accuracy | Debuff |
| 最大体力减少 | Reduced max HP | Debuff |
| 伤害降低 | Reduced damage dealt | Debuff |
| 能力封印 | Cannot use ability-type skills | Debuff |
| 加速 | Shorter auto-attack, cast, and recast times | Buff |
| 最大体力魔力提升 | Increased max HP/MP | Buff |
| 禁止使用道具 | Cannot use items or certain special items | Debuff |
| 禁止冲刺 | Cannot sprint | Debuff |
| 禁止击退 | Knockback/pull effects disabled (both sides) | Neutral |
| 禁止体力自然恢复 | No natural HP regen | Debuff |
| 妖雾弥漫 | All enemies: faster, +damage, -damage taken | Debuff |
| 冲刺 | Increased movement speed | Buff |
| 禁止使用魔石 | Cannot use 魔石 (HoH only) | Debuff |
| 禁止使用亚灵复制体 | Cannot use 複製體 (EO only) | Debuff |
| 禁止使用杜松香 | Cannot use 杜松香 (PT only) | Debuff |

## 9. Accursed Hoard and Intuition

### 9.1 Current-Floor Result **(confirmed)**

Using Intuition on the current floor produces a result after a short asynchronous delay:

- A hoard exists on this floor.
- No hoard exists on this floor.

These results are floor-local. The no-hoard message occurs in response to using Intuition; an inherited Intuition effect entering a new floor with no hoard does not produce another no-hoard message. A no-hoard result does not end an Intuition effect that remains active across floors.

### 9.2 Cross-Floor Persistence **(confirmed)**

- If Intuition remains active when the player changes floors, it applies to the new floor.
- If the inherited floor has a hoard, its result/indicator appears after the floor loads.
- If the inherited floor has no hoard, no new no-hoard message is emitted.
- The effect ends when the game deactivates the Intuition icon/state, normally after the relevant hoard is found.
- Revealing/finding a hoard and opening/acquiring its coffer are distinct events.

### 9.3 Indicator and Coffer **(confirmed)**

- When a hoard exists, Intuition shows its hidden trigger position.
- The indicator may not appear until the player approaches its area.
- Standing at the indicated trigger position for approximately 3 seconds reveals the banded coffer.
- The hoard-found message means the coffer was revealed; it does not prove the coffer was opened or its reward acquired.
- Using Intuition after the floor's hoard has already been found produces the no-hoard result because no unfound hoard remains on that floor.
