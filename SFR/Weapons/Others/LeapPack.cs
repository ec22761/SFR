using SFD;
using SFD.Sounds;
using SFD.Tiles;
using SFD.Weapons;
using SFR.Fighter;
using SFR.Fighter.Jetpacks;
using SFR.Helper;
using SFR.Sync.Generic;
using Player = SFD.Player;

namespace SFR.Weapons.Others;

internal sealed class LeapPack : HItem
{
    internal LeapPack()
    {
        HItemProperties itemProperties = new(124, "LeapPack", "ItemLeapPack", false, WeaponCategory.Supply)
        {
            GrabSoundID = "GetHealthSmall",
            VisualText = "Leap Pack"
        };
        HItemVisuals visuals = new(Textures.GetTexture("Pills"));

        Properties = itemProperties;
        Visuals = visuals;
    }

    private LeapPack(HItemProperties itemProperties, HItemVisuals itemVisuals) : base(itemProperties, itemVisuals)
    {
    }

    public override void OnPickup(Player player, HItem instantPickupItem)
    {
        if (player.GameOwner != GameOwnerEnum.Client)
        {
            SoundHandler.PlaySound(instantPickupItem.Properties.GrabSoundID, player.Position, player.GameWorld);

            ExtendedPlayer extendedPlayer = player.GetExtension();
            extendedPlayer.JetpackType = JetpackType.LeapPack;
            extendedPlayer.GenericJetpack = new Fighter.Jetpacks.LeapPack();
            if (player.GameOwner == GameOwnerEnum.Server)
            {
                GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], player.ObjectID, extendedPlayer.GetStates()));
            }
        }
    }

    public override bool CheckDoPickup(Player player, HItem instantPickupItem) => true;

    public override HItem Copy() => new LeapPack(Properties, Visuals);
}