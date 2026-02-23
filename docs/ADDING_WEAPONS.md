# Adding New Weapons to SFR (Superfighters Redux)

This guide covers **everything** required to add a new weapon — from creating textures and tile definitions to writing C# code and registering it in all the right places. Follow each section in order.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Weapon Types & Base Classes](#2-weapon-types--base-classes)
3. [Step-by-Step: Adding a Ranged Weapon (Handgun / Rifle)](#3-step-by-step-adding-a-ranged-weapon)
4. [Step-by-Step: Adding a Melee Weapon](#4-step-by-step-adding-a-melee-weapon)
5. [Step-by-Step: Adding a Thrown Weapon](#5-step-by-step-adding-a-thrown-weapon)
6. [Step-by-Step: Adding a Pickup / Powerup Item](#6-step-by-step-adding-a-pickup--powerup-item)
7. [Adding a Custom Projectile](#7-adding-a-custom-projectile)
8. [Adding a Thrown Object (Grenade-Like)](#8-adding-a-thrown-object-grenade-like)
9. [File Reference & What Each File Does](#9-file-reference--what-each-file-does)
10. [Optional Interfaces](#10-optional-interfaces)
11. [Property Reference Tables](#11-property-reference-tables)
12. [Animation Reference](#12-animation-reference)
13. [Texture Naming Conventions](#13-texture-naming-conventions)
14. [Sound System](#14-sound-system)
15. [Common Pitfalls](#15-common-pitfalls)
16. [Full Checklist](#16-full-checklist)

---

## 1. Architecture Overview

SFR is a Harmony-based mod for Superfighters Deluxe (SFD). It patches the base game's weapon, projectile, tile, and animation systems using `[HarmonyPatch]` attributes. Weapons are **not** loaded through configuration files — they are defined entirely in C# classes, then registered into the base game's arrays via Harmony prefix/postfix hooks.

### Key Systems

| System | Purpose |
|--------|---------|
| **`SFR/Weapons/Database.cs`** | Registers all new weapon instances into `WeaponDatabase.m_weapons[]` and defines spawn chances |
| **`SFR/Projectiles/Database.cs`** | Registers all new projectiles into `ProjectileDatabase.projectiles[]` |
| **`SFR/Objects/ObjectsHandler.cs`** | Maps tile/object IDs (strings) to C# object instances for spawning (weapon pickups, thrown objects, etc.) |
| **`SFR/Bootstrap/Assets.cs`** | Loads textures, sounds, tiles, items, animations, and maps from `SFR/Content/` |
| **`Content/Data/Weapons/weapons.sfdx`** | Defines the physical tile (collision shape, texture, pickup behavior) for each weapon as a world object |
| **`Content/Data/Images/Weapons/`** | PNG texture files for all weapon sprites |
| **`Content/Data/Sounds/`** | Sound files (`.wav`) and sound definitions (`.sfds`) |

### How a Weapon Gets Loaded (Flow)

```
Game starts
  → Assets.cs patches load SFR textures, sounds, tiles, animations
  → WeaponDatabase.Load() is called
    → Database.cs (Harmony Prefix) resizes the weapon array and inserts all SFR weapons
  → ProjectileDatabase.Load() is called
    → Projectiles/Database.cs (Harmony Postfix) resizes the projectile array and inserts all SFR projectiles
  → When a weapon item spawns in the world
    → ObjectsHandler.cs maps the ModelID string to an ObjectWeaponItem
```

---

## 2. Weapon Types & Base Classes

Each weapon type inherits from a different SFD base class:

| Weapon Type | Base Class | `WeaponItemType` Enum | Category | Example |
|-------------|-----------|----------------------|----------|---------|
| Ranged (handgun) | `RWeapon` | `WeaponItemType.Handgun` | `WeaponCategory.Secondary` | Flintlock, Anaconda |
| Ranged (rifle) | `RWeapon` | `WeaponItemType.Rifle` | `WeaponCategory.Primary` | Barrett, AK47, Crossbow |
| Melee | `MWeapon` | `WeaponItemType.Melee` | `WeaponCategory.Melee` | Blade, Greatsword, Crowbar |
| Thrown | `TWeapon` | `WeaponItemType.Thrown` | `WeaponCategory.Supply` | Claymore, Frag Grenade |
| Powerup | `PItem` | `WeaponItemType.Powerup` | `WeaponCategory.Supply` | Health Pouch, Adrenaline |
| Instant Pickup | `HItem` | `WeaponItemType.InstantPickup` | `WeaponCategory.Supply` | Jetpack, Gunpack |

### Optional Interfaces

| Interface | Purpose | Example |
|-----------|---------|---------|
| `IExtendedWeapon` | Adds per-frame `Update()`, `OnHit()`, `OnHitObject()`, `GetDealtDamage()`, `DrawExtra()` callbacks to any weapon | Caber (explosion on hit), StickyLauncher (detonation key) |
| `ISharpMelee` | Marks a melee weapon as capable of decapitation, defines chance | Blade (33%), Greatsword (100%) |
| `IExtendedProjectile` | Custom hit behavior for projectiles against objects/explosives | Crossbow bolt (sticks into things), NailGun |

---

## 3. Step-by-Step: Adding a Ranged Weapon

We'll walk through adding a hypothetical handgun called **"Derringer"** with ID **110**.

### 3.1 — Choose a Weapon ID

Pick the next available ID. Check `SFR/Weapons/Database.cs` — the `CustomWeaponItem` enum and the weapon list show all used IDs. The current highest is **109** (Anaconda). Use **110**.

> **Important:** The weapon ID must also be the index in `WeaponDatabase.m_weapons[]`. The array is currently sized to 110 entries (indices 0–109). You must increase this size.

### 3.2 — Create Texture Assets

Create PNG files in the appropriate subfolder of `Content/Data/Images/Weapons/`:

For a **handgun**, place files in `Content/Data/Images/Weapons/Handguns/`:

| File | Purpose | Required? |
|------|---------|-----------|
| `DerringerM.png` | **Model** texture — the weapon as it appears lying on the ground as a pickup | **Yes** |
| `DerringerD.png` | **Drawn** texture — the weapon as the player holds it | **Yes** |
| `DerringerDReload.png` | Drawn texture during reload animation frame | Optional |
| `DerringerS.png` | **Sheathed** texture — the weapon on the player's back/hip when not active | Optional |
| `DerringerH.png` | **Holster** texture — alternative holster sprite | Optional |
| `DerringerThrowing.png` | Texture when the weapon is thrown | Recommended |

For a **rifle**, place files in `Content/Data/Images/Weapons/Rifles/`. Same pattern.

### 3.3 — Create the Weapon Class

Create `SFR/Weapons/Handguns/Derringer.cs`:

```csharp
using Microsoft.Xna.Framework;
using SFD;
using SFD.Objects;
using SFD.Sounds;
using SFD.Weapons;

namespace SFR.Weapons.Handguns;

internal sealed class Derringer : RWeapon
{
    internal Derringer()
    {
        // --- PROPERTIES ---
        // Constructor overload 1 (compact): 
        //   RWeaponProperties(id, name, maxMagsInWeapon, maxRoundsInMag, maxCarriedSpareMags,
        //     startMags, cooldownBeforePostAction, cooldownAfterPostAction, extraAutoCooldown,
        //     projectilesEachBlast, projectileID, shellID, accuracyDeflection, muzzlePosition,
        //     muzzleEffectTextureID, blastSoundID, drawSoundID, grabAmmoSoundID, 
        //     outOfAmmoSoundID, modelID, canDualWield, weaponCategory)
        //
        // Constructor overload 2 (minimal, set props individually):
        //   RWeaponProperties(id, name, modelID, canDualWield, weaponCategory) { ... }

        RWeaponProperties weaponProperties = new(110, "Derringer", "WpnDerringer", false, WeaponCategory.Secondary)
        {
            MaxMagsInWeapon = 1,
            MaxRoundsInMag = 2,
            MaxCarriedSpareMags = 6,
            StartMags = 3,
            CooldownBeforePostAction = 500,
            CooldownAfterPostAction = 0,
            ExtraAutomaticCooldown = 200,
            ShellID = "ShellSmall",           // Shell casing object to eject (defined in weapons.sfdx)
            AccuracyDeflection = 0.06f,
            ProjectileID = 24,                // Use an existing projectile ID, OR create a new one (see Section 7)
            MuzzlePosition = new Vector2(5f, -2f),
            MuzzleEffectTextureID = "MuzzleFlashS",
            BlastSoundID = "Magnum",
            DrawSoundID = "MagnumDraw",
            GrabAmmoSoundID = "MagnumReload",
            OutOfAmmoSoundID = "OutOfAmmoHeavy",
            CursorAimOffset = new Vector2(0f, 3.5f),
            LazerPosition = new Vector2(6f, -0.5f),
            AimStartSoundID = "PistolAim",
            AI_DamageOutput = DamageOutputType.High,
            BreakDebris = ["ItemDebrisShiny01"],  // Debris when weapon breaks
            SpecialAmmoBulletsRefill = 6,
            VisualText = "Derringer"              // Display name in UI
        };

        // --- VISUALS (ANIMATIONS) ---
        // These reference animation names from the AnimationsData system.
        // Use Handgun or Rifle animation sets depending on weapon type.
        RWeaponVisuals weaponVisuals = new()
        {
            AnimIdleUpper = "UpperIdleHandgun",
            AnimCrouchUpper = "UpperCrouchHandgun",
            AnimJumpKickUpper = "UpperJumpKickHandgun",
            AnimJumpUpper = "UpperJumpHandgun",
            AnimJumpUpperFalling = "UpperJumpFallingHandgun",
            AnimKickUpper = "UpperKickHandgun",
            AnimStaggerUpper = "UpperStaggerHandgun",
            AnimRunUpper = "UpperRunHandgun",
            AnimWalkUpper = "UpperWalkHandgun",
            AnimUpperHipfire = "UpperHipfireHandgun",
            AnimFireArmLength = 7f,
            AnimDraw = "UpperDrawMagnum",         // Or "UpperDrawHandgun"
            AnimManualAim = "ManualAimHandgun",
            AnimManualAimStart = "ManualAimHandgunStart",
            AnimReloadUpper = "UpperReload",
            AnimFullLand = "FullLandHandgun",
            AnimToggleThrowingMode = "UpperToggleThrowing"
        };

        // --- TEXTURES ---
        weaponVisuals.SetModelTexture("DerringerM");       // Ground pickup sprite
        weaponVisuals.SetDrawnTexture("DerringerD");       // In-hand sprite
        weaponVisuals.SetSheathedTexture("DerringerS");    // On-back sprite (optional)
        weaponVisuals.SetThrowingTexture("DerringerThrowing"); // Thrown sprite (optional)
        // weaponVisuals.SetHolsterTexture("DerringerH");  // Alternative holster (optional)

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

        // Cache extra drawn texture variants (e.g., "Reload" → looks for "DerringerDReload.png")
        CacheDrawnTextures(["Reload"]);
    }

    // Private copy constructor — required for the Copy() method
    private Derringer(RWeaponProperties weaponProperties, RWeaponVisuals weaponVisuals) 
        => SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

    // --- REQUIRED: Copy method ---
    public override RWeapon Copy()
    {
        Derringer copy = new(Properties, Visuals);
        copy.CopyStatsFrom(this);
        return copy;
    }

    // --- OPTIONAL OVERRIDES ---

    // Called when the weapon fires (to play sounds, spawn effects, eject shells)
    public override void OnRecoilEvent(Player player)
    {
        if (player.GameOwner != GameOwnerEnum.Server)
        {
            if (Properties.ShellID != string.Empty && Constants.EFFECT_LEVEL_FULL)
            {
                SpawnUnsyncedShell(player, Properties.ShellID);
            }
            SoundHandler.PlaySound(Properties.BlastSoundID, player.Position, player.GameWorld);
        }
    }

    // Called during reload animation frames
    public override void OnReloadAnimationEvent(Player player, AnimationEvent animEvent, SubAnimationPlayer subAnim)
    {
        if (player.GameOwner != GameOwnerEnum.Server && animEvent == AnimationEvent.EnterFrame)
        {
            if (subAnim.GetCurrentFrameIndex() == 1)
            {
                SpawnMagazine(player, "MagSmall", new Vector2(-6f, -5f));
                SoundHandler.PlaySound("MagnumReloadEnd", player.Position, player.GameWorld);
            }
            if (subAnim.GetCurrentFrameIndex() == 4)
            {
                SoundHandler.PlaySound("PistolReload", player.Position, player.GameWorld);
            }
        }
    }

    // Called during draw animation frames
    public override void OnSubAnimationEvent(Player player, AnimationEvent animationEvent, AnimationData animationData, int currentFrameIndex)
    {
        if (player.GameOwner != GameOwnerEnum.Server && animationEvent == AnimationEvent.EnterFrame)
        {
            if (animationData.Name == "UpperDrawMagnum")
            {
                switch (currentFrameIndex)
                {
                    case 1:
                        SoundHandler.PlaySound("Draw1", player.GameWorld);
                        break;
                    case 6:
                        SoundHandler.PlaySound("MagnumDraw", player.GameWorld);
                        break;
                }
            }
        }
    }

    // Called when the weapon is thrown — adjust velocity/spin
    public override void OnThrowWeaponItem(Player player, ObjectWeaponItem thrownWeaponItem)
    {
        Vector2 linearVelocity = thrownWeaponItem.Body.GetLinearVelocity();
        linearVelocity.X *= 1.2f;
        linearVelocity.Y *= 1f;
        thrownWeaponItem.Body.SetLinearVelocity(linearVelocity);
    }

    // Switch drawn texture during reload frames
    public override Texture2D GetDrawnTexture(ref GetDrawnTextureArgs args)
    {
        if (args.SubAnimation is "UpperReload" && args.SubFrame is >= 1 and <= 5)
        {
            args.Postfix = "Reload";
        }
        return base.GetDrawnTexture(ref args);
    }

    // Control when laser attachment is visible
    public override bool CheckDrawLazerAttachment(string subAnimation, int subFrame) 
        => subAnimation is not "UpperReload";
}
```

### 3.4 — Register in Weapons Database

Edit `SFR/Weapons/Database.cs`:

**A) Increase the array size:**
```csharp
WeaponDatabase.m_weapons = new WeaponItem[111]; // Was 110, now 111
```

**B) Add the weapon to the `Weapons` list** (in the appropriate category section):
```csharp
// Handgun
new WeaponItem(WeaponItemType.Handgun, new Derringer()), // 110
```

**C) Add spawn chance** (in the `SpawnChance` method):
```csharp
{ 110, 15 }, // Derringer
```
The number is a relative weight — higher = more common. Set to 0 or comment out to prevent random spawning.

**D) Add to the `CustomWeaponItem` enum:**
```csharp
Derringer = 110,  // At the end, before closing brace
```

**E) Add the `using` directive** if it's a new namespace (already exists for `SFR.Weapons.Handguns`).

### 3.5 — Define the Tile in weapons.sfdx

Add a tile definition in `Content/Data/Weapons/weapons.sfdx`. This defines the weapon's **physical object** in the world (how it looks on the ground, its collision shape, pickup behavior):

```
Tile(WpnDerringer)
{
	tileTexture = DerringerM;
	pickupType = instant;
	pickupRange = 10.0;
	absorbProjectile = false;
	projectileHit = false;
	impactEffect = ImpactDefault;
	impactSound = WeaponBounce;
	missileDamageMax = 10;
	fixture()
	{
		collisionGroup = debris;
		mass = 2.5 kg;
		collisionPoints = (-3.5, 1.5), (3.5, 1.5), (3.5, 3.5), (-3.5, 3.5);
		blockFire = false;
	}
	fixture(grip)
	{
		collisionGroup = debris;
		mass = 2.5 kg;
		collisionPoints = (-3.5, -1.5), (-1.5, -1.5), (-1.5, 1.5), (-3.5, 1.5);
		blockFire = false;
	}
}
```

> **Important**: The tile name (`WpnDerringer`) must match the `ModelID` parameter in your `RWeaponProperties` constructor, converted to uppercase. The `ObjectsHandler` automatically maps any weapon's `ModelID` (uppercased) to an `ObjectWeaponItem`.

### 3.6 — Add Debris Tiles (if custom)

If your weapon uses custom `BreakDebris` IDs, define those tiles too:

```
Tile(DerringerDebris1)
{
	editorEnabled = false;
	punchable = true;
	kickable = true;
	material = metal;
	fixture()
	{
		collisionPoints = (-1.5, -2.5), (1.5, -2.5), (1.5, 2.5), (-1.5, 1.5);
		blockFire = false;
	}
}
```

And create a matching texture PNG in `Content/Data/Images/Weapons/` (e.g., `DerringerDebris1.png`).

### 3.7 — Done!

That's all that's required for a basic ranged weapon. The tile→weapon mapping is automatic through `ObjectsHandler.cs` which iterates `Database.Weapons` and matches by `ModelID`.

---

## 4. Step-by-Step: Adding a Melee Weapon

### 4.1 — Create Textures

Place in `Content/Data/Images/Weapons/Melee/`:

| File | Purpose | Required? |
|------|---------|-----------|
| `MyWeaponM.png` | Model (ground pickup) | **Yes** |
| `MyWeaponD.png` | Drawn (in hand) | **Yes** |
| `MyWeaponS.png` | Sheathed (on back) | Recommended |
| `MyWeaponDebris1.png` | Debris when weapon breaks | Optional |

### 4.2 — Create the Weapon Class

Create `SFR/Weapons/Melee/Mace.cs`:

```csharp
using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Materials;
using SFD.Objects;
using SFD.Sounds;
using SFD.Weapons;

namespace SFR.Weapons.Melee;

internal sealed class Mace : MWeapon
{
    internal Mace()
    {
        // MWeaponProperties(id, name, damageObjects, damagePlayers, swingSoundID,
        //   hitSoundID, hitEffect, blockSoundID, blockEffect, drawSoundID,
        //   modelID, deflectsProjectiles, weaponCategory, isMakeshift)
        MWeaponProperties weaponProperties = new(110, "Mace", 9.5f, 13f, 
            "MeleeSwing", "MeleeHitBlunt", "HIT_B", "MeleeBlock", "HIT", 
            "MeleeDraw", "WpnMace", true, WeaponCategory.Melee, false)
        {
            // One-handed: MeleeWeaponTypeEnum.OneHanded
            // Two-handed: MeleeWeaponTypeEnum.TwoHanded
            MeleeWeaponType = MeleeWeaponTypeEnum.OneHanded,
            WeaponMaterial = MaterialDatabase.Get("metal"),      // "metal", "wood", "stone"
            DurabilityLossOnHitObjects = 4f,
            DurabilityLossOnHitPlayers = 8f,
            DurabilityLossOnHitBlockingPlayers = 4f,
            ThrownDurabilityLossOnHitPlayers = 20f,
            ThrownDurabilityLossOnHitBlockingPlayers = 10f,
            DeflectionDuringBlock =
            {
                DeflectType = DeflectBulletType.Deflect,   // or .Absorb
                DurabilityLoss = 4f
            },
            DeflectionOnAttack =
            {
                DeflectType = DeflectBulletType.Deflect,
                DurabilityLoss = 4f
            },
            BreakDebris = ["MetalDebris00A", "MaceDebris1"],
            AI_DamageOutput = DamageOutputType.Standard,
            VisualText = "Mace"
        };

        MWeaponVisuals weaponVisuals = new()
        {
            // --- ONE-HANDED animations ---
            AnimBlockUpper = "UpperBlockMelee",
            AnimMeleeAttack1 = "UpperMelee1H1",
            AnimMeleeAttack2 = "UpperMelee1H2",
            AnimMeleeAttack3 = "UpperMelee1H3",
            AnimFullJumpAttack = "FullJumpAttackMelee",
            AnimDraw = "UpperDrawMelee",            // or "UpperDrawMeleeSheathed"
            AnimCrouchUpper = "UpperCrouchMelee",
            AnimIdleUpper = "UpperIdleMelee",
            AnimJumpKickUpper = "UpperJumpKickMelee",
            AnimJumpUpper = "UpperJumpMelee",
            AnimJumpUpperFalling = "UpperJumpFallingMelee",
            AnimKickUpper = "UpperKickMelee",
            AnimStaggerUpper = "UpperStagger",
            AnimRunUpper = "UpperRunMelee",
            AnimWalkUpper = "UpperWalkMelee",
            AnimFullLand = "FullLandMelee",
            AnimToggleThrowingMode = "UpperToggleThrowing"

            // --- TWO-HANDED animations (use these instead if TwoHanded) ---
            // AnimBlockUpper = "UpperBlockMelee2H",
            // AnimMeleeAttack1 = "UpperMelee2H1",
            // AnimMeleeAttack2 = "UpperMelee2H2",
            // AnimMeleeAttack3 = "UpperMelee2H3",
            // AnimCrouchUpper = "UpperCrouchMelee2H",
            // AnimIdleUpper = "UpperIdleMelee2H",
            // AnimJumpUpper = "UpperJumpMelee2H",
            // AnimJumpUpperFalling = "UpperJumpFallingMelee2H",
            // AnimKickUpper = "UpperKickMelee2H",
            // AnimRunUpper = "UpperRunMelee2H",
            // AnimWalkUpper = "UpperWalkMelee2H",
        };

        weaponVisuals.SetModelTexture("MaceM");
        weaponVisuals.SetDrawnTexture("MaceD");
        weaponVisuals.SetSheathedTexture("MaceS");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
    }

    private Mace(MWeaponProperties wp, MWeaponVisuals wv) => SetPropertiesAndVisuals(wp, wv);

    public override MWeapon Copy() => new Mace(Properties, Visuals)
    {
        Durability = { CurrentValue = Durability.CurrentValue }
    };

    public override void OnThrowWeaponItem(Player player, ObjectWeaponItem thrownWeaponItem)
    {
        thrownWeaponItem.Body.SetAngularVelocity(thrownWeaponItem.Body.GetAngularVelocity() * 0.9f);
        Vector2 lv = thrownWeaponItem.Body.GetLinearVelocity();
        lv.X *= 0.9f;
        lv.Y *= 0.9f;
        thrownWeaponItem.Body.SetLinearVelocity(lv);
        base.OnThrowWeaponItem(player, thrownWeaponItem);
    }

    public override void Destroyed(Player ownerPlayer)
    {
        SoundHandler.PlaySound("DestroyMetal", ownerPlayer.GameWorld);
        EffectHandler.PlayEffect("DestroyMetal", ownerPlayer.Position, ownerPlayer.GameWorld);
        Vector2 center = new(ownerPlayer.Position.X, ownerPlayer.Position.Y + 16f);
        ownerPlayer.GameWorld.SpawnDebris(ownerPlayer.ObjectData, center, 8f, ["MetalDebris00A", "MaceDebris1"]);
    }
}
```

### 4.3 — Register, Define Tile, etc.

Same process as ranged weapons (Sections 3.4–3.6), but use `WeaponItemType.Melee`:

```csharp
new WeaponItem(WeaponItemType.Melee, new Mace()), // 110
```

### 4.4 — Making it Sharp (Optional)

To allow decapitation, implement `ISharpMelee`:

```csharp
internal sealed class Mace : MWeapon, ISharpMelee
{
    public float GetDecapitationChance() => 0.33f;  // 33% chance
    // ...
}
```

### 4.5 — Custom Melee Handling (Optional)

For special attack behavior, set `Handling = MeleeHandlingType.Custom` in properties and override:

```csharp
public override bool CustomHandlingOnAttackKey(Player player, bool onKeyEvent)
{
    // Return true to consume the attack, false for default behavior
    if (onKeyEvent && player.CurrentAction is PlayerAction.Idle && !player.InAir)
    {
        player.CurrentAction = PlayerAction.MeleeAttack3;
    }
    return true;
}
```

---

## 5. Step-by-Step: Adding a Thrown Weapon

### 5.1 — Create Textures

Place in `Content/Data/Images/Weapons/Thrown/`:

| File | Purpose |
|------|---------|
| `MyGrenadeM.png` | Model (in-hand / pickup) |
| `MyGrenadeT.png` | Thrown (in-flight) |

### 5.2 — Create the Weapon Class

```csharp
using SFD;
using SFD.Sounds;
using SFD.Weapons;

namespace SFR.Weapons.Thrown;

internal sealed class MyGrenade : TWeapon
{
    internal MyGrenade()
    {
        TWeaponProperties weaponProperties = new(110, "My_Grenades", "WpnMyGrenades", false, WeaponCategory.Supply)
        {
            MaxCarriedTotalThrowables = 5,
            NumberOfThrowables = 3,
            ThrowObjectID = "WpnMyGrenadesThrown",   // Must match tile in weapons.sfdx
            ThrowDeadlineTimer = 2550f,               // Time before auto-drop (0 = none)
            DrawSoundID = "GrenadeDraw",
            VisualText = "My Grenades"
        };

        TWeaponVisuals weaponVisuals = new()
        {
            AnimDraw = "UpperDrawThrown",
            AnimManualAim = "ManualAimThrown",
            AnimManualAimStart = "ManualAimThrownStart",
            AnimCrouchUpper = "UpperCrouch",
            AnimIdleUpper = "UpperIdle",
            AnimJumpKickUpper = "UpperJumpKick",
            AnimJumpUpper = "UpperJump",
            AnimJumpUpperFalling = "UpperJumpFalling",
            AnimKickUpper = "UpperKick",
            AnimStaggerUpper = "UpperStagger",
            AnimRunUpper = "UpperRun",
            AnimWalkUpper = "UpperWalk",
            AnimFullLand = "FullLandThrown",
            AnimToggleThrowingMode = "UpperToggleThrowing"
        };

        weaponVisuals.SetModelTexture("MyGrenadeM");
        weaponVisuals.SetDrawnTexture("MyGrenadeT");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
        NumberOfThrowablesLeft = Properties.NumberOfThrowables;
    }

    private MyGrenade(TWeaponProperties wp, TWeaponVisuals wv)
    {
        SetPropertiesAndVisuals(wp, wv);
        NumberOfThrowablesLeft = wp.NumberOfThrowables;
    }

    public override void OnBeforeBeginCharge(TWeaponBeforeBeginChargeArgs e) { }

    public override void OnThrow(TWeaponOnThrowArgs e)
    {
        if (e.Player.GameOwner != GameOwnerEnum.Server)
            SoundHandler.PlaySound("GrenadeThrow", e.Player.Position, e.Player.GameWorld);
    }

    public override void OnBeginCharge(TWeaponOnBeginChargeArgs e)
    {
        if (e.Player.GameOwner != GameOwnerEnum.Server)
            SoundHandler.PlaySound("GrenadeSafe", e.Player.Position, e.Player.GameWorld);
    }

    public override void OnDrop(TWeaponOnThrowArgs e) { }
    public override void OnDeadline(TWeaponOnDeadlineArgs e) => e.Action = TWeaponDeadlineAction.Drop;

    public override TWeapon Copy() => new MyGrenade(Properties, Visuals)
    {
        NumberOfThrowablesLeft = NumberOfThrowablesLeft
    };
}
```

### 5.3 — Create the Thrown Object (See Section 8)

Thrown weapons need a corresponding **thrown object class** in `SFR/Objects/` and a **thrown tile** in `weapons.sfdx`.

### 5.4 — Register

Register with `WeaponItemType.Thrown`:
```csharp
new WeaponItem(WeaponItemType.Thrown, new MyGrenade()), // 110
```

---

## 6. Step-by-Step: Adding a Pickup / Powerup Item

### Powerup (PItem) — like Health Pouch

```csharp
using SFD;
using SFD.Sounds;
using SFD.Tiles;
using SFD.Weapons;

namespace SFR.Weapons.Others;

internal sealed class MyPotion : PItem
{
    internal MyPotion()
    {
        PItemProperties itemProperties = new(110, "My_Potion", "ItemMyPotion", false, WeaponCategory.Supply)
        {
            PickupSoundID = "GetSlomo",
            ActivateSoundID = "GetHealthSmall",
            VisualText = "My Potion"
        };

        PItemVisuals visuals = new(Textures.GetTexture("MyPotion"));
        SetPropertiesAndVisuals(itemProperties, visuals);
    }

    private MyPotion(PItemProperties p, PItemVisuals v) => SetPropertiesAndVisuals(p, v);

    public override void OnActivation(Player player, PItem powerupItem)
    {
        SoundHandler.PlaySound(powerupItem.Properties.ActivateSoundID, player.Position, player.GameWorld);
        player.HealAmount(25f);
        player.RemovePowerup();
    }

    public override PItem Copy() => new MyPotion(Properties, Visuals);
}
```

Register with `WeaponItemType.Powerup`. Define an `ItemMyPotion` tile in `weapons.sfdx`.

### Instant Pickup (HItem) — like Jetpack

```csharp
internal sealed class MyPickup : HItem
{
    internal MyPickup()
    {
        HItemProperties itemProperties = new(110, "My_Pickup", "ItemMyPickup", false, WeaponCategory.Supply)
        {
            GrabSoundID = "GetHealthSmall",
            VisualText = "My Pickup"
        };
        HItemVisuals visuals = new(Textures.GetTexture("MyPickup"));
        Properties = itemProperties;
        Visuals = visuals;
    }
    
    private MyPickup(HItemProperties p, HItemVisuals v) : base(p, v) { }
    
    public override void OnPickup(Player player, HItem item)
    {
        // Custom pickup logic
        SoundHandler.PlaySound(item.Properties.GrabSoundID, player.Position, player.GameWorld);
    }
    
    public override bool CheckDoPickup(Player player, HItem item) => true;
    public override HItem Copy() => new MyPickup(Properties, Visuals);
}
```

Register with `WeaponItemType.InstantPickup`.

---

## 7. Adding a Custom Projectile

If your ranged weapon needs custom bullet behavior (not reusing an existing projectile), create a new projectile class.

### 7.1 — Create Projectile Textures

Place in `Content/Data/Images/Weapons/Projectiles/`:
- `BulletMyWeapon.png` — normal view
- `BulletMyWeaponSlowmo.png` — slowmo view (can be the same texture)

### 7.2 — Create the Projectile Class

Create `SFR/Projectiles/ProjectileDerringer.cs`:

```csharp
using SFD.Projectiles;
using SFD.Tiles;

namespace SFR.Projectiles;

internal sealed class ProjectileDerringer : Projectile
{
    internal ProjectileDerringer()
    {
        // Use existing common textures or load custom ones
        Visuals = new ProjectileVisuals(
            Textures.GetTexture("BulletMyWeapon"),       // Normal
            Textures.GetTexture("BulletMyWeaponSlowmo")  // Slowmo
        );

        // ProjectileProperties(id, range, speed, damageHP, damageObjects,
        //   playerDodgeChance, critDamageHP, critDamageObjects, critChance)
        Properties = new ProjectileProperties(110, 800f, 50f, 18f, 15f, 0.2f, 25f, 20f, 0.3f)
        {
            PowerupBounceRandomAngle = 0f,
            PowerupFireType = ProjectilePowerupFireType.Default,
            PowerupFireIgniteValue = 50f
        };
    }

    private ProjectileDerringer(ProjectileProperties pp, ProjectileVisuals pv) : base(pp, pv) { }

    public override Projectile Copy()
    {
        ProjectileDerringer p = new(Properties, Visuals);
        p.CopyBaseValuesFrom(this);
        return p;
    }

    // OPTIONAL: Override for gravity-affected projectiles
    // public override void Update(float ms)
    // {
    //     Velocity -= Vector2.UnitY * ms * 0.5f;
    // }

    // OPTIONAL: Override for custom hit behavior
    // public override void HitPlayer(Player player, ObjectData playerObjectData)
    // {
    //     base.HitPlayer(player, playerObjectData);
    // }
}
```

### 7.3 — Register in Projectile Database

Edit `SFR/Projectiles/Database.cs`:

```csharp
[HarmonyPostfix]
[HarmonyPatch(typeof(ProjectileDatabase), nameof(ProjectileDatabase.Load))]
private static void LoadProjectiles()
{
    Array.Resize(ref ProjectileDatabase.projectiles, 111);  // Increase size
    // ... existing entries ...
    ProjectileDatabase.projectiles[110] = new ProjectileDerringer();
}
```

### 7.4 — Set ProjectileID in Weapon

In your weapon class, set `ProjectileID = 110` to match.

### 7.5 — Using IExtendedProjectile (Optional)

For projectiles that need special behavior when hitting objects/explosives:

```csharp
internal sealed class ProjectileDerringer : Projectile, IExtendedProjectile
{
    public bool OnHit(Projectile projectile, ProjectileHitEventArgs e, ObjectData objectData) => true;
    public bool OnExplosiveHit(Projectile projectile, ProjectileHitEventArgs e, ObjectExplosive objectData)
    {
        ObjectDataMethods.ApplyProjectileHitImpulse(objectData, projectile, e);
        return false;  // false = skip original hit handler
    }
    public bool OnExplosiveBarrelHit(Projectile projectile, ProjectileHitEventArgs e, ObjectBarrelExplosive objectData)
    {
        ObjectDataMethods.ApplyProjectileHitImpulse(objectData, projectile, e);
        return false;
    }
}
```

---

## 8. Adding a Thrown Object (Grenade-Like)

For thrown weapons (grenades, claymores, sticky bombs), you need a **thrown object** — the physical object that exists in the world after being thrown.

### 8.1 — Create the Object Class

Create `SFR/Objects/ObjectMyGrenadeThrown.cs` by following the pattern from existing thrown objects (e.g., `ObjectFragGrenadeThrown`, `ObjectStickyBombThrown`, `ObjectClaymoreThrown`).

Key points:
- Inherit from the appropriate SFD base class (e.g., `ObjectGrenade` or `ObjectData`)
- Implement explosion/detonation logic
- Handle the `ExplosionTimer` for timed detonation

### 8.2 — Define the Thrown Tile in weapons.sfdx

```
Tile(WpnMyGrenadesThrown)
{
	absorbProjectile = true;
	projectileHit = true;
	tileTexture = MyGrenadeT;
	drawCategory = THRN;
	mainLayer = 1;
	editorEnabled = true;
	material.restitution = 0.3;
	material.blockExplosions = false;
	material.hit.melee.sound = MeleeBlock;
	material.hit.melee.effect = HIT;
	material.hit.Melee.Prio = 10;
	material.resistance.impact.modifier = 0.0;
	gibpressure.total = 250 kg;
	gibpressure.spike = 1600 kg;
	kickable = true;
	punchable = true;
	impactEffect = ImpactDefault;
	impactSound = GrenadeBounce;
	type = Dynamic;
	fixture()
	{
		mass = 5 kg;
		collisionGroup = dynamics_thrown;
		circle = (-0.5, 0.5, 2);
		blockFire = false;
	}
}
```

### 8.3 — Register in ObjectsHandler

Edit `SFR/Objects/ObjectsHandler.cs` and add a case in the `LoadObjects` method's switch statement:

```csharp
case "WPNMYGRENADESTHROWN":
    __result = new ObjectMyGrenadeThrown(startParams);
    return false;
```

> **Note:** Standard weapon pickups do NOT need to be added here — they are auto-matched via the `Database.Weapons` loop at the top of `LoadObjects`. Only special objects (thrown projectiles, custom objects) need explicit cases.

---

## 9. File Reference & What Each File Does

### Code Files

| File | Purpose |
|------|---------|
| `SFR/Weapons/Database.cs` | **Central registry** — defines all weapons, spawn chances, and the `CustomWeaponItem` enum. Harmony-patches `WeaponDatabase.Load()` |
| `SFR/Weapons/IExtendedWeapon.cs` | Interface for weapons needing extra callbacks (Update, OnHit, DrawExtra). Also contains Harmony patches that dispatch these callbacks |
| `SFR/Weapons/ISharpMelee.cs` | Interface for melee weapons that can decapitate |
| `SFR/Weapons/Handguns/*.cs` | Handgun weapon implementations (extend `RWeapon`) |
| `SFR/Weapons/Rifles/*.cs` | Rifle weapon implementations (extend `RWeapon`) |
| `SFR/Weapons/Melee/*.cs` | Melee weapon implementations (extend `MWeapon`) |
| `SFR/Weapons/Thrown/*.cs` | Thrown weapon implementations (extend `TWeapon`) |
| `SFR/Weapons/Makeshift/*.cs` | Makeshift melee weapons (extend `MWeapon`, `isMakeshift=true`) |
| `SFR/Weapons/Others/*.cs` | Powerups and instant pickups (`PItem`, `HItem`) |
| `SFR/Projectiles/Database.cs` | **Central registry** for projectiles. Harmony-patches `ProjectileDatabase.Load()` |
| `SFR/Projectiles/IExtendedProjectile.cs` | Interface for projectiles with custom hit behavior |
| `SFR/Projectiles/Projectile*.cs` | Individual projectile implementations |
| `SFR/Objects/ObjectsHandler.cs` | Maps tile IDs to C# object instances for spawning. Handles weapon pickups, thrown objects, and other custom objects |
| `SFR/Objects/Object*.cs` | Custom thrown/world objects (grenades, bolts, sticky bombs, etc.) |
| `SFR/Bootstrap/Assets.cs` | Loads all SFR content (textures, sounds, tiles, animations) by patching SFD's loading methods |
| `SFR/Bootstrap/AnimationHandler.cs` | Loads custom animation data from `.txt` files |
| `SFR/Fighter/GoreHandler.cs` | Gore/decapitation system — uses `ISharpMelee` |
| `SFR/Fighter/PlayerHandler.cs` | Player state patches — dispatches `IExtendedWeapon.Update()` |

### Content Files

| Path | Purpose |
|------|---------|
| `Content/Data/Weapons/weapons.sfdx` | Tile definitions for all weapon world objects (collision, textures, physics) |
| `Content/Data/Images/Weapons/Handguns/` | Handgun texture PNGs |
| `Content/Data/Images/Weapons/Rifles/` | Rifle texture PNGs |
| `Content/Data/Images/Weapons/Melee/` | Melee weapon texture PNGs |
| `Content/Data/Images/Weapons/Thrown/` | Thrown weapon texture PNGs |
| `Content/Data/Images/Weapons/Projectiles/` | Projectile/bullet texture PNGs |
| `Content/Data/Images/Weapons/Other/` | Powerup/pickup texture PNGs |
| `Content/Data/Images/Weapons/Makeshift/` | Makeshift weapon texture PNGs |
| `Content/Data/Sounds/Sounds.sfds` | Sound definitions (name, pitch, wav file path) |
| `Content/Data/Sounds/Weapons/` | Weapon sound WAV files |

---

## 10. Optional Interfaces

### IExtendedWeapon

Implement on any weapon class (melee or ranged) that needs per-frame updates or custom hit callbacks. The Harmony patches in `IExtendedWeapon.cs` automatically detect when a weapon implements this interface and call the methods.

```csharp
internal sealed class MyWeapon : MWeapon, IExtendedWeapon
{
    public void Update(Player player, float ms, float realMs) { /* Called every frame */ }
    public void GetDealtDamage(Player player, float damage) { /* Called when durability is lost */ }
    public void OnHit(Player player, Player target) { /* Called after hitting a player */ }
    public void OnHitObject(Player player, PlayerHitEventArgs args, ObjectData obj) { /* Called after hitting an object */ }
    public void DrawExtra(SpriteBatch spritebatch, Player player, float ms) { /* Custom drawing */ }
}
```

**Examples:**
- **Caber**: Uses `GetDealtDamage()` and `Destroyed()` to trigger an explosion
- **StickyLauncher**: Uses `Update()` to check for detonation key press

### ISharpMelee

Simple interface for melee weapons capable of decapitation. The `GoreHandler` checks for this.

```csharp
internal sealed class MyBlade : MWeapon, ISharpMelee
{
    public float GetDecapitationChance() => 0.5f;  // 50% chance per lethal hit
}
```

### IExtendedProjectile

For projectiles needing custom object-hit logic (e.g., not exploding barrels on contact, applying impulse instead).

```csharp
internal sealed class MyProjectile : Projectile, IExtendedProjectile
{
    public bool OnHit(Projectile p, ProjectileHitEventArgs e, ObjectData obj) => true;        // true = run original
    public bool OnExplosiveHit(Projectile p, ProjectileHitEventArgs e, ObjectExplosive obj) => false;  // false = skip original
    public bool OnExplosiveBarrelHit(Projectile p, ProjectileHitEventArgs e, ObjectBarrelExplosive obj) => false;
}
```

---

## 11. Property Reference Tables

### RWeaponProperties (Ranged Weapons)

| Property | Type | Description |
|----------|------|-------------|
| `WeaponID` (constructor) | `int` | Unique weapon ID |
| `Name` (constructor) | `string` | Internal name |
| `ModelID` (constructor) | `string` | Tile ID in weapons.sfdx (e.g., "WpnDerringer") |
| `CanDualWield` (constructor) | `bool` | Whether two can be held simultaneously |
| `WeaponCategory` (constructor) | `enum` | `Primary` or `Secondary` |
| `MaxMagsInWeapon` | `int` | Magazines that fit in the weapon at once |
| `MaxRoundsInMag` | `int` | Rounds per magazine |
| `MaxCarriedSpareMags` | `int` | Extra mags the player can carry |
| `StartMags` | `int` | Mags weapon starts with |
| `CooldownBeforePostAction` | `int` | Ms delay before post-fire action |
| `CooldownAfterPostAction` | `int` | Ms delay after post-fire action |
| `ExtraAutomaticCooldown` | `int` | Additional cooldown for automatic fire |
| `ProjectilesEachBlast` | `int` | Pellets per shot (shotguns) |
| `ShellID` | `string` | Shell casing tile ID |
| `AccuracyDeflection` | `float` | Bullet spread |
| `ProjectileID` | `int` | Which projectile to fire (index in ProjectileDatabase) |
| `MuzzlePosition` | `Vector2` | Muzzle flash offset |
| `MuzzleEffectTextureID` | `string` | Muzzle flash texture |
| `BlastSoundID` | `string` | Firing sound |
| `DrawSoundID` | `string` | Draw/equip sound |
| `GrabAmmoSoundID` | `string` | Reload sound |
| `OutOfAmmoSoundID` | `string` | Empty click sound |
| `CursorAimOffset` | `Vector2` | Aim cursor offset |
| `LazerPosition` | `Vector2` | Laser sight origin |
| `AimStartSoundID` | `string` | Sound when starting to aim |
| `AI_DamageOutput` | `enum` | AI damage classification |
| `BreakDebris` | `string[]` | Debris tile IDs when weapon breaks |
| `SpecialAmmoBulletsRefill` | `int` | Ammo given by ammo pickups |
| `VisualText` | `string` | Display name |
| `CanRefilAtAmmoStashes` | `bool` | Whether ammo crates refill this weapon |
| `BurstRoundsToFire` | `int` | Rounds per burst (burst fire mode) |
| `BurstCooldown` | `int` | Ms between burst rounds |
| `ReloadPostCooldown` | `float` | Extra delay after reload |
| `ClearRoundsOnReloadStart` | `bool` | Clear remaining rounds when reloading |

### MWeaponProperties (Melee Weapons)

| Property | Type | Description |
|----------|------|-------------|
| `WeaponID` | `int` | Unique weapon ID |
| `Name` | `string` | Internal name |
| `DamageObjects` | `float` | Damage to world objects |
| `DamagePlayers` | `float` | Damage to players |
| `Range` | `float` | Hit range |
| `MeleeWeaponType` | `enum` | `OneHanded` or `TwoHanded` |
| `Handling` | `enum` | `Default` or `Custom` |
| `WeaponMaterial` | `Material` | `MaterialDatabase.Get("metal"/"wood"/"stone")` |
| `DurabilityLossOnHitObjects` | `float` | Durability lost hitting objects |
| `DurabilityLossOnHitPlayers` | `float` | Durability lost hitting players |
| `DurabilityLossOnHitBlockingPlayers` | `float` | Durability lost hitting blocking players |
| `ThrownDurabilityLossOnHitPlayers` | `float` | Durability lost when thrown at players |
| `DeflectionDuringBlock.DeflectType` | `enum` | `Deflect` or `Absorb` bullets while blocking |
| `DeflectionDuringBlock.DurabilityLoss` | `float` | Durability cost per deflection |
| `BreakDebris` | `string[]` | Debris tile IDs |
| `IsMakeshift` (constructor) | `bool` | True for furniture-type items (chair, bottle, etc.) |
| `DeflectsProjectiles` (constructor) | `bool` | Can deflect bullets |

### TWeaponProperties (Thrown Weapons)

| Property | Type | Description |
|----------|------|-------------|
| `WeaponID` | `int` | Unique weapon ID |
| `Name` | `string` | Internal name |
| `ModelID` | `string` | Tile ID (pickup) |
| `MaxCarriedTotalThrowables` | `int` | Max carried |
| `NumberOfThrowables` | `int` | Starting count |
| `ThrowObjectID` | `string` | Object tile ID spawned when thrown |
| `ThrowDeadlineTimer` | `float` | Auto-drop timer (ms) |
| `DrawSoundID` | `string` | Draw sound |

### ProjectileProperties

| Property | Type | Description |
|----------|------|-------------|
| `ID` | `int` | Must match weapon's `ProjectileID` |
| `Range` | `float` | Max distance in pixels |
| `Speed` | `float` | Travel speed |
| `DamageHP` | `float` | Damage to players |
| `DamageObjects` | `float` | Damage to objects |
| `PlayerDodgeChance` | `float` | Chance player dodges (0–1) |
| `CritDamageHP` | `float` | Critical hit damage to players |
| `CritDamageObjects` | `float` | Critical hit damage to objects |
| `CritChance` | `float` | Critical hit chance (0–1) |
| `PowerupBounceRandomAngle` | `float` | Bounce angle randomness |
| `PowerupFireType` | `enum` | `Default` or `Fireplosion` |
| `PowerupFireIgniteValue` | `float` | Fire ignition amount |
| `PowerupTotalBounces` | `int` | Bounces with bounce powerup |
| `CanBeAbsorbedOrBlocked` | `bool` | Whether shields/blocking stops it |
| `DodgeChance` | `int` | Override dodge chance |
| `PlayerForce` | `float` | Knockback force on players |
| `ObjectForce` | `float` | Impulse force on objects |

---

## 12. Animation Reference

### Handgun Animations
| Slot | Animation Name |
|------|---------------|
| Idle | `UpperIdleHandgun` |
| Crouch | `UpperCrouchHandgun` |
| Jump | `UpperJumpHandgun` |
| Jump Falling | `UpperJumpFallingHandgun` |
| Jump Kick | `UpperJumpKickHandgun` |
| Kick | `UpperKickHandgun` |
| Stagger | `UpperStaggerHandgun` |
| Run | `UpperRunHandgun` |
| Walk | `UpperWalkHandgun` |
| Hipfire | `UpperHipfireHandgun` |
| Manual Aim | `ManualAimHandgun` |
| Aim Start | `ManualAimHandgunStart` |
| Draw | `UpperDrawHandgun` or `UpperDrawMagnum` |
| Reload | `UpperReload` or `UpperReloadBazooka` |
| Land | `FullLandHandgun` |
| Toggle Throw | `UpperToggleThrowing` |

### Rifle Animations
| Slot | Animation Name |
|------|---------------|
| Idle | `UpperIdleRifle` |
| Crouch | `UpperCrouchRifle` |
| Jump | `UpperJumpRifle` |
| Jump Falling | `UpperJumpFallingRifle` |
| Jump Kick | `UpperJumpKickRifle` |
| Kick | `UpperKickRifle` |
| Run | `UpperRunRifle` |
| Walk | `UpperWalkRifle` |
| Hipfire | `UpperHipfireRifle` |
| Manual Aim | `ManualAimRifle` |
| Aim Start | `ManualAimRifleStart` |
| Draw | `UpperDrawRifle` or `UpperDrawShotgun` |
| Reload | `UpperReload` or `UpperReloadBazooka` or `UpperReloadShell` |

### Melee Animations (One-Handed)
| Slot | Animation Name |
|------|---------------|
| Idle | `UpperIdleMelee` |
| Block | `UpperBlockMelee` |
| Attack 1 | `UpperMelee1H1` |
| Attack 2 | `UpperMelee1H2` |
| Attack 3 | `UpperMelee1H3` |
| Jump Attack | `FullJumpAttackMelee` |
| Draw | `UpperDrawMelee` |

### Melee Animations (Two-Handed)
| Slot | Animation Name |
|------|---------------|
| Idle | `UpperIdleMelee2H` |
| Block | `UpperBlockMelee2H` |
| Attack 1 | `UpperMelee2H1` |
| Attack 2 | `UpperMelee2H2` |
| Attack 3 | `UpperMelee2H3` |
| Crouch | `UpperCrouchMelee2H` |
| Jump | `UpperJumpMelee2H` |
| Jump Falling | `UpperJumpFallingMelee2H` |
| Kick | `UpperKickMelee2H` |
| Run | `UpperRunMelee2H` |
| Walk | `UpperWalkMelee2H` |

### Melee Animations (Slow / Very Slow — Heavy Weapons)
| Speed | Block | Attack 1 | Attack 2 | Attack 3 | Jump Attack |
|-------|-------|----------|----------|----------|-------------|
| Slow | `UpperBlockMelee2HSlow` | — | — | `UpperMelee2H3Slow` | `FullJumpAttackMeleeSlow` |
| Very Slow | `UpperBlockMelee2HVerySlow` | `UpperMelee2H1VerySlow` | `UpperMelee2H2VerySlow` | `UpperMelee2H3VerySlow` | `FullJumpAttackMeleeVerySlow` |

### Thrown Weapon Animations
| Slot | Animation Name |
|------|---------------|
| Draw | `UpperDrawThrown` |
| Aim | `ManualAimThrown` |
| Aim Start | `ManualAimThrownStart` |
| All other slots | Use base animations (`UpperIdle`, `UpperCrouch`, etc.) |

---

## 13. Texture Naming Conventions

Textures are loaded by name from `Content/Data/Images/`. The name you pass to `SetModelTexture()`, `SetDrawnTexture()`, etc. must match the PNG filename (without extension).

| Suffix | Purpose | Example |
|--------|---------|---------|
| `M` | Model (ground pickup) | `BladeM.png` |
| `D` | Drawn (in-hand) | `BladeD.png` |
| `S` | Sheathed (on back) | `BladeS.png` |
| `H` | Holster | `ScytheH.png` |
| `MH` | Model highlight | `ParryingdaggerMH.png` |
| `Throwing` / `T` | Thrown in air | `AK47Throwing.png` / `CrossbowT.png` |
| `DReload` | Drawn during reload | `AK47DReload.png` |
| `DDrawBack` | Drawn during drawback | `CrossbowDDrawBack.png` |
| `DPump` | Drawn during pump action | `WinchesterDPump.png` |
| `DF` | Drawn while firing | `MinigunDF.png` |
| `DBlink` | Drawn blinking state | `SledgehammerDBlink.png` |
| `Debris1` | Break debris piece | `BladeDebris1.png` |

**CacheDrawnTextures**: When you call `CacheDrawnTextures(["Reload"])`, the system looks for `{DrawnTextureName}{Postfix}.png`. For `SetDrawnTexture("BladeD")` + `CacheDrawnTextures(["Reload"])` → looks for `BladeDReload.png`.

---

## 14. Sound System

### Using Existing Sounds

SFD comes with many built-in sounds. Common weapon sounds you can reuse:
- Fire: `"Magnum"`, `"Revolver"`, `"SawedOff"`, `"TommyGun"`, `"Carbine"`, `"Sniper"`, `"Shotgun"`
- Draw: `"MagnumDraw"`, `"PistolDraw"`, `"SniperDraw"`, `"ShotgunDraw"`, `"TommyGunDraw"`, `"SawedOffDraw"`, `"GLauncherDraw"`, `"MeleeDraw"`, `"KatanaDraw"`, `"MeleeDrawMetal"`, `"GrenadeDraw"`
- Reload: `"MagnumReload"`, `"PistolReload"`, `"SniperReload"`, `"TommyGunReload"`, `"ShotgunReload"`, `"MagnumReloadStart"`, `"MagnumReloadEnd"`, `"SawedOffReload"`, `"GLauncherReload"`
- Melee: `"MeleeSwing"`, `"MeleeSlash"`, `"MeleeHitSharp"`, `"MeleeHitBlunt"`, `"MeleeBlock"`
- Impact: `"ImpactMetal"`, `"ImpactDefault"`, `"ImpactWood"`, `"ImpactFlesh"`, `"ImpactGlass"`, `"WeaponBounce"`, `"GrenadeBounce"`
- Other: `"Draw1"` (universal holster click), `"GrenadeThrow"`, `"GrenadeSafe"`, `"GetHealthSmall"`, `"GetSlomo"`, `"BowShoot"`, `"BowDrawback"`, `"Explosion"`
- Destroy: `"DestroyMetal"`, `"DestroySmall"`
- Bolt action: `"SniperBoltAction1"`, `"SniperBoltAction2"`, `"ShotgunPump1"`, `"ShotgunPump2"`
- Empty: `"OutOfAmmoLight"`, `"OutOfAmmoHeavy"`

### Adding New Sounds

1. Place `.wav` files in `Content/Data/Sounds/Weapons/` (or any subfolder of `Content/Data/Sounds/`)

2. Register in `Content/Data/Sounds/Sounds.sfds`:
   ```
   MySoundName 0,5 Weapons\MySound
   ```
   Format: `SoundID Pitch SoundFilePath` (path relative to `Content/Data/Sounds/`, no extension)
   
   - Pitch is comma-separated decimal (European format): `0,5` = 0.5
   - Multiple wav files can be listed (random selection): `MySoundName 0,5 Weapons\MySound1 Weapons\MySound2`

3. Use in code: `SoundHandler.PlaySound("MySoundName", player.Position, player.GameWorld);`

---

## 15. Common Pitfalls

1. **Array size not increased**: `WeaponDatabase.m_weapons` must be large enough for your weapon ID index. If your ID is 110, the array needs at least 111 elements. Same for `ProjectileDatabase.projectiles`.

2. **Tile name mismatch**: The tile name in `weapons.sfdx` (e.g., `WpnDerringer`) must **exactly** match the `ModelID` in your weapon properties (case-insensitive for matching, but the tile definition is case-sensitive for the `Tile(...)` declaration).

3. **Missing `Copy()` method**: Every weapon and projectile **must** implement `Copy()`. The game clones weapons frequently for multiplayer sync.

4. **Missing textures**: If a texture referenced by `SetModelTexture()` etc. doesn't exist as a PNG in the Images folder, the game will crash on load.

5. **Projectile ID mismatch**: The `ProjectileID` in your weapon properties must match the index in `ProjectileDatabase.projectiles[]` where you registered your projectile.

6. **Not adding to `CustomWeaponItem` enum**: While this won't crash the game, it makes the weapon inaccessible from code that uses the enum.

7. **Thrown weapons need explicit ObjectsHandler registration**: Unlike regular weapon pickups (which auto-map), thrown objects (the in-flight version) need a manual `case` in `ObjectsHandler.LoadObjects()`.

8. **Sound not found**: If you reference a sound ID that isn't registered in `Sounds.sfds` or built into SFD, the sound will silently fail. Check the ID string exactly.

9. **Debris tiles**: If `BreakDebris` references tile IDs that don't exist in `weapons.sfdx`, the game will crash when the weapon breaks.

10. **weapons.sfdx placement**: Weapon tiles should be placed between the `editorEnabled = true` and `editorEnabled = false` default tile blocks, so they appear in the map editor if desired.

---

## 16. Full Checklist

Use this checklist when adding a new weapon:

### Required for ALL weapon types
- [ ] Choose a unique weapon ID (check `Database.cs` for the next available)
- [ ] Create the weapon class in the appropriate `SFR/Weapons/` subfolder
- [ ] Implement the `Copy()` method
- [ ] Create texture PNGs in `Content/Data/Images/Weapons/{Category}/`
- [ ] Define a tile in `Content/Data/Weapons/weapons.sfdx`
- [ ] Register in `SFR/Weapons/Database.cs`:
  - [ ] Increase `WeaponDatabase.m_weapons` array size if needed
  - [ ] Add `new WeaponItem(...)` to the `Weapons` list
  - [ ] Add spawn chance in `SpawnChance()` method (or comment out)
  - [ ] Add to `CustomWeaponItem` enum
- [ ] Add `using` directive in `Database.cs` if new namespace

### Additional for Ranged weapons
- [ ] Set `ProjectileID` to an existing projectile OR create a custom one
- [ ] If custom projectile: create class in `SFR/Projectiles/`, register in `SFR/Projectiles/Database.cs`
- [ ] If custom projectile textures: add PNGs to `Content/Data/Images/Weapons/Projectiles/`

### Additional for Thrown weapons
- [ ] Create thrown object class in `SFR/Objects/`
- [ ] Define "Thrown" tile in `weapons.sfdx` (e.g., `WpnMyWeaponThrown`)
- [ ] Register in `ObjectsHandler.cs` switch statement

### Additional for Melee weapons with custom debris
- [ ] Create debris tile definitions in `weapons.sfdx`
- [ ] Create debris texture PNGs

### Optional enhancements
- [ ] Implement `IExtendedWeapon` for per-frame updates or custom hit callbacks
- [ ] Implement `ISharpMelee` for decapitation capability
- [ ] Implement `IExtendedProjectile` for custom projectile-object interactions
- [ ] Add new sounds: `.wav` file + entry in `Sounds.sfds`
- [ ] Cache extra drawn texture variants with `CacheDrawnTextures(["Reload", ...])`
- [ ] Override `GetDrawnTexture()` for animation-dependent texture swaps
- [ ] Override `OnSetPostFireAction()` for bolt-action/pump-action behavior
- [ ] Set `LazerUpgrade = 1` for weapons with built-in laser sights

---

## Quick-Start Template Summary

| Want to add... | Base class | Subfolder | `WeaponItemType` | Key files to modify |
|----------------|-----------|-----------|-------------------|---------------------|
| Handgun | `RWeapon` | `Weapons/Handguns/` | `Handgun` | Database.cs, weapons.sfdx, textures |
| Rifle | `RWeapon` | `Weapons/Rifles/` | `Rifle` | Database.cs, weapons.sfdx, textures |
| Melee | `MWeapon` | `Weapons/Melee/` | `Melee` | Database.cs, weapons.sfdx, textures |
| Makeshift | `MWeapon` | `Weapons/Makeshift/` | `Melee` | Database.cs, weapons.sfdx, textures |
| Thrown | `TWeapon` | `Weapons/Thrown/` | `Thrown` | Database.cs, weapons.sfdx, ObjectsHandler.cs, textures, + Object class |
| Powerup | `PItem` | `Weapons/Others/` | `Powerup` | Database.cs, weapons.sfdx, textures |
| Instant Pickup | `HItem` | `Weapons/Others/` | `InstantPickup` | Database.cs, weapons.sfdx, textures |
| Custom Projectile | `Projectile` | `Projectiles/` | — | Projectiles/Database.cs, textures |
