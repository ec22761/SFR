using System;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Effects;
using SFD.Materials;
using SFD.Objects;
using SFD.Sounds;
using SFD.Tiles;
using SFD.Weapons;
using SFR.Helper;
using SFR.Misc;

namespace SFR.Weapons.Rifles;

/// <summary>
///     Tesla Rifle — continuous electric beam weapon.
///     Hold fire to charge a wind-up beam that gradually thickens, then fires
///     a damaging raycast beam once fully charged. No visible bullets — the beam IS the weapon.
/// </summary>
internal sealed class TeslaRifle : RWeapon, IExtendedWeapon
{
    // --- Gameplay constants ---
    private const float BeamRange = 999f; // effectively unlimited — clamped to screen edge
    private const float BeamDamage = 2f;
    private const float BeamObjectDamage = 12f; // higher damage to objects so beam breaks lights, chains, barrels etc.
    private const float WindUpDuration = 800f; // ms to fully charge
    private const float WindUpDecay = 400f; // ms to lose charge when not firing
    private const float SoundCooldown = 180f;
    private const float FiringSoundCooldown = 120f; // ms between firing beam sounds
    private const float BeamLingerTime = 60f; // ms the full beam visual persists after each shot
    private const float BeamForwardOffset = 5f; // extra pixels to push the beam origin forward
    private const float AfterFireThreshold = 1000f; // ms before wind-down sound plays
    private const float WindUpBeamMaxWidth = 1.2f;
    private const float FiringBeamOuterWidth = 5f;
    private const float FiringBeamMidWidth = 2.5f;
    private const float FiringBeamCoreWidth = 1f;

    private const float SparkEffectInterval = 40f; // ms between spark draws at hit point
    private const int SparkCount = 3; // sparks per frame at hit point
    private const float HitEffectInterval = 150f; // ms between material smoke/dust effects at hit point

    private static Texture2D _pixelTexture;

    // --- Per-instance state ---
    private float _windUpProgress; // 0 → WindUpDuration
    private float _lastFireAttemptTime; // tracks when the player last held fire
    private float _lastSoundTime;
    private float _lastDamageBeamTime; // when we last fired the actual damage beam
    private Vector2 _beamStart; // world-space beam origin (muzzle)
    private Vector2 _beamEnd; // world-space beam end (hit or max range)
    private Vector2 _beamDir; // world-space beam direction (for stable rendering)
    private float _lastSparkEffectTime; // throttle spark effects at beam hit
    private float _lastHitEffectTime; // throttle material hit effects (smoke/dust)
    private bool _beamHitSurface; // whether the beam hit a solid surface (not a player/empty)

    // Cached game-computed muzzle position for smooth DrawExtra visuals.
    // Updated every BeforeCreateProjectile call (~50ms). In DrawExtra we
    // compensate for player movement since the cache to keep it smooth.
    private Vector2 _cachedMuzzlePos;
    private Vector2 _cachedPlayerPos;

    // Sound state — mirrors the minigun rev-up pattern.
    private string _soundState; // "windup", "firing", or empty
    private float _soundTimeStamp;
    private float _lastFiringSoundTime; // separate cooldown for firing beam sound
    private bool _playedWindDown;
    private bool _wasAiming; // tracks if player was in ManualAim last frame

    internal TeslaRifle()
    {
        RWeaponProperties weaponProperties = new(114, "Tesla_Rifle", "WpnTeslaRifle", false, WeaponCategory.Primary)
        {
            MaxMagsInWeapon = 1,
            MaxRoundsInMag = 150,
            MaxCarriedSpareMags = 0,
            StartMags = 1,
            CooldownBeforePostAction = 0,
            CooldownAfterPostAction = 0,
            ExtraAutomaticCooldown = 50,
            ProjectilesEachBlast = 1,
            ShellID = string.Empty,
            AccuracyDeflection = 0.01f,
            ProjectileID = 114,
            MuzzlePosition = new Vector2(12f, -1.5f),
            CursorAimOffset = new Vector2(0f, 1.5f),
            LazerPosition = new Vector2(10f, -3f),
            MuzzleEffectTextureID = string.Empty,
            BlastSoundID = string.Empty,
            DrawSoundID = "SniperDraw",
            GrabAmmoSoundID = string.Empty,
            OutOfAmmoSoundID = "OutOfAmmoHeavy",
            AimStartSoundID = "PistolAim",
            AI_DamageOutput = DamageOutputType.High,
            AI_EffectiveRange = 80,
            AI_MaxRange = 200,
            CanRefilAtAmmoStashes = false,
            BreakDebris =
            [
                "MetalDebris00A",
                "ItemDebrisShiny00",
                "ItemDebrisShiny01"
            ],
            SpecialAmmoBulletsRefill = 150,
            VisualText = "Tesla Rifle"
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

        weaponVisuals.SetModelTexture("TeslaRifleM");
        weaponVisuals.SetDrawnTexture("TeslaRifleD");
        weaponVisuals.SetSheathedTexture("TeslaRifleThrowing");
        weaponVisuals.SetThrowingTexture("TeslaRifleThrowing");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
    }

    private TeslaRifle(RWeaponProperties weaponProperties, RWeaponVisuals weaponVisuals) =>
        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

    private bool IsFullyCharged => _windUpProgress >= WindUpDuration;

    // ─── IExtendedWeapon ────────────────────────────────────────────────

    public void Update(Player player, float ms, float realMs)
    {
        float now = player.GameWorld.ElapsedTotalGameTime;

        // If the player just left ManualAim, immediately kill the beam and wind-up
        // to prevent the laser floating in its last aimed position.
        bool isAiming = player.CurrentAction == PlayerAction.ManualAim;
        if (_wasAiming && !isAiming)
        {
            _windUpProgress = 0f;
            _cachedMuzzlePos = Vector2.Zero;
            _lastDamageBeamTime = 0f;
        }
        _wasAiming = isAiming;

        // Decay wind-up when not actively firing.
        if (now - _lastFireAttemptTime > 120f)
        {
            float prev = _windUpProgress;
            _windUpProgress = Math.Max(0f, _windUpProgress - ms * (WindUpDuration / WindUpDecay));

            // Wind-down sound — play once when charge runs out (like TeslaDown).
            if (player.GameOwner != GameOwnerEnum.Client && prev > 0f && !_playedWindDown &&
                now > _soundTimeStamp && now - _lastFireAttemptTime > AfterFireThreshold / 2)
            {
                SoundHandler.PlaySound("TeslaDown", player.GameWorld);
                _soundTimeStamp = player.GameWorld.ElapsedTotalRealTime + 500f;
                _soundState = string.Empty;
                _playedWindDown = true;
            }
        }

        // Continue playing the spin sound only while still actively firing.
        // Stop the loop once the player hasn't fired for > 120ms.
        if (player.GameOwner != GameOwnerEnum.Client &&
            player.GameWorld.ElapsedTotalRealTime > _soundTimeStamp)
        {
            if (_soundState == "spin")
            {
                if (now - _lastFireAttemptTime <= 120f)
                {
                    SoundHandler.PlaySound("TeslaSpin", player.GameWorld);
                    _soundTimeStamp = player.GameWorld.ElapsedTotalRealTime + 200f;
                }
                else
                {
                    _soundState = string.Empty;
                }
            }
        }
    }

    public void GetDealtDamage(Player player, float damage) { }
    public void OnHit(Player player, Player target) { }
    public void OnHitObject(Player player, PlayerHitEventArgs args, ObjectData obj) { }

    public void DrawExtra(SpriteBatch spriteBatch, Player player, float ms)
    {
        if (player.GameOwner == GameOwnerEnum.Server) return;

        EnsurePixelTexture(spriteBatch);

        float now = player.GameWorld.ElapsedTotalGameTime;
        bool showingFiringBeam = now - _lastDamageBeamTime <= BeamLingerTime;

        // Compute current aim-aligned beam for the wind-up preview.
        GetAimBeamPositions(player, out Vector2 muzzleWorld, out Vector2 aimDir);
        float edgeDist = Camera.GetDistanceToEdge(muzzleWorld, aimDir);
        float range = edgeDist > 0 ? edgeDist + 16f : BeamRange;

        // Use GameWorld.RayCast to find the visual endpoint (same as laser sight).
        GameWorld.RayCastResult ray = player.GameWorld.RayCast(
            muzzleWorld, aimDir, 0f, range, LazerRayCastCollision, _ => true);

        bool hitSomething = ray.EndFixture is not null;
        Vector2 previewEnd = hitSomething
            ? ray.EndPosition
            : muzzleWorld + aimDir * range;

        if (showingFiringBeam)
        {
            // Render the firing beam identically to the wind-up preview — use the
            // live aim position/direction so it stays perfectly smooth. The actual
            // damage is handled invisibly in BeforeCreateProjectile.
            player.GameWorld.DrawLazer(spriteBatch, true, muzzleWorld, previewEnd, aimDir);

            // Blue glow overlay at full opacity.
            DrawBeamLine(spriteBatch, muzzleWorld, previewEnd, aimDir, new Color(30, 80, 200, 60), FiringBeamOuterWidth);
            DrawBeamLine(spriteBatch, muzzleWorld, previewEnd, aimDir, new Color(80, 180, 255, 120), FiringBeamMidWidth);
            // Almost-white core.
            DrawBeamLine(spriteBatch, muzzleWorld, previewEnd, aimDir, new Color(220, 240, 255, 240), FiringBeamCoreWidth);

            // Blue spark particles at beam hit point — tiny custom-drawn dots.
            if (hitSomething && now > _lastSparkEffectTime + SparkEffectInterval)
            {
                _lastSparkEffectTime = now;
                for (int i = 0; i < SparkCount; i++)
                {
                    Vector2 sparkWorld = previewEnd + new Vector2(
                        Globals.Random.NextFloat(-2f, 2f), Globals.Random.NextFloat(-2f, 2f));
                    Vector2 sparkScreen = WorldToScreen(sparkWorld);
                    float sparkSize = Globals.Random.NextFloat(0.5f, 1.5f) * Camera.ZoomUpscaled;
                    int sparkAlpha = Globals.Random.Next(150, 255);
                    Color sparkColor = new(160, 220, 255, sparkAlpha);
                    spriteBatch.Draw(_pixelTexture, sparkScreen, null, sparkColor, 0f,
                        new Vector2(0.5f, 0.5f), sparkSize, SpriteEffects.None, 0f);
                }
            }
        }
        else if (_windUpProgress > 0f)
        {
            // Wind-up preview beam — use engine DrawLazer for a thin smooth line,
            // then add a growing blue tint overlay.
            float t = Math.Min(_windUpProgress / WindUpDuration, 1f);

            // Show the engine laser at partial visibility via the blink flag.
            player.GameWorld.DrawLazer(spriteBatch, t > 0.3f, muzzleWorld, previewEnd, aimDir);

            // Blue glow grows with charge.
            float width = 0.3f + t * WindUpBeamMaxWidth;
            int alpha = (int)(20 + t * 80);
            Color windUpColor = new(80, 180, 255, alpha);
            DrawBeamLine(spriteBatch, muzzleWorld, previewEnd, aimDir, windUpColor, width);

            // White core fades in as charge builds.
            if (t > 0.5f)
            {
                int coreAlpha = (int)((t - 0.5f) * 2f * 200);
                DrawBeamLine(spriteBatch, muzzleWorld, previewEnd, aimDir, new Color(220, 240, 255, coreAlpha), 0.5f);
            }
        }
    }

    // ─── RWeapon overrides ──────────────────────────────────────────────

    public override RWeapon Copy()
    {
        TeslaRifle copy = new(Properties, Visuals);
        copy.CopyStatsFrom(this);
        return copy;
    }

    public override void BeforeCreateProjectile(BeforeCreateProjectileArgs args)
    {
        float now = args.Player.GameWorld.ElapsedTotalGameTime;
        _lastFireAttemptTime = now;

        // Cache the game-computed muzzle position & player position so DrawExtra
        // can produce a smooth, correctly-placed beam between physics ticks.
        _cachedMuzzlePos = args.WorldPosition;
        _cachedPlayerPos = args.Player.Position;

        // Always suppress the physical projectile — the beam IS the weapon.
        args.Handled = true;
        _playedWindDown = false;

        // Build up wind-up charge. Each fire tick is ~50ms (ExtraAutomaticCooldown).
        float prevProgress = _windUpProgress;
        _windUpProgress = Math.Min(_windUpProgress + 50f, WindUpDuration);

        // Sound system — mirrors minigun rev-up pattern with deeper Tesla sounds.
        if (args.Player.GameOwner != GameOwnerEnum.Client)
        {
            if (IsFullyCharged)
            {
                // Firing beam: play laser sound on a cooldown.
                if (now > _lastSoundTime + SoundCooldown)
                {
                    SoundHandler.PlaySound("Lazer", args.Player.Position, args.Player.GameWorld);
                    _lastSoundTime = now;
                }
                // Deep electric zap while actively firing.
                if (now > _lastFiringSoundTime + FiringSoundCooldown)
                {
                    SoundHandler.PlaySound("TeslaBeamFire", args.Player.Position, args.Player.GameWorld);
                    _lastFiringSoundTime = now;
                }
                // Keep the spin sound looping while firing, layered with the laser.
                _soundState = "spin";
            }
            else if (prevProgress == 0f)
            {
                // First tick of windup: play charge-up start sound (deeper TeslaUp).
                SoundHandler.PlaySound("TeslaUp", args.Player.GameWorld);
                _soundTimeStamp = args.Player.GameWorld.ElapsedTotalRealTime + 500f;
                _soundState = "windup";
            }
            else if (_soundState == "windup" && args.Player.GameWorld.ElapsedTotalRealTime > _soundTimeStamp)
            {
                // Transition from windup to looping spin sound.
                _soundState = "spin";
            }
        }

        if (!IsFullyCharged)
        {
            // Not ready to fire yet — just wind up.
            args.FireResult = true;
            return;
        }

        // ── Fully charged: fire the damage beam via raycast ──
        args.FireResult = true;

        // Use the game-engine-computed muzzle position and direction for damage.
        Vector2 aimDir = args.Direction;
        if (aimDir.LengthSquared() > 0) aimDir.Normalize();
        Vector2 muzzleWorld = args.WorldPosition + aimDir * BeamForwardOffset;

        float edgeDist = Camera.GetDistanceToEdge(muzzleWorld, aimDir);
        float range = edgeDist > 0 ? edgeDist + 16f : BeamRange;

        _beamStart = muzzleWorld;
        _beamDir = aimDir;
        _beamHitSurface = false;

        if (args.Player.GameOwner != GameOwnerEnum.Client)
        {
            // Server/host: do the damage raycast.
            GameWorld.RayCastResult ray = args.Player.GameWorld.RayCast(
                muzzleWorld, aimDir, 0f, range, LazerRayCastCollision, _ => true);

            if (ray.EndFixture is not null)
            {
                _beamEnd = ray.EndPosition;
                ObjectData hitObj = ObjectData.Read(ray.EndFixture);

                if (hitObj.IsPlayer && hitObj.InternalData is Player hitPlayer &&
                    !hitPlayer.IsDead && !hitPlayer.IsRemoved)
                {
                    hitPlayer.TakeMiscDamage(BeamDamage, sourceID: args.Player.ObjectID);
                }
                else
                {
                    _beamHitSurface = true;
                    if (hitObj.Destructable)
                    {
                        hitObj.DealScriptDamage((int)BeamObjectDamage, args.Player.ObjectID);
                    }

                    // Play material-based smoke/dust effects at hit point (like bullets do).
                    if (now > _lastHitEffectTime + HitEffectInterval && hitObj.Tile?.Material != null)
                    {
                        Material mat = hitObj.Tile.Material;
                        EffectHandler.PlayEffect(mat.Hit.Projectile.HitEffect, ray.EndPosition, args.Player.GameWorld);
                        SoundHandler.PlaySound(mat.Hit.Projectile.HitSound, ray.EndPosition, args.Player.GameWorld);
                        _lastHitEffectTime = now;
                    }
                }
            }
            else
            {
                _beamEnd = muzzleWorld + aimDir * range;
            }
        }
        else
        {
            // Client: visual-only raycast for beam endpoint.
            GameWorld.RayCastResult ray = args.Player.GameWorld.RayCast(
                muzzleWorld, aimDir, 0f, range, LazerRayCastCollision, _ => true);
            _beamEnd = ray.EndFixture is not null ? ray.EndPosition : muzzleWorld + aimDir * range;
            _beamHitSurface = ray.EndFixture is not null;
        }

        _lastDamageBeamTime = now;

        // Consume ammo manually since we suppressed the projectile.
        base.ConsumeAmmoFromFire(args.Player);
    }

    public override void ConsumeAmmoFromFire(Player player)
    {
        // Suppress default ammo consumption — we handle it in BeforeCreateProjectile.
    }

    public override void OnRecoilEvent(Player player)
    {
        // No recoil for beam weapon.
    }

    public override void OnSubAnimationEvent(Player player, AnimationEvent animationEvent,
        AnimationData animationData, int currentFrameIndex)
    {
        if (player.GameOwner != GameOwnerEnum.Server && animationEvent == AnimationEvent.EnterFrame &&
            animationData.Name == "UpperDrawRifle")
        {
            switch (currentFrameIndex)
            {
                case 1:
                    SoundHandler.PlaySound("Draw1", player.GameWorld);
                    break;
                case 6:
                    SoundHandler.PlaySound("SniperDraw", player.GameWorld);
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

    // ─── Private helpers ────────────────────────────────────────────────

    /// <summary>
    ///     Compute the world-space muzzle position and aim direction.
    ///     Uses the cached game-engine position (from BeforeCreateProjectile) with
    ///     player-movement compensation so the beam tracks smoothly between physics
    ///     ticks, and a fresh aim direction every render frame.
    /// </summary>
    private void GetAimBeamPositions(Player player, out Vector2 muzzleWorld, out Vector2 aimDir)
    {
        // Fresh aim direction every call for smooth visual tracking.
        if (player.CurrentAction == PlayerAction.ManualAim)
        {
            aimDir = player.AimVector();
            if (aimDir.LengthSquared() > 0) aimDir.Normalize();
        }
        else
        {
            aimDir = new Vector2(player.LastDirectionX, 0f);
        }

        // Use game-computed muzzle position, compensated for player movement
        // since the last BeforeCreateProjectile call (keeps beam glued to weapon).
        if (_cachedMuzzlePos != Vector2.Zero)
        {
            muzzleWorld = _cachedMuzzlePos + (player.Position - _cachedPlayerPos) + aimDir * BeamForwardOffset;
        }
        else
        {
            // Fallback before the very first fire attempt.
            float heightOffset = player.Crouching ? 5f : 9f;
            Vector2 bodyPos = player.Position + new Vector2(0f, heightOffset);
            muzzleWorld = bodyPos + aimDir * (Properties.MuzzlePosition.X + BeamForwardOffset);
        }
    }

    /// <summary>
    ///     RayCast collision filter — same logic as the claymore laser: collide with
    ///     solid fixtures and players.
    /// </summary>
    private static bool LazerRayCastCollision(Fixture fixture)
    {
        if (fixture.IsCloud()) return false;
        ObjectData objectData = ObjectData.Read(fixture);
        fixture.GetFilterData(out Filter filter);
        return (filter.categoryBits & 15) > 0 || objectData.IsPlayer;
    }

    /// <summary>
    ///     Draw a straight beam line between two world-space points.
    ///     Uses the provided world-space direction for the angle so the beam
    ///     is perfectly straight and does not jitter from coordinate conversion.
    /// </summary>
    private static void DrawBeamLine(SpriteBatch spriteBatch, Vector2 worldStart, Vector2 worldEnd,
        Vector2 worldDir, Color color, float width)
    {
        Vector2 screenStart = WorldToScreen(worldStart);
        Vector2 screenEnd = WorldToScreen(worldEnd);

        // Use the world-space direction for the angle — this is stable and
        // doesn't wobble from sub-pixel screen-space delta changes.
        // Note: screen Y is flipped relative to world Y.
        float angle = (float)Math.Atan2(-worldDir.Y, worldDir.X);
        float length = Vector2.Distance(screenStart, screenEnd);

        spriteBatch.Draw(
            _pixelTexture,
            screenStart,
            null,
            color,
            angle,
            new Vector2(0f, 0.5f),
            new Vector2(length, width * Camera.ZoomUpscaled),
            SpriteEffects.None,
            0f
        );
    }

    private static Vector2 WorldToScreen(Vector2 worldPos)
    {
        Vector2 pos = new(Converter.WorldToBox2D(worldPos.X), Converter.WorldToBox2D(worldPos.Y));
        Camera.ConvertBox2DToScreen(ref pos, out pos);
        return pos;
    }

    private static void EnsurePixelTexture(SpriteBatch spriteBatch)
    {
        if (_pixelTexture == null || _pixelTexture.IsDisposed)
        {
            _pixelTexture = new Texture2D(spriteBatch.GraphicsDevice, 1, 1);
            _pixelTexture.SetData([Color.White]);
        }
    }
}
