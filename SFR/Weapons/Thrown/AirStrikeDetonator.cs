using SFD;
using SFD.Sounds;
using SFD.Weapons;
using SFR.Objects;

namespace SFR.Weapons.Thrown;

internal sealed class AirStrikeDetonator : TWeapon
{
    internal int ConnectedAirStrikeObjectID;
    internal bool Detonated;

    internal AirStrikeDetonator()
    {
        TWeaponProperties weaponProperties = new(121, "AirStrikeDetonator", "WpnAirStrikeDetonator", false, WeaponCategory.Supply)
        {
            CanEnterManualAim = false,
            Stackable = false,
            MaxCarriedTotalThrowables = 1,
            NumberOfThrowables = 1,
            ThrowObjectID = "",
            ThrowDeadlineTimer = 0f,
            DrawSoundID = "GrenadeDraw",
            AimStartSoundID = "",
            VisualText = "Air Strike Radio"
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

        weaponVisuals.SetModelTexture("AirStrikeDetonatorM");
        weaponVisuals.SetDrawnTexture("AirStrikeDetonatorD");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
        NumberOfThrowablesLeft = Properties.NumberOfThrowables;
    }

    private AirStrikeDetonator(TWeaponProperties weaponProperties, TWeaponVisuals weaponVisuals)
    {
        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
        NumberOfThrowablesLeft = weaponProperties.NumberOfThrowables;
    }

    public override void OnBeforeBeginCharge(TWeaponBeforeBeginChargeArgs e)
    {
        if (e.Player.InThrowingMode)
        {
            return;
        }

        e.Player.TimeSequence.TimeThrowCooldown = 1000f;
        e.CustomHandled = true;

        if (e.Player.GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("PistolAim", e.Player.Position, e.Player.GameWorld);
        }

        if (Detonated || e.Player.GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

        ObjectData target = e.Player.GameWorld.GetObjectDataByID(ConnectedAirStrikeObjectID);
        if (target is ObjectAirStrikeThrown marker && marker.Trigger(e.Player))
        {
            Detonated = true;
        }
        else
        {
            Detonated = true;
        }
    }

    public override void OnBeginCharge(TWeaponOnBeginChargeArgs e) { }
    public override void OnDeadline(TWeaponOnDeadlineArgs e) { }
    public override void OnDrop(TWeaponOnThrowArgs e) { }
    public override void OnThrow(TWeaponOnThrowArgs e) { }

    public override TWeapon Copy() => new AirStrikeDetonator(Properties, Visuals)
    {
        NumberOfThrowablesLeft = NumberOfThrowablesLeft,
        ConnectedAirStrikeObjectID = ConnectedAirStrikeObjectID,
        Detonated = Detonated
    };
}
