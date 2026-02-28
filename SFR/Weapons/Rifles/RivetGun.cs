using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Objects;
using SFD.Sounds;
using SFD.Weapons;

namespace SFR.Weapons.Rifles;

/// <summary>
/// Rivet Gun — a semi-auto rifle that functions as the NailGun's bigger brother.
/// Medium damage per shot; projectiles explode into shrapnel on hitting surfaces,
/// dealing area damage to nearby players.
/// </summary>
internal sealed class RivetGun : RWeapon
{
    internal RivetGun()
    {
        RWeaponProperties weaponProperties = new(119, "Rivet_Gun", "WpnRivetGun", false, WeaponCategory.Primary)
        {
            MaxMagsInWeapon = 1,
            MaxRoundsInMag = 12,
            MaxCarriedSpareMags = 3,
            StartMags = 2,
            CooldownBeforePostAction = 0,
            CooldownAfterPostAction = 200,
            ExtraAutomaticCooldown = 0,
            ShellID = string.Empty,
            AccuracyDeflection = 0.08f,
            ProjectileID = 119,
            MuzzlePosition = new Vector2(13f, -2.5f),
            CursorAimOffset = new Vector2(0f, 2.5f),
            LazerPosition = new Vector2(12f, -1.5f),
            MuzzleEffectTextureID = "MuzzleFlashS",
            BlastSoundID = "Carbine",
            DrawSoundID = "CarbineDraw",
            GrabAmmoSoundID = "CarbineReload",
            OutOfAmmoSoundID = "OutOfAmmoHeavy",
            AimStartSoundID = "PistolAim",
            AI_DamageOutput = DamageOutputType.Standard,
            AI_GravityArcingEffect = 0.5f,
            AI_EffectiveRange = 175,
            BreakDebris =
            [
                "ItemDebrisDark00",
                "ItemDebrisDark01",
                "ItemDebrisShiny00"
            ],
            SpecialAmmoBulletsRefill = 12,
            VisualText = "Rivet Gun"
        };

        RWeaponVisuals weaponVisuals = new()
        {
            AnimIdleUpper = "UpperIdleRifle",
            AnimCrouchUpper = "UpperCrouchRifle",
            AnimJumpKickUpper = "UpperJumpKickRifle",
            AnimJumpUpper = "UpperJumpRifle",
            AnimJumpUpperFalling = "UpperJumpFallingRifle",
            AnimKickUpper = "UpperKickRifle",
            AnimStaggerUpper = "UpperStaggerHandgun",
            AnimRunUpper = "UpperRunRifle",
            AnimWalkUpper = "UpperWalkRifle",
            AnimUpperHipfire = "UpperHipfireRifle",
            AnimFireArmLength = 2f,
            AnimDraw = "UpperDrawRifle",
            AnimManualAim = "ManualAimRifle",
            AnimManualAimStart = "ManualAimRifleStart",
            AnimReloadUpper = "UpperReload",
            AnimFullLand = "FullLandHandgun",
            AnimToggleThrowingMode = "UpperToggleThrowing"
        };

        weaponVisuals.SetModelTexture("RivetGunM");
        weaponVisuals.SetDrawnTexture("RivetGunD");
        weaponVisuals.SetSheathedTexture("RivetGunS");
        weaponVisuals.SetThrowingTexture("RivetGunThrowing");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

        CacheDrawnTextures(["Reload"]);
    }

    private RivetGun(RWeaponProperties weaponProperties, RWeaponVisuals weaponVisuals) => SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

    public override RWeapon Copy()
    {
        RivetGun wpnRivetGun = new(Properties, Visuals);
        wpnRivetGun.CopyStatsFrom(this);
        return wpnRivetGun;
    }

    public override void OnRecoilEvent(Player player)
    {
        if (player.GameOwner != GameOwnerEnum.Server)
        {
            if (Properties.ShellID != string.Empty && Constants.EFFECT_LEVEL_FULL)
            {
                SpawnUnsyncedShell(player, Properties.ShellID);
            }

            SoundHandler.PlaySound(Properties.BlastSoundID, player.Position, player.GameWorld);
        }
    }

    public override void OnReloadAnimationEvent(Player player, AnimationEvent animEvent, SubAnimationPlayer subAnim)
    {
        if (player.GameOwner != GameOwnerEnum.Server)
        {
            if (animEvent == AnimationEvent.EnterFrame && subAnim.GetCurrentFrameIndex() == 1)
            {
                SpawnMagazine(player, "MagSmall", new Vector2(-6f, -5f));
                SoundHandler.PlaySound("MagnumReloadEnd", player.Position, player.GameWorld);
            }

            if (animEvent == AnimationEvent.EnterFrame && subAnim.GetCurrentFrameIndex() == 4)
            {
                SoundHandler.PlaySound("CarbineReload", player.Position, player.GameWorld);
            }
        }
    }

    public override void OnSubAnimationEvent(Player player, AnimationEvent animationEvent, AnimationData animationData, int currentFrameIndex)
    {
        if (player.GameOwner != GameOwnerEnum.Server && animationEvent == AnimationEvent.EnterFrame && animationData.Name == "UpperDrawRifle")
        {
            switch (currentFrameIndex)
            {
                case 1:
                    SoundHandler.PlaySound("Draw1", player.GameWorld);
                    break;
                case 6:
                    SoundHandler.PlaySound("CarbineDraw", player.GameWorld);
                    break;
            }
        }
    }

    public override void OnThrowWeaponItem(Player player, ObjectWeaponItem thrownWeaponItem)
    {
        thrownWeaponItem.Body.SetAngularVelocity(thrownWeaponItem.Body.GetAngularVelocity() * 0.8f);
        Vector2 linearVelocity = thrownWeaponItem.Body.GetLinearVelocity() * 0.85f;
        thrownWeaponItem.Body.SetLinearVelocity(linearVelocity);
        base.OnThrowWeaponItem(player, thrownWeaponItem);
    }

    public override Texture2D GetDrawnTexture(ref GetDrawnTextureArgs args)
    {
        if (args.SubAnimation is "UpperReload" && args.SubFrame is >= 1 and <= 5)
        {
            args.Postfix = "Reload";
        }

        return base.GetDrawnTexture(ref args);
    }
}
