using SFD;
using SFD.Sounds;
using SFD.Tiles;
using SFD.Weapons;
using SFR.Fighter;
using SFR.Helper;
using Player = SFD.Player;

namespace SFR.Weapons.Others;

internal class SpectrumAnalyzer : PItem
{
    internal SpectrumAnalyzer()
    {
        PItemProperties itemProperties = new(115, "SpectrumAnalyzer", "ItemSpectrumAnalyzer", false, WeaponCategory.Supply)
        {
            PickupSoundID = "GetSlomo",
            ActivateSoundID = "",
            VisualText = "Spectrum Analyzer"
        };

        PItemVisuals itemVisuals = new(Textures.GetTexture("SpectrumAnalyzer"), Textures.GetTexture("SpectrumAnalyzer"));

        SetPropertiesAndVisuals(itemProperties, itemVisuals);
    }

    private SpectrumAnalyzer(PItemProperties properties, PItemVisuals visuals) => SetPropertiesAndVisuals(properties, visuals);

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
            SoundHandler.PlaySound("GetSlomo", player.Position, player.GameWorld);

            ExtendedPlayer extendedPlayer = player.GetExtension();
            extendedPlayer.ApplySpectral();
            if (!player.InfiniteAmmo)
            {
                player.RemovePowerup();
            }
        }
    }

    public override PItem Copy() => new SpectrumAnalyzer(Properties, Visuals);
}
