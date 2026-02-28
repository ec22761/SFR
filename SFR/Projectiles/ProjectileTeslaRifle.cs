using Box2D.XNA;
using Microsoft.Xna.Framework;
using SFD;
using SFD.Projectiles;
using SFD.Tiles;

namespace SFR.Projectiles;

/// <summary>
///     Tesla Rifle projectile — near-instant raycast beam hit.
///     Chains to the nearest secondary target within 60 world units for 40% damage.
///     Stores last beam and chain endpoints for visual rendering in TeslaRifle.DrawExtra.
/// </summary>
internal sealed class ProjectileTeslaRifle : Projectile
{
    /// <summary>
    ///     Shared state for beam rendering — position where the beam last hit something.
    /// </summary>
    internal static Vector2 LastBeamEndPosition;

    internal static float LastBeamTime;

    /// <summary>
    ///     Shared state for chain arc rendering — position of the secondary chain target.
    /// </summary>
    internal static Vector2 LastChainEndPosition;

    internal static float LastChainTime;

    private const float _chainRange = 60f;
    private const float _chainDamageMultiplier = 0.4f;
    private const float _baseDamage = 2f;

    internal ProjectileTeslaRifle()
    {
        Visuals = new ProjectileVisuals(
            Textures.GetTexture("BulletTeslaRifle"),
            Textures.GetTexture("BulletTeslaRifleSlowmo")
        );

        Properties = new ProjectileProperties(116, 999f, 5000f, 2f, 1.5f, 0f, 0f, 0f, 0f)
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

        // Chain arc — find nearest secondary target and deal reduced damage.
        if (player.GameOwner != GameOwnerEnum.Client)
        {
            Player closestTarget = null;
            float closestDist = float.MaxValue;

            AABB.Create(out AABB area, player.Position, player.Position, _chainRange);

            foreach (ObjectData obj in player.GameWorld.GetObjectDataByArea(area, false,
                         SFDGameScriptInterface.PhysicsLayer.Active))
            {
                if (obj.InternalData is Player candidate &&
                    candidate != player &&
                    !candidate.IsDead &&
                    !candidate.IsRemoved)
                {
                    float dist = Vector2.Distance(candidate.Position, player.Position);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestTarget = candidate;
                    }
                }
            }

            if (closestTarget is not null)
            {
                float chainDamage = _baseDamage * _chainDamageMultiplier;
                closestTarget.TakeMiscDamage(chainDamage);

                // Track the chain endpoint for visuals.
                LastChainEndPosition = closestTarget.Position;
                LastChainTime = player.GameWorld.ElapsedTotalGameTime;
            }
        }
    }

    public override void HitObject(ObjectData objectData, ProjectileHitEventArgs e)
    {
        base.HitObject(objectData, e);

        // Track the beam endpoint for visuals.
        LastBeamEndPosition = objectData.GetWorldPosition();
        LastBeamTime = objectData.GameWorld.ElapsedTotalGameTime;
    }
}
