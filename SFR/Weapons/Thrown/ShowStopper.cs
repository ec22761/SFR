using Microsoft.Xna.Framework;
using SFD;
using SFD.Sounds;
using SFD.Weapons;
using SFR.Helper;
using SFR.Misc;
using SFR.Objects;

namespace SFR.Weapons.Thrown;

internal sealed class ShowStopper : TWeapon
{
    internal ShowStopper()
    {
        TWeaponProperties weaponProperties = new(112, "Show_Stopper", "WpnShowStopper", false, WeaponCategory.Supply)
        {
            MaxCarriedTotalThrowables = 3,
            NumberOfThrowables = 2,
            ThrowObjectID = "WpnShowStopperThrown",
            ThrowDeadlineTimer = 2550f,
            DrawSoundID = "GrenadeDraw",
            VisualText = "Show Stoppers"
        };

        TWeaponVisuals weaponVisuals = new()
        {
            AnimDraw = "UpperDrawThrown",
            AnimManualAim = "ManualAimThrown",
            AnimManualAimStart = "ManualAimThrownStart",
            AnimCrouchUpper = "UpperCrouch",
            AnimIdleUpper = "UpperIdle",
            AnimJumpKickUpper = "UpperJumpKick",
            AnimJumpUpper = "UpperJump",
            AnimJumpUpperFalling = "UpperJumpFalling",
            AnimKickUpper = "UpperKick",
            AnimStaggerUpper = "UpperStagger",
            AnimRunUpper = "UpperRun",
            AnimWalkUpper = "UpperWalk",
            AnimFullLand = "FullLandThrown",
            AnimToggleThrowingMode = "UpperToggleThrowing"
        };

        weaponVisuals.SetModelTexture("ShowStopperM");
        weaponVisuals.SetDrawnTexture("ShowStopperT");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

        NumberOfThrowablesLeft = Properties.NumberOfThrowables;
    }

    private ShowStopper(TWeaponProperties weaponProperties, TWeaponVisuals weaponVisuals)
    {
        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
        NumberOfThrowablesLeft = weaponProperties.NumberOfThrowables;
    }

    public override void OnBeforeBeginCharge(TWeaponBeforeBeginChargeArgs e)
    {
    }

    public override void OnThrow(TWeaponOnThrowArgs e)
    {
        if (e.Player.GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("GrenadeThrow", e.Player.Position, e.Player.GameWorld);
        }

        if (e.Player.GameOwner != GameOwnerEnum.Client && e.ThrowableIsActivated)
        {
            ObjectShowStopperThrown objectGrenadeThrown = (ObjectShowStopperThrown)e.ThrowableObjectData;
            objectGrenadeThrown.ExplosionTimer = e.ThrowableDeadlineTimer;
        }
    }

    public override void OnBeginCharge(TWeaponOnBeginChargeArgs e)
    {
        if (e.Player.GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("GrenadeSafe", e.Player.Position, e.Player.GameWorld);
            Vector2 worldPosition = e.Player.Position + new Vector2(-(float)e.Player.LastDirectionX * 5f, 7f);
            Vector2 linearVelocity = new(-(float)e.Player.LastDirectionX * 2f, 2f);
            _ = e.Player.GameWorld.CreateLocalTile("WpnGrenadePin", worldPosition, Globals.Random.NextFloat(-3f, 3f), (short)e.Player.LastDirectionX, linearVelocity, Globals.Random.NextFloat(-3f, 3f));
        }
    }

    public override void OnDrop(TWeaponOnThrowArgs e)
    {
        if (e.Player.GameOwner != GameOwnerEnum.Client && e.ThrowableIsActivated)
        {
            ObjectShowStopperThrown objectGrenadeThrown = (ObjectShowStopperThrown)e.ThrowableObjectData;
            objectGrenadeThrown.ExplosionTimer = e.ThrowableDeadlineTimer;
        }
    }

    public override void OnDeadline(TWeaponOnDeadlineArgs e) => e.Action = TWeaponDeadlineAction.Drop;

    public override TWeapon Copy() => new ShowStopper(Properties, Visuals)
    {
        NumberOfThrowablesLeft = NumberOfThrowablesLeft
    };
}
