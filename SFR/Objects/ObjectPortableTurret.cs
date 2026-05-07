using System;
using System.IO;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Effects;
using SFD.Materials;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.Tiles;
using SFDGameScriptInterface;
using SFR.Helper;
using SFR.Misc;
using Player = SFD.Player;
using Vector2 = Microsoft.Xna.Framework.Vector2;
using MathHelper = Microsoft.Xna.Framework.MathHelper;
using Color = Microsoft.Xna.Framework.Color;

namespace SFR.Objects;

/// <summary>
/// Deployed sentry turret. Pinned in place via a static body, but otherwise
/// behaves like a normal hittable/breakable object. Tracks the closest enemy
/// and fires periodically when it has line-of-sight (no walls in the way).
/// </summary>
internal sealed class ObjectPortableTurret : ObjectData
{
    private const float ScanRange = 220f;
    private const float FireCooldown = 1300f;
    private const float BurstShotInterval = 80f;
    private const int BurstRoundCount = 3;
    private const float BurstSpread = 0.08f;
    private const float ProjectileDamageScale = 0.2f;
    private const float ProjectileForceScale = 0.1f;
    private const float MaxHealth = 80f;
    private const float AimLerp = 0.18f;
    private const float MuzzleHeight = 4f;
    private const float BarrelVisualOffsetX = 1f;
    private const float BarrelVisualHeight = 1f;
    private const float StandingTargetHeight = 14f;
    private const float CrouchingTargetHeight = 9f;
    private static readonly Color BarrelTint = new(90, 90, 90);

    private int _ownerObjectId;
    private short _facing = 1;
    private float _fireTimer;
    private float _burstTimer;
    private float _barrelAngle;
    private float _health = MaxHealth;
    private int _burstShotsRemaining;

    private Texture2D _barrelTexture;

    internal ObjectPortableTurret(ObjectDataStartParams startParams) : base(startParams) { }

    internal void Configure(int ownerObjectId, short facing)
    {
        _ownerObjectId = ownerObjectId;
        _facing = facing == 0 ? (short)1 : facing;
    }

    public override void Initialize()
    {
        EnableUpdateObject();
        SettleBodyOnSpawn();

        // Let the engine draw the base sprite from the tile's tileTexture.
        // Load the barrel directly so the SFD texture registry cannot return
        // the white fallback texture when this loose overlay art is missing.
        _barrelTexture = LoadBarrelTexture();
        _barrelAngle = _facing >= 0 ? 0f : MathHelper.Pi;
    }

    private static Texture2D LoadBarrelTexture()
    {
        string barrelTexturePath = Path.Combine(SFR.Program.GameDirectory, @"SFR\Content\Data\Images\Objects\PortableTurretBarrel.png");
        if (!File.Exists(barrelTexturePath))
        {
            return Textures.GetTexture("PortableTurretBarrel");
        }

        using FileStream stream = File.OpenRead(barrelTexturePath);
        return Texture2D.FromStream(GameSFD.Handle.GraphicsDevice, stream);
    }

    public override void UpdateObject(float ms)
    {
        if (GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

        if (_health <= 0f || Health != null && Health.Fullness <= 0f)
        {
            Detonate();
            return;
        }

        Player target = FindClosestEnemy();
        if (target != null)
        {
            Vector2 to = GetTargetAimPosition(target) - GetMuzzlePosition();
            float desired = (float)Math.Atan2(to.Y, to.X);
            _barrelAngle = SmoothAngle(_barrelAngle, desired, AimLerp);

            _fireTimer -= ms;
            if (!HasLineOfSight(target))
            {
                _burstShotsRemaining = 0;
                _burstTimer = 0f;
                return;
            }

            if (_burstShotsRemaining == 0 && _fireTimer <= 0f)
            {
                _burstShotsRemaining = BurstRoundCount;
                _burstTimer = 0f;
            }

            if (_burstShotsRemaining > 0)
            {
                _burstTimer -= ms;
                if (_burstTimer <= 0f)
                {
                    FireRound();
                    _burstShotsRemaining--;
                    _burstTimer = BurstShotInterval;

                    if (_burstShotsRemaining == 0)
                    {
                        _fireTimer = FireCooldown;
                    }
                }
            }
        }
        else
        {
            _fireTimer = 0f;
            _burstTimer = 0f;
            _burstShotsRemaining = 0;
        }
    }

    public override void ProjectileHit(Projectile projectile, ProjectileHitEventArgs e)
    {
        if (GameOwner != GameOwnerEnum.Client && projectile != null)
        {
            base.ProjectileHit(projectile, e);
            _health -= Math.Max(4f, projectile.HitDamageValue);
        }
    }

    public override void ExplosionHit(SFD.Explosion explosionData, ExplosionHitEventArgs e)
    {
        if (GameOwner != GameOwnerEnum.Client)
        {
            base.ExplosionHit(explosionData, e);
            _health -= explosionData.SourceExplosionDamage;
        }
    }

    private void Detonate()
    {
        EffectHandler.PlayEffect("EXP", GetWorldPosition(), GameWorld);
        SoundHandler.PlaySound("Explosion", GetWorldPosition(), GameWorld);
        Destroy();
    }

    private Player FindClosestEnemy()
    {
        Player owner = GameWorld.GetPlayer(_ownerObjectId);
        Team ownerTeam = owner != null ? owner.m_currentTeam : Team.Independent;

        Player closest = null;
        float closestSq = ScanRange * ScanRange;
        Vector2 myPos = GetWorldPosition();

        foreach (Player p in GameWorld.Players)
        {
            if (p == null || p.IsDead || p.IsRemoved || p.m_isDisposed)
            {
                continue;
            }

            if (p.ObjectID == _ownerObjectId)
            {
                continue;
            }

            if (ownerTeam != Team.Independent && p.m_currentTeam == ownerTeam)
            {
                continue;
            }

            float distSq = Vector2.DistanceSquared(myPos, p.Position);
            if (distSq < closestSq)
            {
                closestSq = distSq;
                closest = p;
            }
        }

        return closest;
    }

    /// <summary>
    /// Casts a ray from the muzzle to the target. Returns false if any
    /// non-transparent map geometry blocks the line.
    /// </summary>
    private bool HasLineOfSight(Player target)
    {
        Vector2 from = GetMuzzlePosition();
        Vector2 to = GetTargetAimPosition(target);
        Vector2 dir = to - from;
        float dist = dir.Length();
        if (dist < 0.001f)
        {
            return true;
        }
        dir /= dist;

        GameWorld.RayCastResult hit = GameWorld.RayCast(from, dir, 0f, dist, LosBlocker, player => player.ObjectID == target.ObjectID);
        return hit.EndFixture == null || IsTargetFixture(hit.EndFixture, target);
    }

    private static bool IsTargetFixture(Fixture fixture, Player target)
    {
        ObjectData od = Read(fixture);
        return od != null && od.IsPlayer && od.ObjectID == target.ObjectID;
    }

    private bool LosBlocker(Fixture fixture)
    {
        if (fixture == null || fixture.IsCloud())
        {
            return false;
        }

        ObjectData od = Read(fixture);
        if (od == null || od == this || od.IsPlayer)
        {
            return false;
        }

        if (od.Destructable && od.DoTakeProjectileDamage)
        {
            return false;
        }

        fixture.GetFilterData(out Filter filter);
        if ((filter.categoryBits & 15) == 0)
        {
            return false;
        }

        Material mat = od.Tile?.GetTileFixtureMaterial(fixture.TileFixtureIndex);
        if (mat != null && mat.Transparent)
        {
            return false;
        }

        return true;
    }

    private void SettleBodyOnSpawn()
    {
        if (Body == null)
        {
            return;
        }

        Body.SetLinearVelocity(Vector2.Zero);
        Body.SetAngularVelocity(0f);
        Body.SetTransform(Body.Position, 0f);
    }

    private void FireRound()
    {
        float shotAngle = _barrelAngle + Globals.Random.NextFloat(-BurstSpread, BurstSpread);
        Vector2 dir = new((float)Math.Cos(shotAngle), (float)Math.Sin(shotAngle));
        Vector2 muzzle = GetMuzzlePosition() + dir * 10f;
        Projectile projectile = GameWorld.SpawnProjectile(1, muzzle, dir, ObjectID);
        if (projectile != null)
        {
            ScaleProjectileDamage(projectile);
        }
        EffectHandler.PlayEffect("MZ", muzzle, GameWorld);
        SoundHandler.PlaySound("Pistol", muzzle, GameWorld);
    }

    private static void ScaleProjectileDamage(Projectile projectile)
    {
        SFD.Projectiles.ProjectileProperties properties = projectile.Properties;
        projectile.Properties = new SFD.Projectiles.ProjectileProperties(
            properties.ProjectileID,
            properties.InitialSpeed,
            properties.Strength * ProjectileDamageScale,
            properties.PlayerDamage * ProjectileDamageScale,
            properties.ObjectDamage * ProjectileDamageScale,
            properties.CritChance,
            properties.CritDamage * ProjectileDamageScale,
            properties.PlayerForce * ProjectileForceScale,
            properties.ObjectForce / 0.04f * ProjectileForceScale)
        {
            DodgeChance = properties.DodgeChance,
            CanBeAbsorbedOrBlocked = properties.CanBeAbsorbedOrBlocked,
            PowerupTotalBounces = properties.PowerupTotalBounces,
            PowerupBounceRandomAngle = properties.PowerupBounceRandomAngle,
            PowerupFireIgniteValue = properties.PowerupFireIgniteValue,
            PowerupFireType = properties.PowerupFireType
        };
        projectile.StrengthLeft *= ProjectileDamageScale;
    }

    private Vector2 GetMuzzlePosition()
    {
        return GetWorldPosition() + new Vector2(0f, MuzzleHeight);
    }

    private static Vector2 GetTargetAimPosition(Player target)
    {
        float height = target.Crouching || target.LayingOnGround ? CrouchingTargetHeight : StandingTargetHeight;
        return target.Position + new Vector2(0f, height);
    }

    private static float SmoothAngle(float current, float target, float t)
    {
        float diff = MathHelper.WrapAngle(target - current);
        return current + diff * t;
    }

    public override void Draw(SpriteBatch spriteBatch, float ms)
    {
        // Engine draws the base via the tile's tileTexture.
        base.Draw(spriteBatch, ms);

        if (_barrelTexture == null || Body == null)
        {
            return;
        }

        // Overlay the rotating barrel. Use the body's interpolated draw position
        // (matching how the engine draws the base) so the barrel stays glued on.
        Vector2 pivot = Body.Position + GameWorld.DrawingBox2DSimulationTimestepOver * Body.GetLinearVelocity();
        pivot.X += Converter.WorldToBox2D(BarrelVisualOffsetX);
        pivot.Y += Converter.WorldToBox2D(BarrelVisualHeight);
        Camera.ConvertBox2DToScreen(ref pivot, out pivot);
        spriteBatch.Draw(
            _barrelTexture,
            pivot,
            null,
            BarrelTint,
            -_barrelAngle,
            new Vector2(_barrelTexture.Width / 4f, _barrelTexture.Height / 2f),
            Camera.ZoomUpscaled,
            SpriteEffects.None,
            0f);
    }
}
