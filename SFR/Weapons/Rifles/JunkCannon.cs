using System;
using System.Collections.Generic;
using System.Linq;
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
using Color = Microsoft.Xna.Framework.Color;
using Player = SFD.Player;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace SFR.Weapons.Rifles;

/// <summary>
/// Junk Cannon — a rifle that vacuums up nearby physics objects from behind the player
/// and launches them at devastating force in the aimed direction. Any dropped weapon,
/// debris, or dynamic pickup becomes ammunition. If no object is found, it dry-fires.
/// </summary>
internal sealed class JunkCannon : RWeapon, IExtendedWeapon
{
    /// <summary>Maximum distance behind the player to search for objects to vacuum (pixels).</summary>
    private const float VacuumRadius = 40f;

    /// <summary>Speed at which the vacuumed object is launched.</summary>
    private const float LaunchSpeed = 18f;

    /// <summary>Damage dealt to players hit by a launched object.</summary>
    private const float LaunchDamagePlayer = 18f;

    /// <summary>Damage dealt to objects hit by a launched object.</summary>
    private const float LaunchDamageObject = 30f;

    /// <summary>How long (ms) we track a launched object before giving up.</summary>
    private const float JunkTrackDuration = 2000f;

    /// <summary>Radius around launched junk to search for player hits (pixels).</summary>
    private const float JunkHitRadius = 8f;

    /// <summary>Minimum velocity (Box2D units) for the junk to still deal damage.</summary>
    private const float JunkMinDamageSpeed = 3f;

    /// <summary>Maximum AABB dimension (Box2D units) for an object to be vacuumable.</summary>
    private const float MaxVacuumObjectSize = 0.6f;

    /// <summary>Maximum mass (Box2D units) for an object to be vacuumable.</summary>
    private const float MaxVacuumObjectMass = 1.6f;

    /// <summary>Tracks remaining cooldown for the vacuum sound effect.</summary>
    private float _vacuumSoundCooldown;

    /// <summary>Timestamp of the last fire for vacuum effect timing.</summary>
    private float _lastFireTime;

    /// <summary>Active launched junk being tracked for damage.</summary>
    private readonly List<LaunchedJunk> _trackedJunk = new();

    /// <summary>Particles for the vacuum suck-in visual effect.</summary>
    private readonly List<VacuumParticle> _vacuumParticles = new();

    /// <summary>Timer for spawning new vacuum particles.</summary>
    private float _particleSpawnTimer;

    /// <summary>Cooldown for spawning engine smoke effects.</summary>
    private float _smokeEffectTimer;

    /// <summary>Shared 1x1 white pixel texture for drawing particles.</summary>
    private static Texture2D _pixelTexture;

    internal JunkCannon()
    {
        RWeaponProperties weaponProperties = new(116, "Junk_Cannon", "WpnJunkCannon", false, WeaponCategory.Primary)
        {
            MaxMagsInWeapon = 1,
            MaxRoundsInMag = 1,
            MaxCarriedSpareMags = 0,
            StartMags = 1,
            CooldownBeforePostAction = 400,
            CooldownAfterPostAction = 300,
            ExtraAutomaticCooldown = 0,
            ShellID = string.Empty,
            AccuracyDeflection = 0.12f,
            ProjectileID = -1, // No traditional projectile — we handle firing manually
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

        // Reuse Rivet Gun textures with renamed references
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
        if (_vacuumSoundCooldown > 0f)
        {
            _vacuumSoundCooldown -= ms;
        }

        // ── Track launched junk and deal damage on impact ──
        if (player.GameOwner != GameOwnerEnum.Client)
        {
            for (int i = _trackedJunk.Count - 1; i >= 0; i--)
            {
                LaunchedJunk junk = _trackedJunk[i];
                junk.TimeRemaining -= ms;

                // Remove if expired or object gone
                if (junk.TimeRemaining <= 0f || junk.Object.RemovalInitiated || junk.Object.Body == null)
                {
                    _trackedJunk.RemoveAt(i);
                    continue;
                }

                // Check if the junk is still moving fast enough to damage
                float speed = junk.Object.Body.GetLinearVelocity().Length();
                if (speed < JunkMinDamageSpeed)
                {
                    _trackedJunk.RemoveAt(i);
                    continue;
                }

                // Search for players near the junk object
                Vector2 junkWorldPos = Converter.ConvertBox2DToWorld(junk.Object.Body.GetPosition());
                AABB.Create(out AABB hitArea, junkWorldPos, junkWorldPos, JunkHitRadius);

                bool hitSomething = false;
                foreach (ObjectData obj in player.GameWorld.GetObjectDataByArea(hitArea, false, PhysicsLayer.Active))
                {
                    if (obj.InternalData is Player hitPlayer && hitPlayer != player && !hitPlayer.IsDead && !hitPlayer.IsRemoved)
                    {
                        hitPlayer.TakeMiscDamage(LaunchDamagePlayer, sourceID: player.ObjectID);

                        // Knockback the hit player
                        Vector2 knockDir = junk.Object.Body.GetLinearVelocity();
                        knockDir.Normalize();
                        hitPlayer.SimulateFallWithSpeed(knockDir * 6f + new Vector2(0f, 3f));

                        // Impact effects
                        SoundHandler.PlaySound("ImpactDefault", junkWorldPos, player.GameWorld);
                        EffectHandler.PlayEffect("BulletHit", junkWorldPos, player.GameWorld);

                        hitSomething = true;
                    }
                    else if (obj.InternalData is not Player && obj != junk.Object && obj.Destructable)
                    {
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

        // ── Update vacuum particles & smoke (client-side visual) ──
        if (player.GameOwner != GameOwnerEnum.Server)
        {
            float now = player.GameWorld.ElapsedTotalGameTime;
            bool recentlyFired = now - _lastFireTime < 600f;

            // Update existing particles
            for (int i = _vacuumParticles.Count - 1; i >= 0; i--)
            {
                VacuumParticle p = _vacuumParticles[i];
                p.Lifetime -= ms;
                if (p.Lifetime <= 0f)
                {
                    _vacuumParticles.RemoveAt(i);
                    continue;
                }

                // Move particle toward the target (weapon back)
                Vector2 dir = p.Target - p.Position;
                float dist = dir.Length();
                if (dist > 1f)
                {
                    dir.Normalize();
                    float speed = 0.15f + (1f - p.Lifetime / p.MaxLifetime) * 0.35f;
                    p.Position = Vector2.Lerp(p.Position, p.Target, speed);
                }

                _vacuumParticles[i] = p;
            }

            // Spawn new particles & effects when firing
            if (recentlyFired)
            {
                // Smoke goes in the exact opposite direction of aim
                Vector2 aimDir = player.AimVector();
                if (aimDir.LengthSquared() < 0.01f)
                {
                    aimDir = new Vector2(player.LastDirectionX, 0);
                }
                aimDir.Normalize();
                Vector2 backDir = -aimDir;

                float weaponY = player.Crouching ? 8f : 10f;
                Vector2 weaponCenter = player.Position + new Vector2(0f, weaponY);
                Vector2 backOfWeapon = weaponCenter + backDir * 4f;

                // Spawn subtle smoke in the opposite aim direction
                _smokeEffectTimer -= ms;
                if (_smokeEffectTimer <= 0f)
                {
                    _smokeEffectTimer = 150f;
                    Vector2 smokePos = weaponCenter + backDir * 4f;
                    EffectHandler.PlayEffect("TR_S", smokePos, player.GameWorld);
                }

                // Spawn custom particles along the back-aim direction
                _particleSpawnTimer -= ms;
                if (_particleSpawnTimer <= 0f)
                {
                    _particleSpawnTimer = 40f;

                    // Perpendicular vector for spread
                    Vector2 perp = new(-backDir.Y, backDir.X);

                    for (int j = 0; j < 2; j++)
                    {
                        float spawnDist = Globals.Random.NextFloat(8f, 25f);
                        float perpSpread = Globals.Random.NextFloat(-6f, 6f);
                        Vector2 spawnPos = weaponCenter + backDir * spawnDist + perp * perpSpread;

                        float lifetime = Globals.Random.NextFloat(150f, 300f);

                        // Very subtle, transparent smoke wisps
                        Color color = Globals.Random.Next(4) switch
                        {
                            0 => new Color(120, 110, 100, 60),
                            1 => new Color(80, 80, 80, 50),
                            2 => new Color(160, 140, 110, 45),
                            _ => new Color(140, 140, 150, 55)
                        };

                        _vacuumParticles.Add(new VacuumParticle
                        {
                            Position = spawnPos,
                            Target = backOfWeapon,
                            Lifetime = lifetime,
                            MaxLifetime = lifetime,
                            Color = color,
                            Size = Globals.Random.NextFloat(1f, 2f)
                        });
                    }
                }
            }
        }
    }

    public void GetDealtDamage(Player player, float damage) { }
    public void OnHit(Player player, Player target) { }
    public void OnHitObject(Player player, PlayerHitEventArgs args, ObjectData obj) { }

    public void DrawExtra(SpriteBatch spritebatch, Player player, float ms)
    {
        if (player.GameOwner == GameOwnerEnum.Server || _vacuumParticles.Count == 0)
        {
            return;
        }

        EnsurePixelTexture(spritebatch);

        foreach (VacuumParticle p in _vacuumParticles)
        {
            float alpha = Math.Min(1f, p.Lifetime / p.MaxLifetime * 2f);
            Color drawColor = p.Color * alpha;

            Vector2 screenPos = WorldToScreen(p.Position);
            float size = p.Size * Camera.ZoomUpscaled;

            spritebatch.Draw(_pixelTexture, screenPos, null, drawColor, 0f,
                new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
        }
    }

    // ── Core firing mechanic ─────────────────────────────────────

    /// <summary>
    /// Intercepts the fire event. Instead of spawning a projectile, we find a nearby
    /// dynamic physics object behind the player, teleport it to the muzzle, and launch
    /// it at high speed in the aimed direction.
    /// </summary>
    public override void BeforeCreateProjectile(BeforeCreateProjectileArgs args)
    {
        args.Handled = true;

        Player player = args.Player;

        if (player.GameOwner == GameOwnerEnum.Client)
        {
            args.FireResult = true;
            return;
        }

        // Search area behind the player for a suckable object
        ObjectData target = FindVacuumTarget(player);

        if (target == null)
        {
            // No junk to vacuum — dry fire
            SoundHandler.PlaySound("OutOfAmmoHeavy", player.Position, player.GameWorld);
            args.FireResult = false;
            return;
        }

        // Calculate launch direction from aim
        Vector2 launchDir = args.Direction;
        if (launchDir == Vector2.Zero)
        {
            launchDir = new Vector2(player.LastDirectionX, 0);
        }

        launchDir.Normalize();

        // Teleport the object to the muzzle position and launch it
        Vector2 muzzleWorld = args.WorldPosition;
        target.Body.SetTransform(Converter.WorldToBox2D(muzzleWorld), target.Body.GetAngle());
        target.Body.SetLinearVelocity(launchDir * LaunchSpeed);
        target.Body.SetAngularVelocity(Globals.Random.NextFloat(-15f, 15f));

        // Track for damage
        _trackedJunk.Add(new LaunchedJunk
        {
            Object = target,
            TimeRemaining = JunkTrackDuration
        });

        _lastFireTime = player.GameWorld.ElapsedTotalGameTime;

        // Effects
        SoundHandler.PlaySound("GLFire", player.Position, player.GameWorld);
        SoundHandler.PlaySound("ImpactMetal", player.Position, player.GameWorld);
        EffectHandler.PlayEffect("S_P", muzzleWorld, player.GameWorld);
        EffectHandler.PlayEffect("CAM_S", Vector2.Zero, player.GameWorld, 0.5f, 100f, false);

        args.FireResult = true;
    }

    /// <summary>
    /// Finds the best dynamic object to vacuum up. Searches in a cone/area behind the player.
    /// Prioritizes the closest valid object.
    /// </summary>
    private static ObjectData FindVacuumTarget(Player player)
    {
        // The "behind" direction is opposite to the player's facing
        int behindDir = -player.LastDirectionX;
        Vector2 playerPos = player.Position;

        // Create a search area centered slightly behind the player
        Vector2 searchCenter = playerPos + new Vector2(behindDir * VacuumRadius * 0.5f, 0f);
        AABB.Create(out AABB area, searchCenter, searchCenter, VacuumRadius);

        ObjectData bestTarget = null;
        float bestDistance = float.MaxValue;

        foreach (ObjectData obj in player.GameWorld.GetObjectDataByArea(area, false, PhysicsLayer.Active))
        {
            // Skip players
            if (obj.InternalData is Player)
            {
                continue;
            }

            // Skip removed or static objects
            if (obj.RemovalInitiated || obj.IsStatic)
            {
                continue;
            }

            // Must have a physics body we can move
            if (obj.Body == null)
            {
                continue;
            }

            // Skip objects that are too heavy
            if (obj.Body.GetMass() > MaxVacuumObjectMass)
            {
                continue;
            }

            // Skip objects that are too physically large (boxes, barrels, etc.)
            // Compute the union AABB across ALL fixtures so multi-fixture bodies
            // (weapon debris with barrel+grip+stock, etc.) are measured correctly.
            Fixture fixture = obj.Body.GetFixtureList();
            if (fixture == null || fixture.GetShape() == null)
            {
                // Can't determine size — skip to be safe
                continue;
            }
            {
                obj.Body.GetTransform(out Transform xf);
                fixture.GetShape().ComputeAABB(out AABB combined, ref xf);

                for (Fixture f = fixture.GetNext(); f != null; f = f.GetNext())
                {
                    if (f.GetShape() == null)
                    {
                        continue;
                    }

                    f.GetShape().ComputeAABB(out AABB fAabb, ref xf);
                    combined.Combine(ref combined, ref fAabb);
                }

                float w = combined.upperBound.X - combined.lowerBound.X;
                float h = combined.upperBound.Y - combined.lowerBound.Y;
                if (Math.Max(w, h) > MaxVacuumObjectSize)
                {
                    continue;
                }
            }

            // Object must actually be behind the player (or at least close to center)
            Vector2 objWorldPos = Converter.ConvertBox2DToWorld(obj.Body.GetPosition());
            float relativeX = (objWorldPos.X - playerPos.X) * behindDir;

            // Object should be behind or roughly at the player, not far in front
            if (relativeX < -10f)
            {
                continue;
            }

            float distance = Vector2.Distance(objWorldPos, playerPos);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestTarget = obj;
            }
        }

        return bestTarget;
    }

    // ── Standard weapon callbacks ────────────────────────────────

    public override void ConsumeAmmoFromFire(Player player)
    {
        // Junk Cannon doesn't consume ammo — it uses physics objects as ammunition.
    }

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

    // ── Helpers ───────────────────────────────────────────────────

    private static void EnsurePixelTexture(SpriteBatch spriteBatch)
    {
        if (_pixelTexture is null)
        {
            _pixelTexture = new Texture2D(spriteBatch.GraphicsDevice, 1, 1);
            _pixelTexture.SetData(new[] { Color.White });
        }
    }

    private static Vector2 WorldToScreen(Vector2 worldPos)
    {
        Vector2 pos = new(Converter.WorldToBox2D(worldPos.X), Converter.WorldToBox2D(worldPos.Y));
        Camera.ConvertBox2DToScreen(ref pos, out pos);
        return pos;
    }

    // ── Inner types ──────────────────────────────────────────────

    private struct LaunchedJunk
    {
        internal ObjectData Object;
        internal float TimeRemaining;
    }

    private struct VacuumParticle
    {
        internal Vector2 Position;
        internal Vector2 Target;
        internal float Lifetime;
        internal float MaxLifetime;
        internal Color Color;
        internal float Size;
    }
}
