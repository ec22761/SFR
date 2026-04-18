using SFD;
using SFD.Sounds;
using SFD.Tiles;
using SFD.Weapons;
using SFR.Fighter;
using SFR.Helper;
using Player = SFD.Player;

namespace SFR.Weapons.Others;

internal class ShrinkBoost : PItem
{
    internal ShrinkBoost()
    {
        PItemProperties itemProperties = new(119, "ShrinkBoost", "ItemShrinkBoost", false, WeaponCategory.Supply)
        {
            PickupSoundID = "GetSlomo",
            ActivateSoundID = "",
            VisualText = "Shrink Boost"
        };

        PItemVisuals itemVisuals = new(Textures.GetTexture("ShrinkBoost"), Textures.GetTexture("ShrinkBoostD"));

        SetPropertiesAndVisuals(itemProperties, itemVisuals);
    }

    private ShrinkBoost(PItemProperties properties, PItemVisuals visuals) => SetPropertiesAndVisuals(properties, visuals);

    public override void OnActivation(Player player, PItem powerupItem)
    {
        if (player.StrengthBoostPrepare())
        {
            SoundHandler.PlaySound(powerupItem.Properties.ActivateSoundID, player.Position, player.GameWorld);
        }
    }

    internal void OnEffectStart(Player player)
    {
        if (player.GameOwner != GameOwnerEnum.Client)
        {
            SoundHandler.PlaySound("Syringe", player.Position, player.GameWorld);
            SoundHandler.PlaySound("StrengthBoostStart", player.Position, player.GameWorld);

            ExtendedPlayer extendedPlayer = player.GetExtension();
            extendedPlayer.ApplyShrinkBoost();
            if (!player.InfiniteAmmo)
            {
                player.RemovePowerup();
            }
        }
    }

    public override PItem Copy() => new ShrinkBoost(Properties, Visuals);
}
