using SFD;
using SFD.Objects;
using SFD.Projectiles;
using SFD.Tiles;

namespace SFR.Projectiles;

/// <summary>
/// Rivet Gun shrapnel fragment — a small projectile that deals minor damage
/// but does NOT trigger explosive barrels or other explosives.
/// </summary>
internal sealed class ProjectileRivetShrapnel : Projectile, IExtendedProjectile
{
    internal ProjectileRivetShrapnel()
    {
        Visuals = new ProjectileVisuals(Textures.GetTexture("ProjectileNailgun"), Textures.GetTexture("ProjectileNailgun"));
        Properties = new ProjectileProperties(120, 150f, 8f, 4f, 3f, 0.3f, 5f, 4f, 0.1f)
        {
            PowerupBounceRandomAngle = 0f,
            PowerupFireType = ProjectilePowerupFireType.Default,
            PowerupTotalBounces = 0,
            PowerupFireIgniteValue = 0f
        };
    }

    private ProjectileRivetShrapnel(ProjectileProperties projectileProperties, ProjectileVisuals projectileVisuals) : base(projectileProperties, projectileVisuals)
    {
    }

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
        ProjectileRivetShrapnel projectile = new(Properties, Visuals);
        projectile.CopyBaseValuesFrom(this);
        return projectile;
    }
}
