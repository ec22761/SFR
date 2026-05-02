using Microsoft.Xna.Framework;
using SFD;
using SFD.Sounds;
using SFD.Tiles;
using SFD.Weapons;
using SFDGameScriptInterface;
using SFR.Objects;
using Player = SFD.Player;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace SFR.Weapons.Others;

internal sealed class PortableTurret : PItem
{
    internal PortableTurret()
    {
        PItemProperties itemProperties = new(122, "PortableTurret", "ItemPortableTurret", false, WeaponCategory.Supply)
        {
            PickupSoundID = "GetSlomo",
            ActivateSoundID = "",
            VisualText = "Portable Turret"
        };

        PItemVisuals visuals = new(Textures.GetTexture("PortableTurret"), Textures.GetTexture("PortableTurret"));

        SetPropertiesAndVisuals(itemProperties, visuals);
    }

    private PortableTurret(PItemProperties properties, PItemVisuals visuals) => SetPropertiesAndVisuals(properties, visuals);

    public override void OnActivation(Player player, PItem powerupItem)
    {
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

        // Spawn the turret one tile in front of the player at foot level.
        float facing = player.LastDirectionX >= 0 ? 1f : -1f;
        Vector2 spawnPos = player.Position + new Vector2(facing * 8f, 4f);

        ObjectPortableTurret turret = (ObjectPortableTurret)player.GameWorld.IDCounter.NextObjectData("PortableTurretDeployed");
        turret.Configure(player.ObjectID, facing >= 0 ? (short)1 : (short)-1);
        _ = player.GameWorld.CreateTile(new SpawnObjectInformation(turret, spawnPos, 0f, 1, Vector2.Zero, 0f));

        SoundHandler.PlaySound("Syringe", player.Position, player.GameWorld);

        if (!player.InfiniteAmmo)
        {
            player.RemovePowerup();
        }
    }

    public override PItem Copy() => new PortableTurret(Properties, Visuals);
}
