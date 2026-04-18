using Microsoft.Xna.Framework;
using SFD;
using SFD.Projectiles;
using SFD.Tiles;

namespace SFR.Projectiles;

/// <summary>
///     Tesla Rifle projectile — near-instant raycast beam hit.
///     Stores last beam endpoints for visual rendering in TeslaRifle.DrawExtra.
/// </summary>
internal sealed class ProjectileTeslaRifle : Projectile
{
    /// <summary>
    ///     Shared state for beam rendering — position where the beam last hit something.
    /// </summary>
    internal static Vector2 LastBeamEndPosition;

    internal static float LastBeamTime;

    internal ProjectileTeslaRifle()
    {
        Visuals = new ProjectileVisuals(
            Textures.GetTexture("BulletTeslaRifle"),
            Textures.GetTexture("BulletTeslaRifleSlowmo")
        );

        Properties = new ProjectileProperties(114, 999f, 5000f, 1.0f, 1.0f, 0f, 0f, 0f, 0f)
        {
            PowerupBounceRandomAngle = 0f,
            PowerupFireType = ProjectilePowerupFireType.Default,
            PowerupFireIgniteValue = 0f,
            PowerupTotalBounces = 0,
            CanBeAbsorbedOrBlocked = true,
            DodgeChance = 0
        };
    }

    private ProjectileTeslaRifle(ProjectileProperties projectileProperties,
        ProjectileVisuals projectileVisuals) : base(projectileProperties, projectileVisuals)
    {
    }

    public override Projectile Copy()
    {
        ProjectileTeslaRifle copy = new(Properties, Visuals);
        copy.CopyBaseValuesFrom(this);
        return copy;
    }

    public override void HitPlayer(Player player, ObjectData playerObjectData)
    {
        base.HitPlayer(player, playerObjectData);

        // Track the beam endpoint for visuals.
        LastBeamEndPosition = player.Position;
        LastBeamTime = player.GameWorld.ElapsedTotalGameTime;
    }

    public override void HitObject(ObjectData objectData, ProjectileHitEventArgs e)
    {
        base.HitObject(objectData, e);

        // Track the beam endpoint for visuals.
        LastBeamEndPosition = objectData.GetWorldPosition();
        LastBeamTime = objectData.GameWorld.ElapsedTotalGameTime;
    }
}
