using System;
using System.Collections.Generic;
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
    private const float BeamDamage = 2.0f;
    private const float BeamObjectDamage = 9999f; // massive damage — instantly smash through any object
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
    private const int SparkCount = 6; // sparks per frame at hit point
    private const float HitEffectInterval = 80f; // ms between material smoke/dust effects at hit point
    private const int MaxPierceIterations = 20; // max objects the beam can pierce through
    private const float PlasmaGlowRadius = 8f; // radius of the blue plasma glow at beam end
    private const int PlasmaParticleCount = 8; // number of blue plasma particles per frame
    private const float PlasmaEffectInterval = 30f; // ms between plasma particle spawns
    private const float SmokeEffectInterval = 60f; // ms between smoke effects at hit point

    // --- Player impact constants ---
    private const float BeamPushForceX = 2.0f;   // horizontal shove along beam direction
    private const float BeamPushForceY = 0.7f;   // small upward lift
    private const float BeamPushMaxSpeedX = 3.5f; // cap so continuous beam doesn't launch player to escape velocity

    // Burn buildup meter: each beam hit on a player increments their buildup;
    // when no longer hit, the buildup decays. Crossing the first threshold
    // ignites them (regular fire); crossing the second sets them ablaze in
    // an inferno ("double fire").
    private const float BurnBuildupPerHit = 1f;        // added each fire tick the beam touches the player
    private const float BurnBuildupDecayPerMs = 0.005f; // lost per ms when no longer hit
    private const float BurnBuildupFireThreshold = 12f;     // ~0.6s of continuous beam
    private const float BurnBuildupInfernoThreshold = 30f;  // ~1.5s of continuous beam
    private const float BurnBuildupMax = 45f;               // hard cap so it can't grow indefinitely

    // --- Beam electric arc constants (showstopper-style arcs along the beam) ---
    private const int MaxBeamArcs = 8;
    private const float ArcSpawnInterval = 80f;   // ms between new arc spawns along the beam
    private const float ArcLength = 14f;          // perpendicular reach of each arc from the beam
    private const int ArcSegmentCount = 6;        // jagged segments per arc

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
    private float _lastPlasmaEffectTime; // throttle plasma particle draws
    private float _lastSmokeEffectTime; // throttle smoke puff effects

    // Per-player burn buildup meter (player ObjectID → buildup units).
    // Tracked on the host only.
    private readonly Dictionary<int, float> _burnBuildup = new();
    // Players whose buildup was incremented during the current fire tick —
    // used so Update() only decays players that weren't hit this frame.
    private readonly HashSet<int> _hitThisTick = new();

    // Beam electric arc state
    private readonly BeamArc[] _beamArcs = new BeamArc[MaxBeamArcs];
    private int _nextArcIndex;
    private float _lastArcSpawnTime;
    private bool _arcsInitialized;

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

        // Apply burn-buildup decay on the host. Players hit this tick keep their
        // buildup (incremented in BeforeCreateProjectile); everyone else decays.
        if (_burnBuildup.Count > 0 && player.GameOwner != GameOwnerEnum.Client)
        {
            List<int> toRemove = null;
            foreach (int id in new List<int>(_burnBuildup.Keys))
            {
                if (_hitThisTick.Contains(id)) continue;

                float v = _burnBuildup[id] - ms * BurnBuildupDecayPerMs;
                if (v <= 0f)
                {
                    (toRemove ??= new List<int>()).Add(id);
                }
                else
                {
                    _burnBuildup[id] = v;
                }
            }
            if (toRemove != null)
            {
                foreach (int id in toRemove)
                {
                    _burnBuildup.Remove(id);
                }
            }
        }
        _hitThisTick.Clear();

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

            // Thick blue glow overlay — super strong beam look.
            DrawBeamLine(spriteBatch, muzzleWorld, previewEnd, aimDir, new Color(20, 50, 180, 40), FiringBeamOuterWidth * 2f);
            DrawBeamLine(spriteBatch, muzzleWorld, previewEnd, aimDir, new Color(30, 80, 200, 70), FiringBeamOuterWidth);
            DrawBeamLine(spriteBatch, muzzleWorld, previewEnd, aimDir, new Color(80, 180, 255, 130), FiringBeamMidWidth);
            // Almost-white core.
            DrawBeamLine(spriteBatch, muzzleWorld, previewEnd, aimDir, new Color(220, 240, 255, 240), FiringBeamCoreWidth);

            // ── Strong smoke + blue plasma effects at beam endpoint ──
            if (hitSomething)
            {
                // Smoke and spark game effects at impact.
                if (now > _lastSmokeEffectTime + SmokeEffectInterval)
                {
                    EffectHandler.PlayEffect("S_P", previewEnd, player.GameWorld);
                    EffectHandler.PlayEffect("TR_S", previewEnd, player.GameWorld);
                    _lastSmokeEffectTime = now;
                }

                // Large blue plasma glow — multiple overlapping circles.
                if (now > _lastPlasmaEffectTime + PlasmaEffectInterval)
                {
                    _lastPlasmaEffectTime = now;

                    // Big plasma particles radiating outward from impact.
                    for (int i = 0; i < PlasmaParticleCount; i++)
                    {
                        float spread = PlasmaGlowRadius;
                        Vector2 plasmaWorld = previewEnd + new Vector2(
                            Globals.Random.NextFloat(-spread, spread),
                            Globals.Random.NextFloat(-spread, spread));
                        Vector2 plasmaScreen = WorldToScreen(plasmaWorld);
                        float plasmaSize = Globals.Random.NextFloat(2f, 5f) * Camera.ZoomUpscaled;
                        int plasmaAlpha = Globals.Random.Next(80, 200);
                        Color plasmaColor = new(40, 120, 255, plasmaAlpha);
                        spriteBatch.Draw(_pixelTexture, plasmaScreen, null, plasmaColor, 0f,
                            new Vector2(0.5f, 0.5f), plasmaSize, SpriteEffects.None, 0f);
                    }

                    // Bright white-blue plasma core at exact impact point.
                    Vector2 coreScreen = WorldToScreen(previewEnd);
                    float coreSize = Globals.Random.NextFloat(3f, 6f) * Camera.ZoomUpscaled;
                    spriteBatch.Draw(_pixelTexture, coreScreen, null, new Color(180, 220, 255, 220), 0f,
                        new Vector2(0.5f, 0.5f), coreSize, SpriteEffects.None, 0f);

                    // Outer blue plasma haze — large soft glow.
                    float hazeSize = Globals.Random.NextFloat(8f, 14f) * Camera.ZoomUpscaled;
                    spriteBatch.Draw(_pixelTexture, coreScreen, null, new Color(30, 80, 220, 50), 0f,
                        new Vector2(0.5f, 0.5f), hazeSize, SpriteEffects.None, 0f);
                }

                // Blue spark particles at beam hit point — more and brighter than before.
                if (now > _lastSparkEffectTime + SparkEffectInterval)
                {
                    _lastSparkEffectTime = now;
                    for (int i = 0; i < SparkCount; i++)
                    {
                        Vector2 sparkWorld = previewEnd + new Vector2(
                            Globals.Random.NextFloat(-4f, 4f), Globals.Random.NextFloat(-4f, 4f));
                        Vector2 sparkScreen = WorldToScreen(sparkWorld);
                        float sparkSize = Globals.Random.NextFloat(1f, 3f) * Camera.ZoomUpscaled;
                        int sparkAlpha = Globals.Random.Next(180, 255);
                        Color sparkColor = new(120, 200, 255, sparkAlpha);
                        spriteBatch.Draw(_pixelTexture, sparkScreen, null, sparkColor, 0f,
                            new Vector2(0.5f, 0.5f), sparkSize, SpriteEffects.None, 0f);
                    }
                }
            }

            // ── Electric arcs along the beam (showstopper-style) ──
            if (!_arcsInitialized)
            {
                for (int i = 0; i < MaxBeamArcs; i++)
                    _beamArcs[i] = new BeamArc();
                _arcsInitialized = true;
            }

            // Spawn new arcs periodically at random positions along the beam.
            if (now > _lastArcSpawnTime + ArcSpawnInterval)
            {
                _lastArcSpawnTime = now;
                float t = Globals.Random.NextFloat(0.05f, 0.85f);
                Vector2 arcOrigin = Vector2.Lerp(muzzleWorld, previewEnd, t);

                _beamArcs[_nextArcIndex].Activate(arcOrigin, aimDir,
                    Globals.Random.NextFloat(ArcLength * 0.5f, ArcLength),
                    Globals.Random.NextFloat(100f, 250f));
                _nextArcIndex = (_nextArcIndex + 1) % MaxBeamArcs;
            }

            // Update and draw all active arcs.
            for (int i = 0; i < MaxBeamArcs; i++)
            {
                if (!_beamArcs[i].Active) continue;
                _beamArcs[i].Update(ms);
                if (!_beamArcs[i].Active) continue;
                DrawBeamArc(spriteBatch, _beamArcs[i]);
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

        if (args.Player.GameOwner != GameOwnerEnum.Client)
        {
            // Server/host: piercing damage raycast — beam smashes through all
            // solid objects. Uses the same collision filter as the visual beam
            // (no cloud-passable fixtures) so damage and visuals stay in sync at
            // every aim angle.
            Vector2 castOrigin = muzzleWorld;
            float remainingRange = range;
            HashSet<int> hitPlayerIds = null;
            HashSet<int> hitObjectIds = null;

            for (int pierce = 0; pierce < MaxPierceIterations && remainingRange > 1f; pierce++)
            {
                GameWorld.RayCastResult ray = args.Player.GameWorld.RayCast(
                    castOrigin, aimDir, 0f, remainingRange, LazerRayCastCollision, _ => true);

                if (ray.EndFixture is null)
                {
                    // Nothing more to hit — beam ends at max range.
                    _beamEnd = castOrigin + aimDir * remainingRange;
                    break;
                }

                ObjectData hitObj = ObjectData.Read(ray.EndFixture);

                if (hitObj.IsPlayer && hitObj.InternalData is Player hitPlayer &&
                    !hitPlayer.IsDead && !hitPlayer.IsRemoved)
                {
                    // Only damage each player once per fire tick to prevent
                    // the piercing loop from multi-hitting the same hitbox.
                    hitPlayerIds ??= new HashSet<int>();
                    if (hitPlayerIds.Add(hitPlayer.ObjectID))
                    {
                        hitPlayer.TakeMiscDamage(BeamDamage, sourceID: args.Player.ObjectID);

                        // Knock the player back along the beam direction. Apply
                        // through WorldBody (the underlying Box2D body) and refresh
                        // the player's cached pre-physics velocity so the engine
                        // doesn't immediately overwrite our push next physics step.
                        if (hitPlayer.WorldBody != null)
                        {
                            Vector2 vel = hitPlayer.WorldBody.GetLinearVelocity();
                            float pushX = aimDir.X * BeamPushForceX;
                            // Add the push but clamp horizontal speed so continuous
                            // beam doesn't accelerate the player off the map.
                            vel.X = Math.Sign(pushX) != 0
                                ? MathHelper.Clamp(vel.X + pushX, -BeamPushMaxSpeedX, BeamPushMaxSpeedX)
                                : vel.X;
                            // Always nudge upward a little so the push feels real
                            // even while the target is grounded.
                            if (vel.Y < BeamPushForceY) vel.Y = BeamPushForceY;
                            hitPlayer.WorldBody.SetLinearVelocity(vel);
                            hitPlayer.m_preBox2DLinearVelocity = vel;
                            hitPlayer.AirControlBaseVelocity = vel;
                            hitPlayer.ForceServerPositionState();
                            hitPlayer.ImportantUpdate = true;
                        }

                        // Increment the burn buildup meter and check thresholds.
                        _hitThisTick.Add(hitPlayer.ObjectID);
                        _burnBuildup.TryGetValue(hitPlayer.ObjectID, out float buildup);
                        buildup = Math.Min(BurnBuildupMax, buildup + BurnBuildupPerHit);
                        _burnBuildup[hitPlayer.ObjectID] = buildup;

                        if (buildup >= BurnBuildupFireThreshold && !hitPlayer.Burning)
                        {
                            hitPlayer.ObjectData?.SetMaxFire();
                        }
                        if (buildup >= BurnBuildupInfernoThreshold && !hitPlayer.BurningInferno)
                        {
                            hitPlayer.BurningInferno = true;
                            hitPlayer.ObjectData?.SetMaxFire();
                        }
                    }

                    // Pierce through players — continue beam past them.
                    float traveled = Vector2.Distance(castOrigin, ray.EndPosition) + 2f;
                    castOrigin = ray.EndPosition + aimDir * 2f;
                    remainingRange -= traveled;
                    continue;
                }

                // Non-player object hit.
                if (hitObj is ObjectExplosive or ObjectBarrelExplosive)
                {
                    // Detonate explosive barrels / oil canisters / small explosive
                    // crates. We use both the explicit Exploding property (the same
                    // path the sledgehammer's heavy attack uses for big barrels) AND
                    // a massive script-damage hit so smaller explosives that don't
                    // honor that property still die and trigger their explosion.
                    hitObjectIds ??= new HashSet<int>();
                    if (hitObjectIds.Add(hitObj.ObjectID))
                    {
                        try
                        {
                            ((ObjectDestructible)hitObj).Properties.Get(ObjectPropertyID.BarrelExplosive_Exploding).Value = true;
                        }
                        catch
                        {
                            // Property may not be present on every explosive variant.
                        }
                        if (hitObj is ObjectExplosive explosive)
                        {
                            explosive.time = 80f;
                        }
                        else if (hitObj is ObjectBarrelExplosive barrel)
                        {
                            barrel.time = 80f;
                        }

                        // Fallback: deal massive damage so any explosive that doesn't
                        // respond to the property still gets destroyed and triggers
                        // its on-destroy explosion.
                        hitObj.DealScriptDamage((int)BeamObjectDamage, args.Player.ObjectID);
                    }

                    // Pierce through — continue beam past the now-detonating object.
                    float traveledExp = Vector2.Distance(castOrigin, ray.EndPosition) + 2f;
                    castOrigin = ray.EndPosition + aimDir * 2f;
                    remainingRange -= traveledExp;
                    continue;
                }

                if (hitObj.Destructable)
                {
                    hitObjectIds ??= new HashSet<int>();
                    if (hitObjectIds.Add(hitObj.ObjectID))
                    {
                        // Obliterate the object instantly.
                        hitObj.DealScriptDamage((int)BeamObjectDamage, args.Player.ObjectID);

                        // Play material destruction effects at each smashed object.
                        if (hitObj.Tile?.Material != null)
                        {
                            Material mat = hitObj.Tile.Material;
                            EffectHandler.PlayEffect(mat.Hit.Projectile.HitEffect, ray.EndPosition, args.Player.GameWorld);
                            SoundHandler.PlaySound(mat.Hit.Projectile.HitSound, ray.EndPosition, args.Player.GameWorld);
                        }
                    }

                    // Pierce through — continue beam past the destroyed object.
                    float traveled = Vector2.Distance(castOrigin, ray.EndPosition) + 2f;
                    castOrigin = ray.EndPosition + aimDir * 2f;
                    remainingRange -= traveled;
                    continue;
                }

                // Hit an indestructible surface — beam stops here.
                _beamEnd = ray.EndPosition;
                // Visual effects (smoke, sparks, material hits) are handled
                // in DrawExtra to avoid doubling up on the host.
                break;
            }

            // Separate, non-blocking pass that damages cloud-passable
            // destructibles (e.g. chain links) along the beam path — these are
            // skipped by the main filter so they don't interfere with hits on
            // big boxes or players, but bullets normally break them so the beam
            // should too.
            BreakCloudDestructiblesAlongBeam(args.Player, muzzleWorld, aimDir,
                Vector2.Distance(muzzleWorld, _beamEnd) + 4f, hitObjectIds);
        }
        else
        {
            // Client: visual-only raycast for beam endpoint — also pierce through destructibles.
            Vector2 castOrigin = muzzleWorld;
            float remainingRange = range;

            for (int pierce = 0; pierce < MaxPierceIterations && remainingRange > 1f; pierce++)
            {
                GameWorld.RayCastResult ray = args.Player.GameWorld.RayCast(
                    castOrigin, aimDir, 0f, remainingRange, LazerRayCastCollision, _ => true);

                if (ray.EndFixture is null)
                {
                    _beamEnd = castOrigin + aimDir * remainingRange;
                    break;
                }

                ObjectData hitObj = ObjectData.Read(ray.EndFixture);

                if (hitObj.IsPlayer || hitObj.Destructable)
                {
                    // Pierce through players and destructible objects visually.
                    float traveled = Vector2.Distance(castOrigin, ray.EndPosition) + 2f;
                    castOrigin = ray.EndPosition + aimDir * 2f;
                    remainingRange -= traveled;
                    continue;
                }

                // Indestructible surface — beam ends here.
                _beamEnd = ray.EndPosition;
                break;
            }
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
    ///     Permissive damage-scan filter — catches every destructible / explosive
    ///     object regardless of cloud/category, but never indestructible terrain.
    ///     Used by a secondary sweep that breaks small barrels, chain links and
    ///     other "thin" destructibles the main beam (LazerRayCastCollision) skips.
    /// </summary>
    private static bool DestructibleSweepCollision(Fixture fixture)
    {
        ObjectData objectData = ObjectData.Read(fixture);
        return objectData is { IsPlayer: false, Destructable: true };
    }

    /// <summary>
    ///     Sweeps along the beam and damages / detonates every destructible
    ///     object it meets that the main beam didn't already handle (chain
    ///     links, small explosive barrels, glass, etc.). Does not affect the
    ///     main beam endpoint.
    /// </summary>
    private void BreakCloudDestructiblesAlongBeam(Player owner, Vector2 origin,
        Vector2 dir, float maxDistance, HashSet<int> alreadyDamagedIds)
    {
        Vector2 castOrigin = origin;
        float remaining = maxDistance;

        for (int i = 0; i < MaxPierceIterations && remaining > 1f; i++)
        {
            GameWorld.RayCastResult ray = owner.GameWorld.RayCast(
                castOrigin, dir, 0f, remaining, DestructibleSweepCollision, _ => true);

            if (ray.EndFixture is null) return;

            ObjectData hitObj = ObjectData.Read(ray.EndFixture);
            if (hitObj == null) return;

            if (alreadyDamagedIds == null || alreadyDamagedIds.Add(hitObj.ObjectID))
            {
                if (hitObj is ObjectExplosive or ObjectBarrelExplosive)
                {
                    try
                    {
                        ((ObjectDestructible)hitObj).Properties.Get(ObjectPropertyID.BarrelExplosive_Exploding).Value = true;
                    }
                    catch
                    {
                        // Property may not be present on every explosive variant.
                    }
                    if (hitObj is ObjectExplosive explosive)
                    {
                        explosive.time = 80f;
                    }
                    else if (hitObj is ObjectBarrelExplosive barrel)
                    {
                        barrel.time = 80f;
                    }
                }

                hitObj.DealScriptDamage((int)BeamObjectDamage, owner.ObjectID);
            }

            float traveled = Vector2.Distance(castOrigin, ray.EndPosition) + 2f;
            castOrigin = ray.EndPosition + dir * 2f;
            remaining -= traveled;
        }
    }

    /// <summary>
    ///     RayCast collision filter — same logic as the claymore laser: collide with
    ///     solid fixtures and players. Used for the visual beam endpoint.
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

    /// <summary>
    ///     Draw a single electric arc (showstopper-style jagged bolt).
    /// </summary>
    private static void DrawBeamArc(SpriteBatch spriteBatch, BeamArc arc)
    {
        if (arc.Segments == null) return;

        int a = Math.Min(255, (int)(arc.Alpha * 255));
        if (a <= 0) return;

        Color outerColor = new(60, 180, 255, (int)(a * 0.5f));
        Color coreColor = new(220, 240, 255, a);

        Vector2 lineOrigin = new(0f, 0.5f);
        float outerWidth = 2.5f * Camera.ZoomUpscaled;
        float coreWidth = 1f * Camera.ZoomUpscaled;

        for (int i = 0; i < arc.Segments.Length - 1; i++)
        {
            Vector2 startScreen = WorldToScreen(arc.Segments[i]);
            Vector2 endScreen = WorldToScreen(arc.Segments[i + 1]);

            Vector2 diff = endScreen - startScreen;
            float length = diff.Length();
            if (length < 0.5f) continue;

            float angle = (float)Math.Atan2(diff.Y, diff.X);

            // Outer glow (cyan)
            spriteBatch.Draw(_pixelTexture, startScreen, null, outerColor, angle, lineOrigin,
                new Vector2(length, outerWidth), SpriteEffects.None, 0f);
            // Bright core
            spriteBatch.Draw(_pixelTexture, startScreen, null, coreColor, angle, lineOrigin,
                new Vector2(length, coreWidth), SpriteEffects.None, 0f);

            // Micro-spark near segment midpoint
            if (Globals.Random.NextFloat() < 0.4f)
            {
                Vector2 mid = Vector2.Lerp(startScreen, endScreen, Globals.Random.NextFloat(0.2f, 0.8f));
                Vector2 perp = new(-diff.Y, diff.X);
                if (perp.LengthSquared() > 0) perp.Normalize();
                mid += perp * Globals.Random.NextFloat(-1.5f, 1.5f) * Camera.ZoomUpscaled;

                float sparkSize = Globals.Random.NextFloat(0.5f, 1.5f) * Camera.ZoomUpscaled;
                Color sparkColor = new(180, 230, 255, Globals.Random.Next(120, 255));
                spriteBatch.Draw(_pixelTexture, mid, null, sparkColor, 0f,
                    new Vector2(0.5f, 0.5f), sparkSize, SpriteEffects.None, 0f);
            }
        }
    }

    private static void EnsurePixelTexture(SpriteBatch spriteBatch)
    {
        if (_pixelTexture == null || _pixelTexture.IsDisposed)
        {
            _pixelTexture = new Texture2D(spriteBatch.GraphicsDevice, 1, 1);
            _pixelTexture.SetData([Color.White]);
        }
    }

    /// <summary>
    ///     Short electric arc that travels along the beam direction — styled after
    ///     the ShowStopper's LightningBolt with jagged, jittering segments.
    /// </summary>
    private sealed class BeamArc
    {
        internal bool Active;
        internal Vector2[] Segments;
        internal float Lifetime;
        internal float MaxLifetime;
        internal float Alpha;
        private float _jitterTimer;
        private Vector2 _beamDir;  // cached beam direction for jitter

        internal void Activate(Vector2 beamPoint, Vector2 beamDir, float length, float lifetime)
        {
            Active = true;
            Lifetime = lifetime;
            MaxLifetime = lifetime;
            Alpha = 1f;
            _beamDir = beamDir;

            // Arc extends along the beam direction from this point.
            Vector2 end = beamPoint + beamDir * length;
            GenerateSegments(beamPoint, end);
        }

        private void GenerateSegments(Vector2 start, Vector2 end)
        {
            Segments = new Vector2[ArcSegmentCount + 1];
            Segments[0] = start;
            Segments[ArcSegmentCount] = end;

            // Perpendicular to the beam for jagged offsets.
            Vector2 perp = new(-_beamDir.Y, _beamDir.X);

            for (int i = 1; i < ArcSegmentCount; i++)
            {
                float t = (float)i / ArcSegmentCount;
                Vector2 midpoint = Vector2.Lerp(start, end, t);

                float offset = Globals.Random.NextFloat(-4f, 4f) * (1f - Math.Abs(t - 0.5f) * 2f);
                Segments[i] = midpoint + perp * offset;
            }
        }

        internal void Update(float ms)
        {
            Lifetime -= ms;
            if (Lifetime <= 0f)
            {
                Active = false;
                return;
            }

            Alpha = Lifetime / MaxLifetime;

            // Re-jitter segments periodically for a flickering effect.
            _jitterTimer -= ms;
            if (_jitterTimer <= 0f)
            {
                _jitterTimer = 40f;
                if (Segments != null && Segments.Length > 2)
                {
                    GenerateSegments(Segments[0], Segments[Segments.Length - 1]);
                }
            }
        }
    }
}
