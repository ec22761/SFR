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
/// Leap Mine — a thrown mine that sticks to surfaces and arms itself.
/// When a player walks near, it triggers and leaps at them.
/// Explodes on player contact or after 3 seconds.
/// </summary>
internal sealed class ObjectLeapMineThrown : ObjectData
{
    // --- Tuning constants ---
    private const float ArmDuration = 2000f;
    private const float ExplosionPower = 80f;
    private const float DetectionRadius = 80f;
    private const float ScanInterval = 200f;
    private const float FuseDuration = 3000f;
    private const float ProximityDetonateRange = 14f;

    // --- Leap ---
    private const float LeapSpeed = 9f;
    private const float LeapArcLift = 18f;

    // --- State ---
    private LeapMineState _state = LeapMineState.Airborne;
    private float _armTimer;
    private float _fuseTimer;
    private float _scanTimer;
    private Player _target;

    // --- Surface ---
    private Vector2 _surfaceNormal = new(0, 1);
    private float _surfaceAngle;
    private float _targetSurfaceAngle;

    // --- Blinking ---
    private bool _blink;
    private float _blinkInterval = 120f;
    private float _blinkTimer;

    // --- Textures ---
    private Texture2D _normalTexture;
    private Texture2D _blinkTexture;
    private Texture2D _idleTexture;
    private Texture2D _idleBlinkTexture;

    // --- Sticky ---
    private bool _stickied;
    private float _stickiedAngle;
    private ObjectData _stickiedObject;
    private Vector2 _stickiedOffset = Vector2.Zero;
    private float _normalAngle;
    private Filter _originalFilter;
    private bool _filterApplied;

    internal ObjectLeapMineThrown(ObjectDataStartParams startParams) : base(startParams)
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
                    _state = LeapMineState.Arming;
                    _armTimer = ArmDuration;
                    _blink = false;
                    _blinkTimer = 0f;
                    break;
                case 2:
                    _state = LeapMineState.Armed;
                    _blink = false;
                    break;
                case 3:
                    _state = LeapMineState.Leaping;
                    _blinkInterval = 60f;
                    _fuseTimer = FuseDuration;
                    break;
                case -1:
                    _state = LeapMineState.Dud;
                    break;
            }
        }
    }

    public override void UpdateObject(float ms)
    {
        switch (_state)
        {
            case LeapMineState.Airborne:
                break;

            case LeapMineState.Arming:
                UpdateArming(ms);
                break;

            case LeapMineState.Armed:
                UpdateArmed(ms);
                break;

            case LeapMineState.Leaping:
                UpdateLeaping(ms);
                break;
        }

        // Sticky tracking (while attached to an object)
        if (_stickied && _state is LeapMineState.Arming or LeapMineState.Armed)
        {
            UpdateStickyTracking();
        }

        // Smoothly interpolate surface angle while leaping
        if (_state is LeapMineState.Leaping)
        {
            float angleDiff = _targetSurfaceAngle - _surfaceAngle;
            while (angleDiff > (float)Math.PI) angleDiff -= (float)Math.PI * 2f;
            while (angleDiff < -(float)Math.PI) angleDiff += (float)Math.PI * 2f;
            _surfaceAngle += angleDiff * Math.Min(1f, ms * 0.015f);
        }

        // Blinking visual/sound
        if (_state is LeapMineState.Arming or LeapMineState.Leaping)
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
                Unstick();
                LeapAtTarget(GetWorldPosition(), nearest);
                Properties.Get(ObjectPropertyID.Mine_Status).Value = 3;
                SoundHandler.PlaySound("MineTrigger", GameWorld);
            }
        }
    }

    /// <summary>
    /// Leaping: the mine has launched toward a target.
    /// Count down the 3-second fuse. Explodes on fuse expiry or player contact.
    /// </summary>
    private void UpdateLeaping(float ms)
    {
        if (GameOwner == GameOwnerEnum.Client) return;

        // Proximity detonation — explode if close to the target player
        if (_target is { IsDead: false, IsRemoved: false })
        {
            float dist = Vector2.Distance(GetWorldPosition(), _target.Position);
            if (dist <= ProximityDetonateRange)
            {
                Destroy();
                return;
            }
        }

        _fuseTimer -= ms;
        if (_fuseTimer <= 0f)
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

    /// <summary>
    /// Leap toward a target player with an arc.
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

        // Explode on player contact while leaping
        if (_state is LeapMineState.Leaping && otherObject is { IsPlayer: true })
        {
            if (GameOwner != GameOwnerEnum.Client)
            {
                Destroy();
            }
            return;
        }

        // Stick to surfaces on first impact (only in airborne state)
        if (_state == LeapMineState.Airborne && !_stickied && otherObject is { RemovalInitiated: false, IsPlayer: false })
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

            _state = LeapMineState.Arming;
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
        if (_state is LeapMineState.Arming or LeapMineState.Armed)
        {
            texture = _blink ? _idleBlinkTexture : _idleTexture;
        }
        else
        {
            texture = _blink ? _blinkTexture : _normalTexture;
        }

        float drawAngle = _state is LeapMineState.Airborne or LeapMineState.Dud or LeapMineState.Leaping
            ? GetAngle()
            : -_surfaceAngle;

        spriteBatch.Draw(texture, vector, null, Color.Gray, drawAngle,
            new Vector2(texture.Width / 2, texture.Height / 2),
            Camera.ZoomUpscaled, m_faceDirectionSpriteEffect, 0f);
    }

    private enum LeapMineState
    {
        Airborne,
        Arming,
        Armed,
        Leaping,
        Dud
    }
}
