using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Sounds;
using Player = SFD.Player;

namespace SFR.Objects;

/// <summary>
/// Marker placed when the AirStrike is thrown. When the player triggers their detonator,
/// this object spawns the strike plane and removes itself.
/// </summary>
internal sealed class ObjectAirStrikeThrown : ObjectData
{
    internal ObjectAirStrikeThrown(ObjectDataStartParams startParams) : base(startParams) { }

    public override void Initialize()
    {
        if (GameOwner != GameOwnerEnum.Server && !GameWorld.EditMode)
        {
            SoundHandler.PlaySound("C4Throw", GameWorld);
        }
    }

    public override void OnDestroyObject()
    {
        if (GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("C4Detonate", GameWorld);
        }
    }

    /// <summary>
    /// Triggered by the matching <see cref="SFR.Weapons.Thrown.AirStrikeDetonator"/>.
    /// Spawns the strike plane above the marker and removes the marker.
    /// </summary>
    /// <returns>True if the strike was actually launched.</returns>
    internal bool Trigger(Player triggeringPlayer)
    {
        if (RemovalInitiated || GameOwner == GameOwnerEnum.Client)
        {
            return false;
        }

        Vector2 targetPos = GetWorldPosition();
        AirStrikeHandler.LaunchStrike(GameWorld, targetPos, triggeringPlayer);
        EffectHandler.PlayEffect("EXP", targetPos, GameWorld);
        Destroy();
        return true;
    }
}
