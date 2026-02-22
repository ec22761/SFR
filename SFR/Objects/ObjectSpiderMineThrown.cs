using System;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Effects;
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
/// Spider Mine — a thrown mine that sticks to surfaces, arms itself,
/// then scurries along surfaces toward the nearest enemy and explodes on contact.
/// Crawls over walls, ceilings, and floors by latching to surfaces.
/// Draws a red "eye cone" toward the target while chasing.
/// </summary>
internal sealed class ObjectSpiderMineThrown : ObjectData
{
    // --- Tuning constants ---
    private const float ArmDuration = 2000f;
    private const float DetectionRadius = 80f;
    private const float ChaseDuration = 8000f;
    private const float ProximityDetonateRange = 12f;
    private const float DetonateDelay = 150f;
    private const float ScanInterval = 200f;
    private const float ExplosionPower = 80f;

    // --- Crawl tuning ---
    private const float MoveSpeed = 4f;                   // Movement along surface
    private const float StickForce = 5f;                  // Push into surface to stay attached
    private const float ProbeRange = 2f;                  // Short raycast to verify adjacent surface
    private const float LeapSpeed = 3.5f;                 // Leap velocity when launching toward player

    // --- Eye cone visual ---
    private const float ConeLength = 40f;
    private const float ConeHalfAngle = 0.35f;

    // --- Animation ---
    private const float WalkAnimInterval = 100f;          // ms between walk frame toggles

    // --- State ---
    private SpiderState _state = SpiderState.Airborne;
    private float _armTimer;
    private float _chaseTimer;
    private float _detonateTimer = DetonateDelay;
    private float _scanTimer;

    // --- Surface crawling ---
    private Vector2 _surfaceNormal = new(0, 1);           // Normal pointing away from surface (default = floor)
    private float _surfaceAngle;
    private float _targetSurfaceAngle;
    private ChaseContact _chaseContact = ChaseContact.Airborne;
    private int _wallPushDir;                              // 1 = wall on right (push right), -1 = wall on left

    // --- Animation ---
    private float _walkAnimTimer;
    private bool _walkFrameB;                              // Toggle between WalkA and WalkB

    // --- Blinking ---
    private bool _blink;
    private float _blinkInterval = 120f;
    private float _blinkTimer;

    // --- Textures (loaded from files) ---
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

    // --- Chase target ---
    private Player _target;

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

        // Load textures from files
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
                    _chaseTimer = ChaseDuration;
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

        // Smoothly interpolate surface angle
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
                Unstick();

                // Leap toward target — one-time velocity boost
                Vector2 myPos = GetWorldPosition();
                Vector2 toTarget = _target.Position - myPos;
                if (toTarget.LengthSquared() > 0.01f)
                {
                    toTarget.Normalize();
                    Body.SetLinearVelocity(toTarget * LeapSpeed);
                }

                Properties.Get(ObjectPropertyID.Mine_Status).Value = 3;
                SoundHandler.PlaySound("MineTrigger", GameWorld);
            }
        }
    }

    private void UpdateChasing(float ms)
    {
        _chaseTimer -= ms;

        if (GameOwner != GameOwnerEnum.Client && Body is not null)
        {
            // Validate target
            if (_target is null or { IsDead: true } or { IsRemoved: true })
            {
                _target = FindNearestEnemy();
                if (_target is null)
                {
                    if (_chaseTimer <= 0f)
                    {
                        Properties.Get(ObjectPropertyID.Mine_Status).Value = 4;
                    }
                    return;
                }
            }

            Vector2 myPos = GetWorldPosition();
            Vector2 targetPos = _target.Position;
            float distance = Vector2.Distance(myPos, targetPos);

            // Proximity detonation
            if (distance <= ProximityDetonateRange)
            {
                Properties.Get(ObjectPropertyID.Mine_Status).Value = 4;
                return;
            }

            // Chase timeout
            if (_chaseTimer <= 0f)
            {
                Properties.Get(ObjectPropertyID.Mine_Status).Value = 4;
                return;
            }

            // Surface crawling movement
            CrawlTowardTarget(ms, myPos, targetPos);

            // Walk animation toggle
            _walkAnimTimer -= ms;
            if (_walkAnimTimer <= 0f)
            {
                _walkAnimTimer += WalkAnimInterval;
                _walkFrameB = !_walkFrameB;
            }
        }
    }

    /// <summary>
    /// 4-state chase movement. Surface type is set by ImpactHit (reliable normals).
    /// Each frame, Probe verifies the current surface still exists.
    /// Gravity always applies unless on a wall or ceiling.
    /// </summary>
    private void CrawlTowardTarget(float ms, Vector2 myPos, Vector2 targetPos)
    {
        int dirX = targetPos.X > myPos.X ? 1 : -1;
        FaceDirection = (short)dirX;

        switch (_chaseContact)
        {
            case ChaseContact.Airborne:
            {
                // Gravity handles Y, nudge toward player horizontally
                _targetSurfaceAngle = 0f;
                Vector2 vel = Body.GetLinearVelocity();
                Body.SetLinearVelocity(new Vector2(dirX * MoveSpeed * 0.5f, vel.Y));
                break;
            }

            case ChaseContact.Floor:
            {
                // Verify floor is still below us
                if (!Probe(myPos, 0, -1))
                {
                    _chaseContact = ChaseContact.Airborne;
                    break;
                }

                _surfaceNormal = new Vector2(0, 1);
                _targetSurfaceAngle = 0f;

                // Walk toward player. Only set X — gravity + contact solver handle Y naturally.
                Vector2 vel = Body.GetLinearVelocity();
                Body.SetLinearVelocity(new Vector2(dirX * MoveSpeed, vel.Y));
                break;
            }

            case ChaseContact.Wall:
            {
                // Verify wall is still beside us
                if (!Probe(myPos, _wallPushDir, 0))
                {
                    _chaseContact = ChaseContact.Airborne;
                    // Kill any upward velocity so we don't fly
                    Vector2 vel = Body.GetLinearVelocity();
                    Body.SetLinearVelocity(new Vector2(vel.X, Math.Min(vel.Y, 0f)));
                    break;
                }

                _surfaceNormal = new Vector2(-_wallPushDir, 0);
                _targetSurfaceAngle = _wallPushDir > 0
                    ? (float)Math.PI / 2f
                    : -(float)Math.PI / 2f;

                // Move up/down toward player, push into wall to stay attached
                // Override both axes to counteract gravity
                int dirY = targetPos.Y > myPos.Y ? 1 : -1;
                Body.SetLinearVelocity(new Vector2(_wallPushDir * StickForce, dirY * MoveSpeed));
                break;
            }

            case ChaseContact.Ceiling:
            {
                // Verify ceiling is still above us
                if (!Probe(myPos, 0, 1))
                {
                    _chaseContact = ChaseContact.Airborne;
                    // Kill upward velocity so we don't fly
                    Vector2 vel = Body.GetLinearVelocity();
                    Body.SetLinearVelocity(new Vector2(vel.X, Math.Min(vel.Y, 0f)));
                    break;
                }

                _surfaceNormal = new Vector2(0, -1);
                _targetSurfaceAngle = (float)Math.PI;

                // If directly above player, drop
                if (Math.Abs(myPos.X - targetPos.X) < 6f && myPos.Y > targetPos.Y)
                {
                    _chaseContact = ChaseContact.Airborne;
                    Body.SetLinearVelocity(Vector2.Zero);
                    break;
                }

                // Crawl along ceiling, push up to counteract gravity
                Body.SetLinearVelocity(new Vector2(dirX * MoveSpeed, StickForce));
                break;
            }
        }
    }

    /// <summary>
    /// Short raycast to verify an adjacent surface still exists.
    /// Only used to check the surface we're CURRENTLY on.
    /// </summary>
    private bool Probe(Vector2 origin, float dx, float dy)
    {
        Vector2 dir = new(dx, dy);
        GameWorld.RayCastResult result = GameWorld.RayCast(origin, dir, 0f, ProbeRange, SurfaceRayCastFilter, _ => true);
        return result.EndFixture is not null;
    }

    private bool SurfaceRayCastFilter(Fixture fixture)
    {
        ObjectData obj = Read(fixture);
        return obj is not null && !obj.IsPlayer;
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
                if (player.ObjectID == BodyID)
                {
                    continue;
                }

                float dist = Vector2.Distance(player.Position, position);
                if (dist <= DetectionRadius && dist < nearestDist)
                {
                    nearest = player;
                    nearestDist = dist;
                }
            }
        }

        return nearest;
    }

    private void Unstick()
    {
        if (!_stickied) return;

        _stickied = false;
        _stickiedObject = null;
        Body.SetType(BodyType.Dynamic);
        Body.SetLinearVelocity(Vector2.Zero);
        Body.SetFixedRotation(true);
        _chaseContact = ChaseContact.Airborne;

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

        // During chase: determine surface type from impact normal (reliable direction)
        if (_state is SpiderState.Chasing && otherObject is { IsPlayer: false })
        {
            Vector2 normal = new(e.WorldNormal.X, e.WorldNormal.Y);
            if (normal.LengthSquared() > 0.01f) normal.Normalize();

            if (normal.Y > 0.5f)
            {
                _chaseContact = ChaseContact.Floor;
            }
            else if (normal.Y < -0.5f)
            {
                _chaseContact = ChaseContact.Ceiling;
            }
            else if (Math.Abs(normal.X) > 0.5f)
            {
                _chaseContact = ChaseContact.Wall;
                // Push in opposite direction of normal (into the wall)
                _wallPushDir = normal.X > 0 ? -1 : 1;
            }

            _surfaceNormal = normal;
            _targetSurfaceAngle = (float)Math.Atan2(normal.Y, normal.X) - (float)Math.PI / 2f;
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

        // Pick the right sprite
        Texture2D texture;
        if (_state is SpiderState.Chasing or SpiderState.Detonating)
        {
            if (_walkFrameB)
            {
                texture = _blink ? _walkBBlinkTexture : _walkBTexture;
            }
            else
            {
                texture = _blink ? _walkABlinkTexture : _walkATexture;
            }
        }
        else if (_state is SpiderState.Arming or SpiderState.Armed)
        {
            texture = _blink ? _idleBlinkTexture : _idleTexture;
        }
        else
        {
            texture = _blink ? _blinkTexture : _normalTexture;
        }

        // Use surface angle for rotation (legs face the surface)
        float drawAngle = _state is SpiderState.Airborne or SpiderState.Dud ? GetAngle() : -_surfaceAngle;

        spriteBatch.Draw(texture, vector, null, Color.Gray, drawAngle,
            new Vector2(texture.Width / 2, texture.Height / 2),
            Camera.ZoomUpscaled, m_faceDirectionSpriteEffect, 0f);

        // Draw eye cone toward target while chasing
        if (_state is SpiderState.Chasing && _target is not null && GameOwner != GameOwnerEnum.Server)
        {
            DrawEyeCone(spriteBatch, vector);
        }
    }

    /// <summary>
    /// Draws a translucent red cone of vision from the mine toward the target player.
    /// </summary>
    private void DrawEyeCone(SpriteBatch spriteBatch, Vector2 screenCenter)
    {
        Vector2 myPos = GetWorldPosition();
        Vector2 targetPos = _target.Position;
        Vector2 toTarget = targetPos - myPos;
        float distance = toTarget.Length();
        if (distance < 1f) return;

        float angle = (float)Math.Atan2(toTarget.Y, toTarget.X);
        float zoom = Camera.ZoomUpscaled;
        float coneLen = Math.Min(ConeLength, distance) * zoom;

        Color coneColor = new(255, 30, 20, 40);
        const int rayCount = 7;

        for (int i = 0; i < rayCount; i++)
        {
            float t = (float)i / (rayCount - 1);
            float rayAngle = angle + ConeHalfAngle * (2f * t - 1f);

            Vector2 rayEnd = screenCenter + new Vector2(
                (float)Math.Cos(rayAngle) * coneLen,
                -(float)Math.Sin(rayAngle) * coneLen);

            DrawLine(spriteBatch, screenCenter, rayEnd, 1f * zoom, coneColor);
        }

        Color centerColor = new(255, 50, 30, 80);
        Vector2 centerEnd = screenCenter + new Vector2(
            (float)Math.Cos(angle) * coneLen,
            -(float)Math.Sin(angle) * coneLen);
        DrawLine(spriteBatch, screenCenter, centerEnd, 1.5f * zoom, centerColor);
    }

    private static void DrawLine(SpriteBatch spriteBatch, Vector2 start, Vector2 end, float thickness, Color color)
    {
        Vector2 delta = end - start;
        float length = delta.Length();
        if (length < 0.5f) return;

        float angle = (float)Math.Atan2(delta.Y, delta.X);
        spriteBatch.Draw(
            Constants.WhitePixel,
            start,
            null,
            color,
            angle,
            new Vector2(0f, 0.5f),
            new Vector2(length, thickness),
            SpriteEffects.None,
            0f);
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

    private enum ChaseContact
    {
        Airborne,
        Floor,
        Wall,
        Ceiling
    }
}
