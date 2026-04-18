using Microsoft.Xna.Framework;
using SFD;
using SFD.Objects;
using SFD.Sounds;
using Player = SFD.Player;

namespace SFR.Objects;

/// <summary>
/// Debris piece that damages players when it slams into them at sufficient speed.
/// Used to make falling/launched debris from destroyed environment lethal.
/// </summary>
internal sealed class ObjectCrushingDebris : ObjectData
{
    /// <summary>Box2D linear-velocity threshold below which a hit deals no damage.</summary>
    private const float MinSpeedForDamage = 4.5f;

    /// <summary>Damage per (m/s) of impact speed.</summary>
    private const float DamageScale = 1.4f;

    /// <summary>Hard cap on damage from a single debris hit.</summary>
    private const float MaxDamage = 40f;

    /// <summary>Per-instance cooldown to avoid stacking damage over many contacts in one frame.</summary>
    private const float CooldownMs = 250f;

    private float _hitCooldown;

    internal ObjectCrushingDebris(ObjectDataStartParams startParams) : base(startParams)
    {
    }

    public override void Initialize()
    {
        base.Initialize();
        EnableUpdateObject();
    }

    public override void UpdateObject(float ms)
    {
        if (_hitCooldown > 0f)
        {
            _hitCooldown -= ms;
        }
    }

    public override void MissileHitPlayer(Player player, MissileHitEventArgs e)
    {
        base.MissileHitPlayer(player, e);
        TryCrush(player);
    }

    public override void ImpactHit(ObjectData otherObject, ImpactHitEventArgs e)
    {
        base.ImpactHit(otherObject, e);
        if (otherObject != null && otherObject.IsPlayer && otherObject.InternalData is Player p)
        {
            TryCrush(p);
        }
    }

    private void TryCrush(Player player)
    {
        if (GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

        if (player == null || player.IsDead || player.IsRemoved || _hitCooldown > 0f || Body == null)
        {
            return;
        }

        Vector2 velocity = Body.GetLinearVelocity();
        float speed = velocity.Length();
        if (speed < MinSpeedForDamage)
        {
            return;
        }

        float damage = speed * DamageScale;
        if (damage > MaxDamage)
        {
            damage = MaxDamage;
        }

        player.TakeMiscDamage(damage, sourceID: ObjectID);
        player.SimulateFallWithSpeed(velocity * 0.5f + new Vector2(0f, 2f));
        SoundHandler.PlaySound("ImpactFlesh", player.Position, GameWorld);

        _hitCooldown = CooldownMs;
    }
}
