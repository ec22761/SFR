using Microsoft.Xna.Framework;
using SFD;
using SFD.Sounds;
using SFD.Weapons;

namespace SFR.Weapons.Rifles;

internal sealed class Blowpipe : RWeapon
{
    internal Blowpipe()
    {
        RWeaponProperties weaponProperties = new(114, "Blowpipe", "WpnBlowpipe", false, WeaponCategory.Primary)
        {
            MaxMagsInWeapon = 1,
            MaxRoundsInMag = 1,
            MaxCarriedSpareMags = 6,
            StartMags = 3,
            CooldownBeforePostAction = 600,
            CooldownAfterPostAction = 0,
            ExtraAutomaticCooldown = 0,
            ShellID = "",
            AccuracyDeflection = 0.02f,
            ProjectileID = 114,
            MuzzlePosition = new Vector2(10f, -2f),
            MuzzleEffectTextureID = "",
            BlastSoundID = "PistolFire",
            DrawSoundID = "PistolDraw",
            GrabAmmoSoundID = "PistolReload",
            OutOfAmmoSoundID = "OutOfAmmoLight",
            CursorAimOffset = new Vector2(0f, 3f),
            LazerPosition = new Vector2(8f, -0.5f),
            AimStartSoundID = "PistolAim",
            AI_DamageOutput = DamageOutputType.Low,
            BreakDebris = ["ItemDebrisDark00", "ItemDebrisDark01"],
            SpecialAmmoBulletsRefill = 6,
            VisualText = "Blowpipe"
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
            AnimFireArmLength = 7f,
            AnimDraw = "UpperDrawRifle",
            AnimManualAim = "ManualAimRifle",
            AnimManualAimStart = "ManualAimRifleStart",
            AnimReloadUpper = "UpperReload",
            AnimFullLand = "FullLandHandgun",
            AnimToggleThrowingMode = "UpperToggleThrowing"
        };

        weaponVisuals.SetModelTexture("BlowpipeM");
        weaponVisuals.SetDrawnTexture("BlowpipeD");
        weaponVisuals.SetSheathedTexture("BlowpipeThrowing");
        weaponVisuals.SetThrowingTexture("BlowpipeThrowing");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

        CacheDrawnTextures(["Reload"]);
    }

    private Blowpipe(RWeaponProperties rwp, RWeaponVisuals rwv) => SetPropertiesAndVisuals(rwp, rwv);

    public override RWeapon Copy()
    {
        Blowpipe wpn = new(Properties, Visuals);
        wpn.CopyStatsFrom(this);
        return wpn;
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
                    SoundHandler.PlaySound("PistolDraw", player.GameWorld);
                    break;
            }
        }
    }
}
