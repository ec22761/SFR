using System;
using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Materials;
using SFD.Objects;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.Tiles;
using SFR.Helper;
using SFR.Misc;

namespace SFR.Projectiles;

/// <summary>
/// Rivet Gun projectile — a heavier nail that arcs slightly due to gravity.
/// On hitting a surface (wall, floor, object), it bursts into shrapnel fragments
/// that damage nearby players. Direct player hits deal normal projectile damage.
/// </summary>
internal sealed class ProjectileRivetGun : Projectile, IExtendedProjectile
{
    private const int ShrapnelMin = 5;
    private const int ShrapnelMax = 8;
    private const float ShrapnelExplosionPower = 8f;

    private float _gravity;
    private float _velocity;

    internal ProjectileRivetGun()
    {
        Visuals = new ProjectileVisuals(Textures.GetTexture("ProjectileNailgun"), Textures.GetTexture("ProjectileNailgun"));
        Properties = new ProjectileProperties(116, 550f, 15f, 12f, 10f, 0.12f, 15f, 12f, 0.2f)
        {
            PowerupBounceRandomAngle = 0f,
            PowerupFireType = ProjectilePowerupFireType.Default,
            PowerupTotalBounces = 4,
            PowerupFireIgniteValue = 4f
        };
    }

    private ProjectileRivetGun(ProjectileProperties projectileProperties, ProjectileVisuals projectileVisuals) : base(projectileProperties, projectileVisuals)
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
        ProjectileRivetGun projectile = new(Properties, Visuals);
        projectile.CopyBaseValuesFrom(this);
        return projectile;
    }

    public override void Update(float ms)
    {
        _velocity += ms;
        float scaleFactor = Math.Min(_velocity / 500f, 1f);
        Velocity -= Vector2.UnitY * ms * 0.5f * scaleFactor;

        if (GameOwner != GameOwnerEnum.Server && PowerupFireActive)
        {
            _gravity -= ms;
            if (_gravity <= 0f)
            {
                if (Constants.EFFECT_LEVEL_FULL)
                {
                    EffectHandler.PlayEffect("TR_S", Position, GameWorld);
                }

                EffectHandler.PlayEffect("TR_F", Position, GameWorld);
                _gravity = Constants.EFFECT_LEVEL_FULL ? 10f : 20f;
            }
        }
    }

    public override void HitPlayer(Player player, ObjectData playerObjectData)
    {
        if (GameOwner != GameOwnerEnum.Client)
        {
            player.TakeProjectileDamage(this);
            Material material = player.GetPlayerHitMaterial() ?? playerObjectData.Tile.Material;
            SoundHandler.PlaySound(material.Hit.Projectile.HitSound, GameWorld);
            EffectHandler.PlayEffect(material.Hit.Projectile.HitEffect, Position, GameWorld);
            SoundHandler.PlaySound("MeleeHitSharp", GameWorld);
        }
    }

    public override void HitObject(ObjectData objectData, ProjectileHitEventArgs e)
    {
        base.HitObject(objectData, e);

        if (HitFlag && GameOwner != GameOwnerEnum.Client && e.ReflectionStatus != ProjectileReflectionStatus.WillBeReflected
            && objectData is not ObjectExplosive and not ObjectBarrelExplosive)
        {
            // Spawn shrapnel fragments in random directions (skip on barrels/explosives)
            int fragmentCount = Globals.Random.Next(ShrapnelMin, ShrapnelMax + 1);
            for (int i = 0; i < fragmentCount; i++)
            {
                float angle = Globals.Random.NextFloat(0f, (float)(Math.PI * 2));
                Vector2 direction = new((float)Math.Cos(angle), (float)Math.Sin(angle));
                _ = GameWorld.SpawnProjectile(10, Position, direction * 5f, 0);
            }
        }

        if (HitFlag && GameOwner != GameOwnerEnum.Server)
        {
            EffectHandler.PlayEffect("S_P", Position, GameWorld);
            SoundHandler.PlaySound("ImpactMetal", Position, GameWorld);
        }
    }
}
