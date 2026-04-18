using Microsoft.Xna.Framework;
using SFD;
using SFD.Sounds;
using SFD.Weapons;
using SFR.Helper;
using SFR.Misc;

namespace SFR.Weapons.Thrown;

internal sealed class Caltrops : TWeapon
{
    internal Caltrops()
    {
        TWeaponProperties weaponProperties = new(117, "Caltrops", "WpnCaltrops", false, WeaponCategory.Supply)
        {
            MaxCarriedTotalThrowables = 3,
            NumberOfThrowables = 3,
            ThrowObjectID = "WpnCaltropThrown",
            ThrowDeadlineTimer = 0f,
            DrawSoundID = "GrenadeDraw",
            VisualText = "Caltrops"
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

        weaponVisuals.SetModelTexture("CaltropsM");
        weaponVisuals.SetDrawnTexture("CaltropT");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
        NumberOfThrowablesLeft = Properties.NumberOfThrowables;
    }

    private Caltrops(TWeaponProperties weaponProperties, TWeaponVisuals weaponVisuals)
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

        if (e.Player.GameOwner != GameOwnerEnum.Client)
        {
            ObjectData thrown = e.ThrowableObjectData;
            Vector2 velocity = thrown.Body.GetLinearVelocity() * 0.6f;
            thrown.Body.SetLinearVelocity(velocity);
            thrown.Body.SetAngularVelocity(Globals.Random.NextFloat(-8f, 8f));

            Vector2 basePos = thrown.GetWorldPosition();

            // Spawn 2 additional caltrops with spread
            for (int i = 0; i < 2; i++)
            {
                float angleOffset = i == 0 ? -0.4f : 0.4f;
                Vector2 extraVel = velocity.GetRotatedVector(angleOffset);
                extraVel *= 0.8f + Globals.Random.NextFloat(0f, 0.4f);

                SpawnObjectInformation spawnInfo = new(
                    e.Player.GameWorld.IDCounter.NextObjectData("WpnCaltropThrown"),
                    basePos, 0f, 1,
                    extraVel, Globals.Random.NextFloat(-8f, 8f));
                _ = e.Player.GameWorld.CreateTile(spawnInfo);
            }
        }
    }

    public override void OnBeginCharge(TWeaponOnBeginChargeArgs e) { }

    public override void OnDrop(TWeaponOnThrowArgs e) { }

    public override void OnDeadline(TWeaponOnDeadlineArgs e) => e.Action = TWeaponDeadlineAction.Drop;

    public override TWeapon Copy() => new Caltrops(Properties, Visuals)
    {
        NumberOfThrowablesLeft = NumberOfThrowablesLeft
    };
}
