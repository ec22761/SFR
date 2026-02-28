using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.Tiles;
using Player = SFD.Player;

namespace SFR.Projectiles;

internal sealed class ProjectileCannon : ProjectileBazooka
{
    private const float _explosionValue = 55;
    private const int _maxPenetrations = 5;
    private float _effectTimer;
    private int _penetrations;
    private float _gravity;

    internal ProjectileCannon()
    {
        Visuals = new ProjectileVisuals(Textures.GetTexture("CannonBall00"), Textures.GetTexture("CannonBall00"));
        Properties = new ProjectileProperties(113, 300f, 0f, 20f, 20f, 0f, 0f, 25f, 0.5f)
        {
            DodgeChance = 0f,
            CanBeAbsorbedOrBlocked = false,
            PowerupTotalBounces = 3,
            PowerupBounceRandomAngle = 0f,
            PowerupFireType = ProjectilePowerupFireType.Fireplosion,
            PowerupFireIgniteValue = 56f
        };
    }

    private ProjectileCannon(ProjectileProperties projectileProperties, ProjectileVisuals projectileVisuals) : base(projectileProperties, projectileVisuals)
    {
    }

    public override Projectile Copy()
    {
        Projectile projectile = new ProjectileCannon(Properties, Visuals);
        projectile.CopyBaseValuesFrom(this);
        return projectile;
    }

    public override void HitPlayer(Player player, ObjectData playerObjectData)
    {
        if (player.GameOwner != GameOwnerEnum.Client)
        {
            _ = GameWorld.TriggerExplosion(Position, _explosionValue, true);
            HitFlag = true;
            GameWorld.RemovedProjectiles.Add(this);
        }
    }

    public override void Update(float ms)
    {
        // Sharper gravity falloff — cannonball arcs down quickly
        _gravity += ms;
        float gravityScale = System.Math.Min(_gravity / 300f, 1f);
        Velocity -= Vector2.UnitY * ms * 0.8f * gravityScale;

        if (GameWorld.GameOwner != GameOwnerEnum.Server)
        {
            _effectTimer -= ms;
            if (_effectTimer < 0)
            {
                EffectHandler.PlayEffect("TR_S", Position, GameWorld, Direction.X, Direction.Y);
                _effectTimer = Constants.EFFECT_LEVEL_FULL ? 10f : 20f;
            }
        }
    }

    public override void HitObject(ObjectData objectData, ProjectileHitEventArgs e)
    {
        if (!ProjectileGrenadeLauncher.SpecialIgnoreObjectsForExplosiveProjectiles(objectData))
        {
            if (GameOwner != GameOwnerEnum.Client)
            {
                if (_penetrations < _maxPenetrations)
                {
                    // Explode but keep going — smash through the wall
                    _penetrations++;
                    _ = GameWorld.TriggerExplosion(Position, _explosionValue, true);
                    SoundHandler.PlaySound("DestroyWood", Position, GameWorld);
                    EffectHandler.PlayEffect("EXP", Position, GameWorld);

                    // Don't remove the projectile — let it keep flying
                    e.CustomHandled = true;
                    e.ReflectionStatus = ProjectileReflectionStatus.None;
                    HitFlag = false;
                }
                else
                {
                    // Final impact — explode and die
                    if (e.ReflectionStatus != ProjectileReflectionStatus.WillBeReflected)
                    {
                        _ = GameWorld.TriggerExplosion(Position - Direction * 2, _explosionValue, true);
                    }

                    HitFlag = true;
                }
            }

            if (GameOwner == GameOwnerEnum.Client)
            {
                base.HitObject(objectData, e);
            }
        }
        else
        {
            e.CustomHandled = true;
            e.ReflectionStatus = ProjectileReflectionStatus.None;
            HitFlag = false;
        }
    }
}
