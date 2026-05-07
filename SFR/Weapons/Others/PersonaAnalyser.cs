using System.Collections.Generic;
using SFD;
using SFD.Sounds;
using SFD.Tiles;
using SFD.Weapons;
using SFR.Fighter;
using SFR.Helper;
using SFR.Misc;
using Player = SFD.Player;

namespace SFR.Weapons.Others;

internal class PersonaAnalyser : PItem
{
    internal PersonaAnalyser()
    {
        PItemProperties itemProperties = new(123, "PersonaAnalyser", "ItemPersonaAnalyser", false, WeaponCategory.Supply)
        {
            PickupSoundID = "GetSlomo",
            ActivateSoundID = "",
            VisualText = "Persona Analyser"
        };

        PItemVisuals itemVisuals = new(Textures.GetTexture("PersonaAnalyser"), Textures.GetTexture("PersonaAnalyser"));

        SetPropertiesAndVisuals(itemProperties, itemVisuals);
    }

    private PersonaAnalyser(PItemProperties properties, PItemVisuals visuals) => SetPropertiesAndVisuals(properties, visuals);

    public override void OnActivation(Player player, PItem powerupItem)
    {
        if (FindTarget(player) is not null && player.StrengthBoostPrepare())
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

        Player target = FindTarget(player);
        Profile disguiseProfile = target?.GetProfile()?.Copy();
        if (disguiseProfile is null)
        {
            return;
        }

        disguiseProfile.Name = target.Name;

        SoundHandler.PlaySound("Syringe", player.Position, player.GameWorld);
        SoundHandler.PlaySound("GetSlomo", player.Position, player.GameWorld);

        player.GetExtension().ApplyPersonaDisguise(disguiseProfile, target.CurrentTeam);
        if (!player.InfiniteAmmo)
        {
            player.RemovePowerup();
        }
    }

    private static Player FindTarget(Player player)
    {
        List<Player> targets = [];
        if (player.GameWorld?.Players is null)
        {
            return null;
        }

        foreach (Player candidate in player.GameWorld.Players)
        {
            if (candidate == player || candidate.IsRemoved || candidate.IsDead || !player.IsEnemyOf(candidate) || candidate.GetProfile() is null)
            {
                continue;
            }

            targets.Add(candidate);
        }

        return targets.Count == 0 ? null : targets[Globals.Random.Next(targets.Count)];
    }

    public override PItem Copy() => new PersonaAnalyser(Properties, Visuals);
}