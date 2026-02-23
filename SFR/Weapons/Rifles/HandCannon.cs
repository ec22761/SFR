using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Objects;
using SFD.Sounds;
using SFD.Weapons;

namespace SFR.Weapons.Rifles;

internal sealed class HandCannon : RWeapon
{
    internal HandCannon()
    {
        RWeaponProperties weaponProperties = new(116, "Hand_Cannon", "WpnHandCannon", false, WeaponCategory.Primary)
        {
            MaxMagsInWeapon = 1,
            MaxRoundsInMag = 1,
            MaxCarriedSpareMags = 3,
            StartMags = 2,
            CooldownBeforePostAction = 1200,
            CooldownAfterPostAction = 400,
            ExtraAutomaticCooldown = 200,
            ProjectilesEachBlast = 1,
            ShellID = string.Empty,
            AccuracyDeflection = 0.06f,
            ProjectileID = 115,
            MuzzlePosition = new Vector2(13f, -2.5f),
            CursorAimOffset = new Vector2(0f, 2.5f),
            LazerPosition = new Vector2(12f, -1.5f),
            MuzzleEffectTextureID = "MuzzleFlashShotgun",
            BlastSoundID = "Explosion",
            DrawSoundID = "CarbineDraw",
            GrabAmmoSoundID = "CarbineReload",
            OutOfAmmoSoundID = "OutOfAmmoHeavy",
            AimStartSoundID = "PistolAim",
            AI_DamageOutput = DamageOutputType.High,
            AI_GravityArcingEffect = 0.5f,
            CanRefilAtAmmoStashes = false,
            BreakDebris =
            [
                "MetalDebris00C",
                "ItemDebrisWood00",
                "ItemDebrisShiny00"
            ],
            SpecialAmmoBulletsRefill = 2,
            VisualText = "Hand Cannon"
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
            AnimReloadUpper = "UpperReloadBazooka",
            AnimFullLand = "FullLandHandgun",
            AnimToggleThrowingMode = "UpperToggleThrowing"
        };

        weaponVisuals.SetModelTexture("HandCannonM");
        weaponVisuals.SetDrawnTexture("HandCannonD");
        weaponVisuals.SetSheathedTexture("HandCannonS");
        weaponVisuals.SetThrowingTexture("HandCannonThrowing");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
    }

    private HandCannon(RWeaponProperties weaponProperties, RWeaponVisuals weaponVisuals) => SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

    public override RWeapon Copy()
    {
        HandCannon wpn = new(Properties, Visuals);
        wpn.CopyStatsFrom(this);
        return wpn;
    }

    public override void OnRecoilEvent(Player player)
    {
        if (player.GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("BarrelExplode", player.Position, player.GameWorld);
            SoundHandler.PlaySound(Properties.BlastSoundID, player.Position, player.GameWorld);
            EffectHandler.PlayEffect("MZLED", Vector2.Zero, player.GameWorld, player, Properties.MuzzleEffectTextureID);
            EffectHandler.PlayEffect("CAM_S", Vector2.Zero, player.GameWorld, 1f, 200f, false);
        }

        // Heavy recoil pushback
        Vector2 force = new(player.ScriptBridge.AimVector.X, player.ScriptBridge.AimVector.Y);
        force.Normalize();
        force *= -6;
        force += player.CurrentVelocity * 0.75f;
        player.FallWithSpeed(force);
    }

    public override void ConsumeAmmoFromFire(Player player)
    {
        for (int i = 0; i < 12; i++)
        {
            if (player.CurrentAction == PlayerAction.ManualAim)
            {
                EffectHandler.PlayEffect("TR_S", player.Position + player.AimVector() * 16 + new Vector2(0f, player.Crouching ? 11f : 16f), player.GameWorld);
            }
            else
            {
                EffectHandler.PlayEffect("TR_S", player.Position + new Vector2(20f * player.LastDirectionX, player.Crouching ? 11f : 16f), player.GameWorld);
            }
        }

        base.ConsumeAmmoFromFire(player);
    }

    public override void OnReloadAnimationEvent(Player player, AnimationEvent animEvent, SubAnimationPlayer subAnim)
    {
        if (player.GameOwner != GameOwnerEnum.Server && animEvent == AnimationEvent.EnterFrame)
        {
            if (subAnim.GetCurrentFrameIndex() == 1)
            {
                SoundHandler.PlaySound("MagnumReloadEnd", player.Position, player.GameWorld);
            }
            else if (subAnim.GetCurrentFrameIndex() == 4)
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
}
