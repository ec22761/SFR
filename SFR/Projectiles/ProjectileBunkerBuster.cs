using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.Tiles;
using Player = SFD.Player;

namespace SFR.Projectiles;

/// <summary>
/// Heavy bomb dropped from the AirStrike plane. Smashes through several layers of
/// terrain (like the cannonball) before its final big explosion.
/// </summary>
internal sealed class ProjectileBunkerBuster : ProjectileBazooka
{
    private const float _penetrationExplosion = 60f;
    private const float _finalExplosion = 130f;
    private const int _maxPenetrations = 6;
    private float _effectTimer;
    private int _penetrations;
    private float _gravity;

    internal ProjectileBunkerBuster()
    {
        Visuals = new ProjectileVisuals(Textures.GetTexture("BunkerBuster"), Textures.GetTexture("BunkerBuster"));
        Properties = new ProjectileProperties(115, 280f, 0f, 25f, 25f, 0f, 0f, 30f, 0.5f)
        {
            DodgeChance = 0f,
            CanBeAbsorbedOrBlocked = false,
            PowerupTotalBounces = 0,
            PowerupBounceRandomAngle = 0f,
            PowerupFireType = ProjectilePowerupFireType.Fireplosion,
            PowerupFireIgniteValue = 60f
        };
    }

    private ProjectileBunkerBuster(ProjectileProperties projectileProperties, ProjectileVisuals projectileVisuals)
        : base(projectileProperties, projectileVisuals) { }

    public override Projectile Copy()
    {
        Projectile projectile = new ProjectileBunkerBuster(Properties, Visuals);
        projectile.CopyBaseValuesFrom(this);
        return projectile;
    }

    public override void HitPlayer(Player player, ObjectData playerObjectData)
    {
        if (player.GameOwner != GameOwnerEnum.Client)
        {
            _ = GameWorld.TriggerExplosion(Position, _finalExplosion, true);
            HitFlag = true;
            GameWorld.RemovedProjectiles.Add(this);
        }
    }

    public override void Update(float ms)
    {
        // Strong gravity so the bomb falls almost straight down.
        _gravity += ms;
        float gravityScale = System.Math.Min(_gravity / 250f, 1f);
        Velocity -= Vector2.UnitY * ms * 1.0f * gravityScale;

        if (GameWorld.GameOwner != GameOwnerEnum.Server)
        {
            _effectTimer -= ms;
            if (_effectTimer < 0)
            {
                EffectHandler.PlayEffect("TR_S", Position, GameWorld, Direction.X, Direction.Y);
                _effectTimer = Constants.EFFECT_LEVEL_FULL ? 12f : 24f;
            }
        }
    }

    public override void HitObject(ObjectData objectData, ProjectileHitEventArgs e)
    {
        if (ProjectileGrenadeLauncher.SpecialIgnoreObjectsForExplosiveProjectiles(objectData))
        {
            e.CustomHandled = true;
            e.ReflectionStatus = ProjectileReflectionStatus.None;
            HitFlag = false;
            return;
        }

        if (GameOwner != GameOwnerEnum.Client)
        {
            // Don't explode when the projectile is being deflected. The deflect
            // callback runs inside Box2D's projectile-hit iteration; calling
            // TriggerExplosion here re-enters our explosion postfix, which can
            // synchronously CreateTile (RecreateFixture -> DynamicTree.CreateProxy)
            // while Box2D's tree is mid-update. That corrupts the tree and freezes
            // the game in DynamicTree.ComputeHeight recursion. Helicopter rotor
            // blades are ObjectProjectileDeflectZones, which is exactly this case.
            if (e.ReflectionStatus == ProjectileReflectionStatus.WillBeReflected)
            {
                // Let SFD finish the deflect; no explosion this hit.
            }
            else if (_penetrations < _maxPenetrations)
            {
                _penetrations++;
                _ = GameWorld.TriggerExplosion(Position, _penetrationExplosion, true);
                SoundHandler.PlaySound("DestroyWood", Position, GameWorld);
                EffectHandler.PlayEffect("EXP", Position, GameWorld);

                e.CustomHandled = true;
                e.ReflectionStatus = ProjectileReflectionStatus.None;
                HitFlag = false;
            }
            else
            {
                _ = GameWorld.TriggerExplosion(Position - Direction * 2, _finalExplosion, true);
                HitFlag = true;
            }
        }

        if (GameOwner == GameOwnerEnum.Client)
        {
            base.HitObject(objectData, e);
        }
    }
}
