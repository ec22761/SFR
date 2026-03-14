using Box2D.XNA;
using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Objects;
using SFD.Sounds;
using SFD.Tiles;
using SFD.Weapons;
using SFDGameScriptInterface;
using Player = SFD.Player;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace SFR.Weapons.Others;

internal sealed class Defib : PItem
{
    private const float DefibRange = 50f;

    internal Defib()
    {
        PItemProperties itemProperties = new(118, "Defibrillator", "ItemDefib", false, WeaponCategory.Supply)
        {
            PickupSoundID = "GetSlomo",
            ActivateSoundID = "",
            VisualText = "Defibrillator"
        };

        PItemVisuals visuals = new(Textures.GetTexture("Defib"), Textures.GetTexture("Defib"));

        SetPropertiesAndVisuals(itemProperties, visuals);
    }

    private Defib(PItemProperties itemProperties, PItemVisuals itemVisuals) => SetPropertiesAndVisuals(itemProperties, itemVisuals);

    public override void OnActivation(Player player, PItem powerupItem)
    {
        // Only start the animation if there's a valid dead player in range
        Player target = FindClosestDeadPlayer(player);
        if (target == null)
        {
            return;
        }

        if (player.StrengthBoostPrepare())
        {
            SoundHandler.PlaySound(powerupItem.Properties.ActivateSoundID, player.Position, player.GameWorld);
        }
    }

    internal void OnEffectStart(Player player)
    {
        if (player.GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

        Player target = FindClosestDeadPlayer(player);
        if (target == null)
        {
            return;
        }

        RevivePlayer(target);

        SoundHandler.PlaySound("Syringe", player.Position, player.GameWorld);
        EffectHandler.PlayEffect("S_P", target.Position, player.GameWorld);
        EffectHandler.PlayEffect("S_P", target.Position + new Vector2(0, 8f), player.GameWorld);
        EffectHandler.PlayEffect("CAM_S", Vector2.Zero, player.GameWorld, 0.5f, 150f, false);

        if (!player.InfiniteAmmo)
        {
            player.RemovePowerup();
        }
    }

    private static Player FindClosestDeadPlayer(Player user)
    {
        AABB.Create(out AABB area, user.Position, user.Position, DefibRange);
        Player closest = null;
        float closestDist = float.MaxValue;

        foreach (ObjectData obj in user.GameWorld.GetObjectDataByArea(area, false, PhysicsLayer.Active))
        {
            if (obj.InternalData is Player target && target != user && target.IsDead && !target.IsRemoved && !target.m_isDisposed)
            {
                float dist = Vector2.DistanceSquared(user.Position, target.Position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = target;
                }
            }
        }

        return closest;
    }

    private static void RevivePlayer(Player target)
    {
        // Reset death state
        target.IsDead = false;
        target.DeadTime = 0f;
        target.DeathKneeling = false;
        target.CancelDeathKneel();

        // Reset state flags
        target.m_states[11] = false; // Dead
        target.m_states[15] = false; // Removed

        // Set health to half
        target.SetNewHealth(50f);

        // Re-enable player
        target.CurrentAction = PlayerAction.Idle;
        target.SetInputEnabled(true);
        target.EnableRectFixture();
    }

    public override PItem Copy() => new Defib(Properties, Visuals);
}
