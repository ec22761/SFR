using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Effects;
using SFD.Materials;
using SFR.Helper;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.Tiles;
using SFR.Misc;
using SFR.Projectiles;
using Explosion = SFD.Explosion;
using Player = SFD.Player;

namespace SFR.Objects;

/// <summary>
/// Mimic Mine — a thrown mine that looks like a supply crate.
/// Once it lands and arms itself, it disguises as a randomly chosen
/// supply crate variant with a very subtle hue shift.
/// When a player kicks, punches, or shoots it, it explodes.
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

    // --- Landing ---
    private bool _landed;

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
            if (_state == MimicState.Disguised)
            {
                TriggerDetonation();
            }
            else
            {
                Destroy();
            }
        }
    }

    public override void BeforePlayerMeleeHit(Player player, PlayerBeforeHitEventArgs e)
    {
    }

    public override void PlayerMeleeHit(Player player, PlayerHitEventArgs e)
    {
        ObjectDataMethods.DefaultPlayerHitBaseballEffect(this, player, e);

        if (_state == MimicState.Disguised && GameOwner != GameOwnerEnum.Client)
        {
            TriggerDetonation();
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
                    SwapDecal(_armingTexture);
                    break;
                case 2:
                    _state = MimicState.Disguised;
                    _blink = false;
                    SwapDecal(_disguiseTexture);
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

    private void SwapDecal(Texture2D texture)
    {
        ClearDecals();
        AddDecal(new ObjectDecal(texture));
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
        }
    }

    private void TriggerDetonation()
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
            }
            else
            {
                Destroy();
            }

            DisableUpdateObject();
        }
    }

    public override void OnDestroyObject()
    {
        if (GameOwner != GameOwnerEnum.Client)
        {
            _ = GameWorld.TriggerExplosion(GetWorldPosition(), 160f);
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

        // Start arming on first impact (like a grenade landing)
        if (!_landed && GameOwner != GameOwnerEnum.Client && _state == MimicState.Airborne)
        {
            _landed = true;
            Properties.Get(ObjectPropertyID.Mine_Status).Value = 1;
        }
    }

    public override void Draw(SpriteBatch spriteBatch, float ms)
    {
        // During detonation, flash between disguise and arming texture
        if (_state == MimicState.Detonating)
        {
            SwapDecal(_blink ? _armingTexture : _disguiseTexture);
        }

        DrawBase(spriteBatch, ms, new Color(0.5f, 0.5f, 0.5f, 1f));
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
