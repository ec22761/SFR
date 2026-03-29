using System.Collections.Generic;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Effects;
using SFD.Objects;
using SFD.Sounds;
using SFD.Weapons;
using SFDGameScriptInterface;
using SFR.Helper;
using SFR.Misc;
using Player = SFD.Player;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace SFR.Weapons.Rifles;

/// <summary>
/// Junk Cannon — fires random pieces of debris at high velocity. Each shot spawns
/// a random small object (metal shards, glass, shell casings, wood splinters, etc.)
/// and launches it. There is a small chance the shot is a red explosive canister
/// that detonates on impact.
/// </summary>
internal sealed class JunkCannon : RWeapon, IExtendedWeapon
{
    private const float LaunchSpeed = 18f;
    private const float LaunchDamagePlayer = 14f;
    private const float LaunchDamageObject = 20f;
    private const float JunkTrackDuration = 2000f;
    private const float JunkHitRadius = 8f;
    private const float JunkMinDamageSpeed = 3f;
    private const float ExplosiveChance = 0.1f;
    private const float ExplosionDamage = 25f;

    private static readonly string[] JunkDebris =
    [
        "MetalDebris00A", "MetalDebris00B", "MetalDebris00C", "MetalDebris00D", "MetalDebris00E",
        "ItemDebrisDark00", "ItemDebrisDark01", "ItemDebrisShiny00", "ItemDebrisShiny01",
        "GlassShard00A",
        "WoodDebris00A", "WoodDebris00B", "WoodDebris00C",
        "ShellSmall", "ShellBig", "ShellShotgun",
        "KnifeDebris1", "BladeDebris1",
        "CrumpledPaper00"
    ];

    private readonly List<LaunchedJunk> _trackedJunk = [];

    internal JunkCannon()
    {
        RWeaponProperties weaponProperties = new(116, "Junk_Cannon", "WpnJunkCannon", false, WeaponCategory.Primary)
        {
            MaxMagsInWeapon = 1,
            MaxRoundsInMag = 20,
            MaxCarriedSpareMags = 0,
            StartMags = 1,
            CooldownBeforePostAction = 0,
            CooldownAfterPostAction = 150,
            ExtraAutomaticCooldown = 0,
            ShellID = string.Empty,
            AccuracyDeflection = 0.15f,
            ProjectileID = -1, // No traditional projectile — we spawn debris objects manually
            MuzzlePosition = new Vector2(13f, -2.5f),
            CursorAimOffset = new Vector2(0f, 2.5f),
            LazerPosition = new Vector2(12f, -1.5f),
            MuzzleEffectTextureID = "MuzzleFlashS",
            BlastSoundID = "",
            DrawSoundID = "CarbineDraw",
            GrabAmmoSoundID = "CarbineReload",
            OutOfAmmoSoundID = "OutOfAmmoHeavy",
            AimStartSoundID = "PistolAim",
            AI_DamageOutput = DamageOutputType.High,
            AI_GravityArcingEffect = 0.5f,
            AI_EffectiveRange = 175,
            BreakDebris =
            [
                "ItemDebrisDark00",
                "ItemDebrisDark01",
                "ItemDebrisShiny00"
            ],
            CanRefilAtAmmoStashes = false,
            SpecialAmmoBulletsRefill = 0,
            VisualText = "Junk Cannon"
        };

        RWeaponVisuals weaponVisuals = new()
        {
            AnimIdleUpper = "UpperIdleRifle",
            AnimCrouchUpper = "UpperCrouchRifle",
            AnimJumpKickUpper = "UpperJumpKickRifle",
            AnimJumpUpper = "UpperJumpRifle",
            AnimJumpUpperFalling = "UpperJumpFallingRifle",
            AnimKickUpper = "UpperKickRifle",
            AnimStaggerUpper = "UpperStaggerHandgun",
            AnimRunUpper = "UpperRunRifle",
            AnimWalkUpper = "UpperWalkRifle",
            AnimUpperHipfire = "UpperHipfireRifle",
            AnimFireArmLength = 2f,
            AnimDraw = "UpperDrawRifle",
            AnimManualAim = "ManualAimRifle",
            AnimManualAimStart = "ManualAimRifleStart",
            AnimReloadUpper = "UpperReload",
            AnimFullLand = "FullLandHandgun",
            AnimToggleThrowingMode = "UpperToggleThrowing"
        };

        weaponVisuals.SetModelTexture("JunkCannonM");
        weaponVisuals.SetDrawnTexture("JunkCannonD");
        weaponVisuals.SetSheathedTexture("JunkCannonS");
        weaponVisuals.SetThrowingTexture("JunkCannonThrowing");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

        CacheDrawnTextures(["Reload"]);
    }

    private JunkCannon(RWeaponProperties weaponProperties, RWeaponVisuals weaponVisuals) => SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

    public override RWeapon Copy()
    {
        JunkCannon wpn = new(Properties, Visuals);
        wpn.CopyStatsFrom(this);
        return wpn;
    }

    // ── IExtendedWeapon ──────────────────────────────────────────

    public void Update(Player player, float ms, float realMs)
    {
        if (player.GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

        for (int i = _trackedJunk.Count - 1; i >= 0; i--)
        {
            LaunchedJunk junk = _trackedJunk[i];
            junk.TimeRemaining -= ms;

            if (junk.TimeRemaining <= 0f || junk.Object.RemovalInitiated || junk.Object.Body == null)
            {
                _trackedJunk.RemoveAt(i);
                continue;
            }

            float speed = junk.Object.Body.GetLinearVelocity().Length();
            Vector2 junkWorldPos = Converter.ConvertBox2DToWorld(junk.Object.Body.GetPosition());

            // Explosive canister detonates when it stops (hit a surface)
            if (junk.IsExplosive && speed < JunkMinDamageSpeed)
            {
                _ = player.GameWorld.TriggerExplosion(junkWorldPos, ExplosionDamage, true);
                _trackedJunk.RemoveAt(i);
                continue;
            }

            if (speed < JunkMinDamageSpeed)
            {
                _trackedJunk.RemoveAt(i);
                continue;
            }

            AABB.Create(out AABB hitArea, junkWorldPos, junkWorldPos, JunkHitRadius);

            bool hitSomething = false;
            foreach (ObjectData obj in player.GameWorld.GetObjectDataByArea(hitArea, false, PhysicsLayer.Active))
            {
                if (obj.InternalData is Player hitPlayer && hitPlayer != player && !hitPlayer.IsDead && !hitPlayer.IsRemoved)
                {
                    if (junk.IsExplosive)
                    {
                        _ = player.GameWorld.TriggerExplosion(junkWorldPos, ExplosionDamage, true);
                    }
                    else
                    {
                        hitPlayer.TakeMiscDamage(LaunchDamagePlayer, sourceID: player.ObjectID);

                        Vector2 knockDir = junk.Object.Body.GetLinearVelocity();
                        knockDir.Normalize();
                        hitPlayer.SimulateFallWithSpeed(knockDir * 6f + new Vector2(0f, 3f));

                        SoundHandler.PlaySound("ImpactDefault", junkWorldPos, player.GameWorld);
                        EffectHandler.PlayEffect("BulletHit", junkWorldPos, player.GameWorld);
                    }

                    hitSomething = true;
                    break;
                }
                else if (obj.InternalData is not Player && obj != junk.Object && obj.Destructable)
                {
                    if (junk.IsExplosive)
                    {
                        _ = player.GameWorld.TriggerExplosion(junkWorldPos, ExplosionDamage, true);
                        hitSomething = true;
                        break;
                    }

                    obj.DealScriptDamage((int)LaunchDamageObject, player.ObjectID);
                    hitSomething = true;
                }
            }

            if (hitSomething)
            {
                _trackedJunk.RemoveAt(i);
            }
            else
            {
                _trackedJunk[i] = junk;
            }
        }
    }

    public void GetDealtDamage(Player player, float damage) { }
    public void OnHit(Player player, Player target) { }
    public void OnHitObject(Player player, PlayerHitEventArgs args, ObjectData obj) { }
    public void DrawExtra(SpriteBatch spritebatch, Player player, float ms) { }

    // ── Core firing mechanic ─────────────────────────────────────

    public override void BeforeCreateProjectile(BeforeCreateProjectileArgs args)
    {
        args.Handled = true;

        Player player = args.Player;

        if (player.GameOwner == GameOwnerEnum.Client)
        {
            args.FireResult = true;
            return;
        }

        Vector2 launchDir = args.Direction;
        if (launchDir == Vector2.Zero)
        {
            launchDir = new Vector2(player.LastDirectionX, 0);
        }

        launchDir.Normalize();

        // Pick random junk — small chance for an explosive canister
        bool isExplosive = Globals.Random.NextFloat() < ExplosiveChance;
        string tileName = isExplosive ? "Gascan00" : JunkDebris[Globals.Random.Next(JunkDebris.Length)];

        Vector2 muzzleWorld = args.WorldPosition;

        // Spawn the debris object at the muzzle and launch it
        ObjectData junkData = player.GameWorld.IDCounter.NextObjectData(tileName);
        _ = player.GameWorld.CreateTile(new SpawnObjectInformation(
            junkData, muzzleWorld, 0f, (short)player.LastDirectionX,
            launchDir * LaunchSpeed, Globals.Random.NextFloat(-15f, 15f)));

        _trackedJunk.Add(new LaunchedJunk
        {
            Object = junkData,
            IsExplosive = isExplosive,
            TimeRemaining = JunkTrackDuration
        });

        // Effects
        SoundHandler.PlaySound("GLFire", player.Position, player.GameWorld);
        SoundHandler.PlaySound("ImpactMetal", player.Position, player.GameWorld);
        EffectHandler.PlayEffect("S_P", muzzleWorld, player.GameWorld);
        EffectHandler.PlayEffect("CAM_S", Vector2.Zero, player.GameWorld, 0.5f, 100f, false);

        args.FireResult = true;
    }

    // ── Standard weapon callbacks ────────────────────────────────

    public override void OnRecoilEvent(Player player)
    {
        // Sound handled in BeforeCreateProjectile
    }

    public override void OnReloadAnimationEvent(Player player, AnimationEvent animEvent, SubAnimationPlayer subAnim)
    {
        if (player.GameOwner != GameOwnerEnum.Server)
        {
            if (animEvent == AnimationEvent.EnterFrame && subAnim.GetCurrentFrameIndex() == 1)
            {
                SpawnMagazine(player, "MagSmall", new Vector2(-6f, -5f));
                SoundHandler.PlaySound("MagnumReloadEnd", player.Position, player.GameWorld);
            }

            if (animEvent == AnimationEvent.EnterFrame && subAnim.GetCurrentFrameIndex() == 4)
            {
                SoundHandler.PlaySound("CarbineReload", player.Position, player.GameWorld);
            }
        }
    }

    public override void OnSubAnimationEvent(Player player, AnimationEvent animationEvent, AnimationData animationData, int currentFrameIndex)
    {
        if (player.GameOwner != GameOwnerEnum.Server && animationEvent == AnimationEvent.EnterFrame && animationData.Name == "UpperDrawRifle")
        {
            switch (currentFrameIndex)
            {
                case 1:
                    SoundHandler.PlaySound("Draw1", player.GameWorld);
                    break;
                case 6:
                    SoundHandler.PlaySound("CarbineDraw", player.GameWorld);
                    break;
            }
        }
    }

    public override void OnThrowWeaponItem(Player player, ObjectWeaponItem thrownWeaponItem)
    {
        thrownWeaponItem.Body.SetAngularVelocity(thrownWeaponItem.Body.GetAngularVelocity() * 0.8f);
        Vector2 linearVelocity = thrownWeaponItem.Body.GetLinearVelocity() * 0.85f;
        thrownWeaponItem.Body.SetLinearVelocity(linearVelocity);
        base.OnThrowWeaponItem(player, thrownWeaponItem);
    }

    public override Texture2D GetDrawnTexture(ref GetDrawnTextureArgs args)
    {
        if (args.SubAnimation is "UpperReload" && args.SubFrame is >= 1 and <= 5)
        {
            args.Postfix = "Reload";
        }

        return base.GetDrawnTexture(ref args);
    }

    // ── Inner types ──────────────────────────────────────────────

    private struct LaunchedJunk
    {
        internal ObjectData Object;
        internal bool IsExplosive;
        internal float TimeRemaining;
    }
}
