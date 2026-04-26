using SFD;
using SFD.Sounds;
using SFD.Weapons;
using SFR.Objects;

namespace SFR.Weapons.Thrown;

internal sealed class AirStrike : TWeapon
{
    internal AirStrike()
    {
        TWeaponProperties weaponProperties = new(120, "AirStrike", "WpnAirStrike", false, WeaponCategory.Supply)
        {
            MaxCarriedTotalThrowables = 1,
            NumberOfThrowables = 1,
            ThrowObjectID = "WpnAirStrikeThrown",
            DrawSoundID = "GrenadeDraw",
            AimStartSoundID = "",
            VisualText = "Air Strike Targetter"
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

        weaponVisuals.SetModelTexture("AirStrikeM");
        weaponVisuals.SetDrawnTexture("AirStrikeM");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
        NumberOfThrowablesLeft = Properties.NumberOfThrowables;
    }

    private AirStrike(TWeaponProperties weaponProperties, TWeaponVisuals weaponVisuals)
    {
        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
        NumberOfThrowablesLeft = weaponProperties.NumberOfThrowables;
    }

    public override void OnBeforeBeginCharge(TWeaponBeforeBeginChargeArgs e) { }

    public override void OnThrow(TWeaponOnThrowArgs e)
    {
        if (e.Player.GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("GrenadeThrow", e.Player.Position, e.Player.GameWorld);
        }

        AfterDropOrThrow(e);
    }

    public override void OnBeginCharge(TWeaponOnBeginChargeArgs e)
    {
        if (e.Player.GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("GrenadeSafe", e.Player.Position, e.Player.GameWorld);
        }
    }

    public override void OnDrop(TWeaponOnThrowArgs e) => AfterDropOrThrow(e);

    public override void OnDeadline(TWeaponOnDeadlineArgs e) => e.Action = TWeaponDeadlineAction.Drop;

    private static void AfterDropOrThrow(TWeaponOnThrowArgs e)
    {
        if (e.Player.GameOwner == GameOwnerEnum.Client || !e.ThrowableIsActivated)
        {
            return;
        }

        ObjectAirStrikeThrown thrown = (ObjectAirStrikeThrown)e.ThrowableObjectData;

        // Equip the AirStrike detonator (weapon ID 121) and bind it to the marker.
        e.Player.EquipWeaponItem(WeaponDatabase.GetWeapon(121));
        e.Player.CurrentWeaponQueued = WeaponItemType.Thrown;
        if (e.Player.CurrentThrownWeapon is AirStrikeDetonator detonator)
        {
            detonator.ConnectedAirStrikeObjectID = thrown.ObjectID;
        }

        e.Player.SyncThrowableWeapon();
        e.Player.QueueAddedWeaponCallback(WeaponItemType.Thrown, 121, 0);
    }

    public override TWeapon Copy() => new AirStrike(Properties, Visuals)
    {
        NumberOfThrowablesLeft = NumberOfThrowablesLeft
    };
}
