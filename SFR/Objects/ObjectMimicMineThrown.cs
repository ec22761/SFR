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
using SFR.Projectiles;
using Explosion = SFD.Explosion;
using Math = System.Math;
using Player = SFD.Player;

namespace SFR.Objects;

/// <summary>
/// Mimic Mine — a thrown mine that looks like a supply crate.
/// Once it lands and arms itself, it disguises as a randomly chosen
/// supply crate variant with a very subtle hue shift.
/// When a player walks within pickup range, it explodes.
/// </summary>
internal sealed class ObjectMimicMineThrown : ObjectData
{
    // --- Tuning constants ---
    private const float ArmDuration = 2000f;
    private const float DetonateDelay = 100f;

    // --- State ---
    private MimicState _state = MimicState.Airborne;
    private float _armTimer;
    private float _detonateTimer = DetonateDelay;
    private int _disguiseVariant;

    // --- Sticky ---
    private bool _stickied;
    private float _stickiedAngle;
    private ObjectData _stickiedObject;
    private Vector2 _stickiedOffset = Vector2.Zero;
    private float _normalAngle;
    private Filter _originalFilter;
    private bool _filterApplied;

    // --- Blinking ---
    private bool _blink;
    private float _blinkInterval = 120f;
    private float _blinkTimer;

    // --- Textures ---
    private Texture2D _disguiseTexture;
    private Texture2D _armingTexture;

    /// <summary>
    /// The supply crate texture names this mine can disguise as.
    /// These are the slightly hue-shifted mimic versions.
    /// </summary>
    private static readonly string[] DisguiseTextures =
    [
        "MimicMineRandom",
        "MimicMineMedic",
        "MimicMineMelee",
        "MimicMinePrimary",
        "MimicMineSecondary",
        "MimicMineSupplies"
    ];

    internal ObjectMimicMineThrown(ObjectDataStartParams startParams) : base(startParams)
    {
    }

    public override void Initialize()
    {
        EnableUpdateObject();
        GameWorld.PortalsObjectsToKeepTrackOf.Add(this);
        Body.SetBullet(true);
        FaceDirection = 1;

        // Pick a random supply crate disguise
        _disguiseVariant = Globals.Random.Next(0, DisguiseTextures.Length);
        _disguiseTexture = Textures.GetTexture(DisguiseTextures[_disguiseVariant]);
        _armingTexture = Textures.GetTexture("MimicMineM");
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
        if (GameOwner != GameOwnerEnum.Client && projectile.Properties.ProjectileID != 64 && projectile is not IExtendedProjectile)
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
                    _state = MimicState.Arming;
                    _armTimer = ArmDuration;
                    _blink = false;
                    _blinkTimer = 0f;
                    break;
                case 2:
                    _state = MimicState.Disguised;
                    _blink = false;
                    break;
                case 3:
                    _state = MimicState.Detonating;
                    _detonateTimer = DetonateDelay;
                    _blinkInterval = 15f;
                    break;
                case -1:
                    _state = MimicState.Dud;
                    break;
            }
        }
    }

    public override void UpdateObject(float ms)
    {
        switch (_state)
        {
            case MimicState.Airborne:
                break;
            case MimicState.Arming:
                UpdateArming(ms);
                break;
            case MimicState.Disguised:
                break;
            case MimicState.Detonating:
                UpdateDetonating(ms);
                break;
        }

        // Sticky tracking (while attached to an object)
        if (_stickied && _state is MimicState.Arming or MimicState.Disguised)
        {
            UpdateStickyTracking();
        }

        // Blinking during arming
        if (_state == MimicState.Arming)
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

        // Rapid blinking during detonation
        if (_state == MimicState.Detonating)
        {
            _blinkTimer -= ms;
            if (_blinkTimer <= 0f)
            {
                _blink = !_blink;
                _blinkTimer += _blinkInterval;
            }
        }
    }

    private void UpdateArming(float ms)
    {
        if (GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

        _armTimer -= ms;
        if (_armTimer <= 0f)
        {
            Properties.Get(ObjectPropertyID.Mine_Status).Value = 2;

            // Enable interact prompt so players think it's a pickup
            Activateable = true;
            ActivateableHighlightning = true;
            ActivateRange = 14f;
        }
    }

    /// <summary>
    /// Called when a player presses the interact button on this object.
    /// Triggers the detonation sequence.
    /// </summary>
    public override void Activate(ObjectData sender)
    {
        if (_state == MimicState.Disguised && GameOwner != GameOwnerEnum.Client)
        {
            Properties.Get(ObjectPropertyID.Mine_Status).Value = 3;
            SoundHandler.PlaySound("MineTrigger", GameWorld);
        }
    }

    private void UpdateDetonating(float ms)
    {
        if (GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

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
            _ = GameWorld.TriggerExplosion(GetWorldPosition(), 80f);
            SoundHandler.PlaySound("Explosion", GetWorldPosition(), GameWorld);
            EffectHandler.PlayEffect("EXP", GetWorldPosition(), GameWorld);
            EffectHandler.PlayEffect("CAM_S", GetWorldPosition(), GameWorld, 8f, 250f, false);
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

        if (!_stickied && otherObject is { RemovalInitiated: false, IsPlayer: false })
        {
            ChangeBodyType(BodyType.Static);
            _stickiedObject = otherObject;
            _stickied = true;
            _stickiedOffset = GetWorldPosition() - otherObject.GetWorldPosition();
            _stickiedAngle = otherObject.GetAngle();
            _normalAngle = (float)Math.Atan2(e.WorldNormal.Y, e.WorldNormal.X);

            // Start arming when we stick to something
            if (GameOwner != GameOwnerEnum.Client && _state == MimicState.Airborne)
            {
                Properties.Get(ObjectPropertyID.Mine_Status).Value = 1;
            }
        }
    }

    public override void Draw(SpriteBatch spriteBatch, float ms)
    {
        Texture2D texture;

        switch (_state)
        {
            case MimicState.Disguised:
                // Show the supply crate disguise
                texture = _disguiseTexture;
                break;
            case MimicState.Detonating:
                // Flash between disguise and white during detonation
                texture = _blink ? _armingTexture : _disguiseTexture;
                break;
            default:
                // During airborne/arming, show the basic mine texture
                texture = _blink ? _armingTexture : _armingTexture;
                break;
        }

        Vector2 vector = Body.Position;
        vector += GameWorld.DrawingBox2DSimulationTimestepOver * Body.GetLinearVelocity();
        Camera.ConvertBox2DToScreen(ref vector, out vector);

        // When disguised, draw upright like a real supply crate (angle = 0)
        float drawAngle = _state == MimicState.Disguised ? 0f : GetAngle();

        spriteBatch.Draw(texture, vector, null, Color.White, drawAngle,
            new Vector2(texture.Width / 2, texture.Height / 2),
            Camera.ZoomUpscaled, m_faceDirectionSpriteEffect, 0f);
    }

    private enum MimicState
    {
        Airborne,
        Arming,
        Disguised,
        Detonating,
        Dud
    }
}
