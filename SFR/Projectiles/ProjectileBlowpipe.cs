using System;
using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Materials;
using SFD.Objects;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.Tiles;
using SFR.Fighter;
using SFR.Helper;

namespace SFR.Projectiles;

internal sealed class ProjectileBlowpipe : Projectile, IExtendedProjectile
{
    private float _gravity;
    private float _velocity;

    internal ProjectileBlowpipe()
    {
        Visuals = new ProjectileVisuals(Textures.GetTexture("ProjectileBlowpipe"), Textures.GetTexture("ProjectileBlowpipe"));
        Properties = new ProjectileProperties(114, 500f, 10f, 4f, 4f, 0f, 4f, 6f, 0.3f)
        {
            PowerupBounceRandomAngle = 0f,
            PowerupFireType = ProjectilePowerupFireType.Default,
            PowerupTotalBounces = 4,
            PowerupFireIgniteValue = 4f
        };
    }

    private ProjectileBlowpipe(ProjectileProperties projectileProperties, ProjectileVisuals projectileVisuals) : base(projectileProperties, projectileVisuals)
    {
    }

    public override float SlowmotionFactor => 1f - (1f - GameWorld.SlowmotionHandler.SlowmotionModifier) * 0.5f;

    public bool OnHit(Projectile projectile, ProjectileHitEventArgs e, ObjectData objectData) => true;

    public bool OnExplosiveHit(Projectile projectile, ProjectileHitEventArgs e, ObjectExplosive objectData)
    {
        ObjectDataMethods.ApplyProjectileHitImpulse(objectData, projectile, e);
        return false;
    }

    public bool OnExplosiveBarrelHit(Projectile projectile, ProjectileHitEventArgs e, ObjectBarrelExplosive objectData)
    {
        ObjectDataMethods.ApplyProjectileHitImpulse(objectData, projectile, e);
        return false;
    }

    public override Projectile Copy()
    {
        ProjectileBlowpipe projectile = new(Properties, Visuals);
        projectile.CopyBaseValuesFrom(this);
        return projectile;
    }

    public override void Update(float ms)
    {
        _velocity += ms;
        float scaleFactor = Math.Min(_velocity / 400f, 1f);
        Velocity -= Vector2.UnitY * ms * 0.8f * scaleFactor;
        if (GameOwner != GameOwnerEnum.Server)
        {
            _gravity -= ms;
            if (_gravity <= 0f)
            {
                if (Constants.EFFECT_LEVEL_FULL)
                {
                    EffectHandler.PlayEffect("TR_S", Position, GameWorld);
                }

                _gravity = Constants.EFFECT_LEVEL_FULL ? 15f : 30f;
            }
        }
    }

    public override void HitPlayer(Player player, ObjectData playerObjectData)
    {
        if (GameOwner != GameOwnerEnum.Client)
        {
            player.TakeProjectileDamage(this);

            // Apply poison effect for 5 seconds
            ExtendedPlayer extendedPlayer = player.GetExtension();
            extendedPlayer.ApplyPoison();

            Material material = player.GetPlayerHitMaterial() ?? playerObjectData.Tile.Material;
            SoundHandler.PlaySound(material.Hit.Projectile.HitSound, GameWorld);
            EffectHandler.PlayEffect(material.Hit.Projectile.HitEffect, Position, GameWorld);
        }
    }
}
