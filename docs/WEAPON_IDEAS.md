# Weapon Ideas

Creative new weapon concepts for Superfighters Redux, organized by category. These focus on filling gaps in the current roster — particularly **crowd control**, **mobility tools**, **trick shots**, **area denial**, and **physics-based chaos**.

Items marked ~~strikethrough~~ have already been implemented.

---

## Melee

### Nunchaku
Fast combo weapon with very short range. Each consecutive hit within a ~600ms window adds +2 damage and +5% attack speed. Resets to base on miss. Base damage is low (~4), but a 5-hit combo hits for ~12 per swing. Rewards aggressive play and accuracy.

### Spear
Longest reach melee in the game, but attacks are exclusively forward thrust (no sweep). Can be thrown (alt-fire) for a one-time ranged hit, but then it's gone. The melee/thrown hybrid.

### War Hammer (one-handed)
Slower than switchblade, faster than sledgehammer. On hit: applies a short knockback burst (sends enemy flying horizontally). The "get off me" melee — a spacing tool rather than a damage tool.

### Boomerang Axe
Melee that can be thrown (alt-fire). Returns to the thrower after a short arc; deals damage on the way out *and* back. Lost if not caught.

---

## Handguns

### Derringer
Pocket 2-shot pistol. Tiny, fast draw, huge per-bullet damage but only 2 rounds total. The "panic button" sidearm — you switch to it when your rifle is empty and you need two good shots *right now*.

### Ricochet Pistol
Fires a single bullet that bounces off walls/floors/ceilings up to 3 times. Low direct damage but excels in tight corridors and around cover. Skill-ceiling weapon — bank shots around platforms. Existing bouncing ammo PowerUp infrastructure can be leveraged.

### Taser Gun
Short range (flare gun distance), fires a single probe. On hit: brief electrocute stun (shorter than ShowStopper), low damage. 2-3 shots total. Utility sidearm — creates a follow-up window for melee or a throw.

---

## Rifles

### ~~Tesla Rifle~~
Continuous electric beam (like a short-range Lazer). Arcs to a second nearby enemy for reduced damage. Drains ammo fast. **Implemented.**

### ~~Rivet Gun~~
Semi-auto, medium damage, but projectiles embed in surfaces. Hitting a surface near an enemy causes a small shrapnel burst after a delay. **Implemented as JunkCannon.**

### Harpoon Gun
Fires a bolt that pins hit players to the nearest wall/floor behind them for ~1.5 seconds. They can mash to break free faster. One shot, slow reload, 3 total ammo. The ultimate positional punishment weapon — being stuck to a wall while someone walks up to you is terrifying.

### Ice Cannon
Fires a frost projectile that applies progressive slow (stacking). Two hits = full freeze for ~1 second (player becomes a frozen physics object that can be kicked off ledges). Low damage, pure CC. Medium fire rate, ~8 rounds. The anti-rusher.

### Lever-Action Shotgun
Fills the gap between the slow blunderbuss/double barrel and the insane AA12. 4 rounds, tighter spread than other shotguns, faster pump than double barrel. The "practical" shotgun.

---

## Thrown

### Bear Trap
Place on the ground. First player who walks over it gets clamped — rooted in place for ~2 seconds, takes 10 damage. Visible but small; rewards map awareness. 2 total. Different from Claymore because it doesn't kill — it *holds* people for your team to capitalize.

### Bola
Thrown projectile that wraps around a hit player's legs. They can't jump or run (walk speed only) for ~2 seconds. No damage. Pure crowd control. The anti-parkour weapon — catches runners.

### Gravity Bomb
After a 1.5s delay, creates a pull vortex for 2 seconds that drags all players/physics objects toward the center. Doesn't damage directly, but pulling 3 people into a cluster and following up with a frag grenade or QuadLauncher is devastating. The combo enabler.

### Thermite
Sticks to whatever it hits (wall, floor, player). After 1 second, burns through in a small radius, **destroying terrain/objects** it's attached to and dealing fire damage. 2 total. Unique because it interacts with destructible map elements rather than just players. The "make your own door" throwable.

---

## Pickups / Utility

### Barricade Kit
Instant pickup. Places a small destructible wall (~2 tiles wide, 3 tall) at your position. Has HP and can be destroyed. Blocking a corridor or creating cover in 2D is a legit game-changer. One use.

### Berserk Serum
Powerup. For 6 seconds: +50% melee damage, +25% move speed, but you *cannot* use ranged weapons (forced melee). Taking damage during berserk doesn't cause flinch. The all-in aggression pickup.

### Magnetic Field
Powerup. For 8 seconds, projectiles that pass near you curve away (deflection aura). Doesn't block — deflects. Direct shots can still hit, but spray weapons and explosives veer off. Counters minigun and shotguns, useless against sniper/melee.

### Cloak Device
Instant pickup. Invisible for 5 seconds. Taking damage or attacking breaks it immediately. The flank tool. Not as OP as it sounds in 2D — movement dust and physics interactions can still give you away.

---

## Priority Picks

If picking the highest-impact additions first:

1. **Ricochet Pistol** — Fills the thin handgun roster + unique 2D mechanic, can leverage existing bouncing ammo code
2. **Bear Trap** — New control archetype for thrown, simple to implement (Claymore-like but roots instead of explodes)
3. **Harpoon Gun** — Pin mechanic is unique and terrifying in a platformer
4. **Gravity Bomb** — Combo enabler, uses the physics engine, unique and chaotic
5. **Nunchaku** — Combo melee fills a gap and adds a skill-expression weapon
6. **Bola** — Simple thrown CC that punishes runners, easy to implement
