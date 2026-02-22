using System;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Effects;
using SFD.Materials;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.Tiles;
using SFR.Helper;
using SFR.Misc;
using Explosion = SFD.Explosion;
using Math = System.Math;
using Player = SFD.Player;

namespace SFR.Objects;

/// <summary>
/// Spider Mine — a thrown mine that sticks to surfaces and arms itself.
/// When a player walks near, it triggers and leaps at them. After landing,
/// it runs along the ground toward the player, jumping over obstacles.
/// Explodes on proximity or direct player contact.
/// If the target is lost (dead/removed), it resticks and waits for the next player.
/// </summary>
internal sealed class ObjectSpiderMineThrown : ObjectData
{
    // --- Tuning constants ---
    private const float ArmDuration = 2000f;
    private const float DetonateDelay = 150f;
    private const float ExplosionPower = 80f;
    private const float DetectionRadius = 80f;
    private const float ScanInterval = 200f;
    private const float ProximityDetonateRange = 14f;

    // --- Initial leap ---
    private const float LeapSpeed = 9f;
    private const float LeapArcLift = 18f;

    // --- Ground chase ---
    private const float RunSpeed = 4f;
    private const float JumpSpeed = 6f;
    private const float ObstacleProbeRange = 5f;
    private const float ObstacleProbeHeight = 3f;
    private const float GroundProbeRange = 3f;

    // --- Animation ---
    private const float WalkAnimInterval = 100f;

    // --- State ---
    private SpiderState _state = SpiderState.Airborne;
    private float _armTimer;
    private float _detonateTimer = DetonateDelay;
    private float _scanTimer;
    private bool _hasLeaped;
    private bool _onGround;

    // --- Chase ---
    private Player _target;

    // --- Surface ---
    private Vector2 _surfaceNormal = new(0, 1);
    private float _surfaceAngle;
    private float _targetSurfaceAngle;

    // --- Animation ---
    private float _walkAnimTimer;
    private bool _walkFrameB;

    // --- Blinking ---
    private bool _blink;
    private float _blinkInterval = 120f;
    private float _blinkTimer;

    // --- Textures ---
    private Texture2D _normalTexture;
    private Texture2D _blinkTexture;
    private Texture2D _idleTexture;
    private Texture2D _idleBlinkTexture;
    private Texture2D _walkATexture;
    private Texture2D _walkABlinkTexture;
    private Texture2D _walkBTexture;
    private Texture2D _walkBBlinkTexture;

    // --- Sticky ---
    private bool _stickied;
    private float _stickiedAngle;
    private ObjectData _stickiedObject;
    private Vector2 _stickiedOffset = Vector2.Zero;
    private float _normalAngle;
    private Filter _originalFilter;
    private bool _filterApplied;

    internal ObjectSpiderMineThrown(ObjectDataStartParams startParams) : base(startParams)
    {
    }

    public override void Initialize()
    {
        EnableUpdateObject();
        GameWorld.PortalsObjectsToKeepTrackOf.Add(this);
        Body.SetBullet(true);
        Body.SetAngularDamping(3f);
        Body.SetFixedRotation(true);
        FaceDirection = 1;

        _normalTexture = Textures.GetTexture("SpiderMineM");
        _blinkTexture = Textures.GetTexture("SpiderMineMBlink");
        _idleTexture = Textures.GetTexture("SpiderMineMIdle");
        _idleBlinkTexture = Textures.GetTexture("SpiderMineMIdleBlink");
        _walkATexture = Textures.GetTexture("SpiderMineMWalk1");
        _walkABlinkTexture = Textures.GetTexture("SpiderMineMWalk1Blink");
        _walkBTexture = Textures.GetTexture("SpiderMineMWalk2");
        _walkBBlinkTexture = Textures.GetTexture("SpiderMineMWalk2Blink");
    }

    public override void OnRemoveObject() => GameWorld.PortalsObjectsToKeepTrackOf.Remove(this);

    public override void ExplosionHit(Explosion explosionData, ExplosionHitEventArgs e)
    {
        if (GameOwner != GameOwnerEnum.Client && explosionData.SourceExplosionDamage > 0f)
        {
            Destroy();
        }
    }

    public override void ProjectileHit(Projectile projectile, ProjectileHitEventArgs e)
    {
        if (GameOwner != GameOwnerEnum.Client && projectile.Properties.ProjectileID != 64 && projectile is not Projectiles.IExtendedProjectile)
        {
            Destroy();
        }
    }

    public override void SetProperties()
    {
        _ = Properties.Add(ObjectPropertyID.Mine_DudChance);
        _ = Properties.Add(ObjectPropertyID.Mine_Status);
    }

    public override void PropertyValueChanged(ObjectPropertyInstance propertyChanged)
    {
        if (propertyChanged.Base.PropertyID == 212)
        {
            int status = (int)Properties.Get(ObjectPropertyID.Mine_Status).Value;
            switch (status)
            {
                case 1:
                    _state = SpiderState.Arming;
                    _armTimer = ArmDuration;
                    _blink = false;
                    _blinkTimer = 0f;
                    break;
                case 2:
                    _state = SpiderState.Armed;
                    _blink = false;
                    break;
                case 3:
                    _state = SpiderState.Chasing;
                    _blinkInterval = 60f;
                    break;
                case 4:
                    _state = SpiderState.Detonating;
                    _detonateTimer = DetonateDelay;
                    _blinkInterval = 15f;
                    break;
                case -1:
                    _state = SpiderState.Dud;
                    break;
            }
        }
    }

    public override void UpdateObject(float ms)
    {
        switch (_state)
        {
            case SpiderState.Airborne:
                break;

            case SpiderState.Arming:
                UpdateArming(ms);
                break;

            case SpiderState.Armed:
                UpdateArmed(ms);
                break;

            case SpiderState.Chasing:
                UpdateChasing(ms);
                break;

            case SpiderState.Detonating:
                UpdateDetonating(ms);
                break;
        }

        // Sticky tracking (while attached to an object)
        if (_stickied && _state is SpiderState.Arming or SpiderState.Armed)
        {
            UpdateStickyTracking();
        }

        // Smoothly interpolate surface angle while chasing/detonating
        if (_state is SpiderState.Chasing or SpiderState.Detonating)
        {
            float angleDiff = _targetSurfaceAngle - _surfaceAngle;
            while (angleDiff > (float)Math.PI) angleDiff -= (float)Math.PI * 2f;
            while (angleDiff < -(float)Math.PI) angleDiff += (float)Math.PI * 2f;
            _surfaceAngle += angleDiff * Math.Min(1f, ms * 0.015f);
        }

        // Blinking visual/sound
        if (_state is SpiderState.Arming or SpiderState.Detonating or SpiderState.Chasing)
        {
            _blinkTimer -= ms;
            if (_blinkTimer <= 0f)
            {
                _blink = !_blink;
                _blinkTimer += _blinkInterval;
                if (_blink && GameOwner != GameOwnerEnum.Server)
                {
                    SoundHandler.PlaySound("MineTick", GameWorld);
                }
            }
        }
    }

    private void UpdateArming(float ms)
    {
        if (GameOwner == GameOwnerEnum.Client) return;

        _armTimer -= ms;
        if (_armTimer <= 0f)
        {
            Properties.Get(ObjectPropertyID.Mine_Status).Value = 2;
            SoundHandler.PlaySound("MineArmed", GameWorld);
        }
    }

    /// <summary>
    /// Armed: scan for nearby players. When one is found, unstick, leap at them.
    /// </summary>
    private void UpdateArmed(float ms)
    {
        if (GameOwner == GameOwnerEnum.Client) return;

        _scanTimer -= ms;
        if (_scanTimer <= 0f)
        {
            _scanTimer = ScanInterval;
            Player nearest = FindNearestEnemy();
            if (nearest is not null)
            {
                _target = nearest;
                _hasLeaped = false;
                Unstick();
                LeapAtTarget(GetWorldPosition(), nearest);
                Properties.Get(ObjectPropertyID.Mine_Status).Value = 3;
                SoundHandler.PlaySound("MineTrigger", GameWorld);
            }
        }
    }

    /// <summary>
    /// Chasing: after the initial leap, runs along the ground toward the player,
    /// jumping over obstacles. Explodes on proximity.
    /// </summary>
    private void UpdateChasing(float ms)
    {
        if (GameOwner == GameOwnerEnum.Client) return;
        if (Body is null) return;

        Vector2 myPos = GetWorldPosition();

        // If target is dead/gone, restick and wait for next player
        if (_target is not { IsDead: false, IsRemoved: false })
        {
            Restick();
            return;
        }

        // Proximity detonation
        float dist = Vector2.Distance(myPos, _target.Position);
        if (dist <= ProximityDetonateRange)
        {
            Destroy();
            return;
        }

        // After initial leap has landed, run along ground
        if (_hasLeaped)
        {
            float dirX = _target.Position.X > myPos.X ? 1f : -1f;
            FaceDirection = (short)dirX;

            // Check if on ground using a short downward raycast
            _onGround = Probe(myPos, new Vector2(0, -1), GroundProbeRange);

            // Get current velocity, override X for running, preserve Y for physics
            Vector2 vel = Body.GetLinearVelocity();
            vel.X = dirX * RunSpeed;

            // Obstacle detection: raycast forward at body level
            if (_onGround)
            {
                // Probe forward at ground level — if wall detected, jump
                bool wallAhead = Probe(myPos, new Vector2(dirX, 0), ObstacleProbeRange);

                // Also probe forward-and-up — if that's clear, it's a jumpable obstacle
                bool clearAbove = !Probe(myPos + new Vector2(0, ObstacleProbeHeight), new Vector2(dirX, 0), ObstacleProbeRange);

                if (wallAhead && clearAbove)
                {
                    vel.Y = JumpSpeed;
                    _onGround = false;
                }

                // Edge detection: if no ground ahead, small hop to maintain momentum
                bool groundAhead = Probe(myPos + new Vector2(dirX * ObstacleProbeRange, 0), new Vector2(0, -1), GroundProbeRange + 2f);
                if (!groundAhead)
                {
                    // Small hop to clear gap
                    vel.Y = JumpSpeed * 0.6f;
                    _onGround = false;
                }
            }

            Body.SetLinearVelocity(vel);

            // Keep surface angle level while running on ground
            if (_onGround)
            {
                _targetSurfaceAngle = 0f;
            }
        }

        // Walk animation toggle
        _walkAnimTimer -= ms;
        if (_walkAnimTimer <= 0f)
        {
            _walkAnimTimer += WalkAnimInterval;
            _walkFrameB = !_walkFrameB;
        }
    }

    /// <summary>
    /// Initial leap toward a target player with a sticky-bomb-like arc.
    /// </summary>
    private void LeapAtTarget(Vector2 myPos, Player target)
    {
        Vector2 toTarget = target.Position - myPos;
        FaceDirection = (short)(toTarget.X >= 0 ? 1 : -1);

        Vector2 launchAim = toTarget + new Vector2(0f, LeapArcLift);
        if (launchAim.LengthSquared() > 0.01f)
        {
            launchAim.Normalize();
        }
        Body.SetLinearVelocity(launchAim * LeapSpeed);
    }

    /// <summary>
    /// Short raycast to detect surfaces/obstacles.
    /// </summary>
    private bool Probe(Vector2 origin, Vector2 direction, float distance)
    {
        if (direction.LengthSquared() < 0.0001f) return false;

        direction.Normalize();
        GameWorld.RayCastResult result = GameWorld.RayCast(origin, direction, 0f, distance, SurfaceRayCastFilter, _ => true);
        return result.EndFixture is not null;
    }

    private bool SurfaceRayCastFilter(Fixture fixture)
    {
        if (fixture.IsCloud()) return false;

        ObjectData obj = Read(fixture);
        if (obj is null || obj == this || obj.IsPlayer) return false;

        fixture.GetFilterData(out Filter filter);
        if ((filter.categoryBits & 15) <= 0) return false;

        Material mat = obj.Tile.GetTileFixtureMaterial(fixture.TileFixtureIndex);
        return !mat.Transparent;
    }

    /// <summary>
    /// Restick at the current position and return to Armed/idle state.
    /// </summary>
    private void Restick()
    {
        _target = null;
        _hasLeaped = false;
        _onGround = false;

        ChangeBodyType(BodyType.Static);
        Body.SetLinearVelocity(Vector2.Zero);

        _stickied = true;
        _stickiedObject = null;

        if (!_filterApplied)
        {
            ApplyStaticFilter();
        }

        _blink = false;
        _blinkTimer = 0f;
        _blinkInterval = 120f;
        _scanTimer = ScanInterval;

        if (GameOwner != GameOwnerEnum.Client)
        {
            Properties.Get(ObjectPropertyID.Mine_Status).Value = 2;
            SoundHandler.PlaySound("MineArmed", GameWorld);
        }
    }

    private Player FindNearestEnemy()
    {
        Vector2 position = GetWorldPosition();
        AABB.Create(out AABB area, position, position, DetectionRadius);
        Player nearest = null;
        float nearestDist = float.MaxValue;

        foreach (ObjectData obj in GameWorld.GetObjectDataByArea(area, false, SFDGameScriptInterface.PhysicsLayer.Active))
        {
            if (obj.InternalData is Player player && !player.IsDead && !player.IsRemoved)
            {
                if (player.ObjectID == BodyID) continue;

                float d = Vector2.Distance(player.Position, position);
                if (d <= DetectionRadius && d < nearestDist)
                {
                    nearest = player;
                    nearestDist = d;
                }
            }
        }

        return nearest;
    }

    private void UpdateDetonating(float ms)
    {
        if (GameOwner == GameOwnerEnum.Client) return;

        _detonateTimer -= ms;
        if (_detonateTimer <= 0f)
        {
            if (Globals.Random.NextFloat() < (float)Properties.Get(ObjectPropertyID.Mine_DudChance).Value)
            {
                EffectHandler.PlayEffect("GR_D", GetWorldPosition(), GameWorld);
                SoundHandler.PlaySound("GrenadeDud", GameWorld);
                Properties.Get(ObjectPropertyID.Mine_Status).Value = -1;
                Body.SetType(BodyType.Dynamic);
                if (_filterApplied)
                {
                    Body.GetFixtureByIndex(0).SetFilterData(ref _originalFilter);
                    _filterApplied = false;
                }
            }
            else
            {
                Destroy();
            }

            DisableUpdateObject();
        }
    }

    private void Unstick()
    {
        if (!_stickied) return;

        _stickied = false;
        _stickiedObject = null;
        Body.SetType(BodyType.Dynamic);
        Body.SetLinearVelocity(Vector2.Zero);
        Body.SetFixedRotation(true);

        if (_filterApplied)
        {
            Body.GetFixtureByIndex(0).SetFilterData(ref _originalFilter);
            _filterApplied = false;
        }
    }

    private void UpdateStickyTracking()
    {
        if (_stickiedObject is { RemovalInitiated: false })
        {
            if (!_filterApplied)
            {
                ApplyStaticFilter();
            }

            if (_stickiedObject.Body is not null)
            {
                Vector2 gamePos = _stickiedOffset;
                SFDMath.RotatePosition(ref gamePos, _stickiedObject.GetAngle() - _stickiedAngle, out gamePos);
                gamePos += _stickiedObject.GetWorldPosition();
                Vector2 newPos = new(Converter.WorldToBox2D(gamePos.X), Converter.WorldToBox2D(gamePos.Y));
                Body.SetTransform(newPos, -_stickiedObject.GetAngle() + _stickiedAngle - _normalAngle);
                SyncTransform();
            }
            else
            {
                _stickied = false;
                Body.SetType(BodyType.Dynamic);
            }
        }
    }

    private void ApplyStaticFilter()
    {
        GetFixtureByIndex(0).GetFilterData(out _originalFilter);
        Filter filter = new()
        {
            categoryBits = 0,
            aboveBits = 0,
            maskBits = 0,
            blockMelee = false,
            projectileHit = true,
            absorbProjectile = true
        };
        Body.GetFixtureByIndex(0).SetFilterData(ref filter);
        _filterApplied = true;
    }

    public override void OnDestroyObject()
    {
        if (GameOwner != GameOwnerEnum.Client)
        {
            _ = GameWorld.TriggerExplosion(GetWorldPosition(), ExplosionPower);
            SoundHandler.PlaySound("Explosion", GetWorldPosition(), GameWorld);
            EffectHandler.PlayEffect("EXP", GetWorldPosition(), GameWorld);
            EffectHandler.PlayEffect("CAM_S", GetWorldPosition(), GameWorld, 6f, 200f, false);
        }
    }

    public override void ImpactHit(ObjectData otherObject, ImpactHitEventArgs e)
    {
        base.ImpactHit(otherObject, e);
        if (GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound(Tile.ImpactSound, GetWorldPosition(), GameWorld);
            EffectHandler.PlayEffect(Tile.ImpactEffect, GetWorldPosition(), GameWorld);
        }

        // Explode on player contact while chasing or detonating
        if (_state is SpiderState.Chasing or SpiderState.Detonating && otherObject is { IsPlayer: true })
        {
            if (GameOwner != GameOwnerEnum.Client)
            {
                Destroy();
            }
            return;
        }

        // During chase: after initial leap, landing on a surface marks us grounded
        if (_state is SpiderState.Chasing && otherObject is { IsPlayer: false })
        {
            Vector2 normal = new(e.WorldNormal.X, e.WorldNormal.Y);
            if (normal.LengthSquared() > 0.01f) normal.Normalize();

            // If this is a floor (normal pointing up), we've landed
            if (normal.Y > 0.5f)
            {
                _onGround = true;
                _hasLeaped = true;
                _surfaceNormal = normal;
                _targetSurfaceAngle = 0f;
            }
        }

        // Stick to surfaces on first impact (only in airborne state)
        if (_state == SpiderState.Airborne && !_stickied && otherObject is { RemovalInitiated: false, IsPlayer: false })
        {
            ChangeBodyType(BodyType.Static);
            _stickiedObject = otherObject;
            _stickied = true;
            _stickiedOffset = GetWorldPosition() - otherObject.GetWorldPosition();
            _stickiedAngle = otherObject.GetAngle();
            _normalAngle = (float)Math.Atan2(e.WorldNormal.Y, e.WorldNormal.X);

            _surfaceNormal = new Vector2(e.WorldNormal.X, e.WorldNormal.Y);
            if (_surfaceNormal.LengthSquared() > 0.01f) _surfaceNormal.Normalize();
            _surfaceAngle = _normalAngle - (float)Math.PI / 2f;
            _targetSurfaceAngle = _surfaceAngle;

            _state = SpiderState.Arming;
            _armTimer = ArmDuration;
            _blink = false;
            _blinkTimer = 0f;

            if (GameOwner != GameOwnerEnum.Client)
            {
                Properties.Get(ObjectPropertyID.Mine_Status).Value = 1;
            }
        }
    }

    public override void Draw(SpriteBatch spriteBatch, float ms)
    {
        Vector2 vector = Body.Position;
        vector += GameWorld.DrawingBox2DSimulationTimestepOver * Body.GetLinearVelocity();
        Camera.ConvertBox2DToScreen(ref vector, out vector);

        Texture2D texture;
        if (_state is SpiderState.Chasing or SpiderState.Detonating)
        {
            texture = _walkFrameB
                ? (_blink ? _walkBBlinkTexture : _walkBTexture)
                : (_blink ? _walkABlinkTexture : _walkATexture);
        }
        else if (_state is SpiderState.Arming or SpiderState.Armed)
        {
            texture = _blink ? _idleBlinkTexture : _idleTexture;
        }
        else
        {
            texture = _blink ? _blinkTexture : _normalTexture;
        }

        float drawAngle = _state is SpiderState.Airborne or SpiderState.Dud ? GetAngle() : -_surfaceAngle;

        spriteBatch.Draw(texture, vector, null, Color.Gray, drawAngle,
            new Vector2(texture.Width / 2, texture.Height / 2),
            Camera.ZoomUpscaled, m_faceDirectionSpriteEffect, 0f);
    }

    private enum SpiderState
    {
        Airborne,
        Arming,
        Armed,
        Chasing,
        Detonating,
        Dud
    }
}
