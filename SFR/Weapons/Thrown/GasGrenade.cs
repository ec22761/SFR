using Microsoft.Xna.Framework;
using SFD;
using SFD.Sounds;
using SFD.Weapons;
using SFR.Helper;
using SFR.Misc;
using SFR.Objects;

namespace SFR.Weapons.Thrown;

internal sealed class GasGrenade : TWeapon
{
    internal GasGrenade()
    {
        TWeaponProperties weaponProperties = new(111, "Gas_Grenades", "WpnGasGrenades", false, WeaponCategory.Supply)
        {
            MaxCarriedTotalThrowables = 4,
            NumberOfThrowables = 2,
            ThrowObjectID = "WpnGasGrenadesThrown",
            ThrowDeadlineTimer = 2550f,
            DrawSoundID = "GrenadeDraw",
            VisualText = "Gas Grenades"
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

        weaponVisuals.SetModelTexture("GasGrenadeM");
        weaponVisuals.SetDrawnTexture("GasGrenadeT");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

        NumberOfThrowablesLeft = Properties.NumberOfThrowables;
    }

    private GasGrenade(TWeaponProperties weaponProperties, TWeaponVisuals weaponVisuals)
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
            ObjectGasGrenadeThrown objectGrenadeThrown = (ObjectGasGrenadeThrown)e.ThrowableObjectData;
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
            ObjectGasGrenadeThrown objectGrenadeThrown = (ObjectGasGrenadeThrown)e.ThrowableObjectData;
            objectGrenadeThrown.ExplosionTimer = e.ThrowableDeadlineTimer;
        }
    }

    public override void OnDeadline(TWeaponOnDeadlineArgs e) => e.Action = TWeaponDeadlineAction.Drop;

    public override TWeapon Copy() => new GasGrenade(Properties, Visuals)
    {
        NumberOfThrowablesLeft = NumberOfThrowablesLeft
    };
}
