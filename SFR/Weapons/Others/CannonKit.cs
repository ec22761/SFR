using Box2D.XNA;
using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Objects;
using SFD.Sounds;
using SFD.Tiles;
using SFD.Weapons;
using SFDGameScriptInterface;
using Player = SFD.Player;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace SFR.Weapons.Others;

internal sealed class CannonKit : PItem
{
    private const float PushRadius = 40f;
    private const float PushForce = 6f;
    private const float PushMinSpeed = 4f;
    private const float PushMaxSpeed = 10f;

    internal CannonKit()
    {
        PItemProperties itemProperties = new(116, "Cannon_Kit", "ItemCannonKit", false, WeaponCategory.Supply)
        {
            PickupSoundID = "GetSlomo",
            ActivateSoundID = "CoinSlot",
            VisualText = "Cannon Kit"
        };

        PItemVisuals visuals = new(Textures.GetTexture("CannonBall00"));

        SetPropertiesAndVisuals(itemProperties, visuals);
    }

    private CannonKit(PItemProperties itemProperties, PItemVisuals itemVisuals) => SetPropertiesAndVisuals(itemProperties, itemVisuals);

    public override void OnActivation(Player player, PItem powerupItem)
    {
        if (player.GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

        int dir = player.LastDirectionX;
        int behind = -dir;

        // Push nearby players away before spawning
        PushNearbyObjects(player, dir);

        // Spawn two wood pallets side by side beneath the cannon, balls, and player
        for (int i = 0; i < 2; i++)
        {
            float palletX = player.Position.X + behind * 3 + (i == 0 ? dir * 14 : behind * 14);
            Vector2 palletPos = new(palletX, player.Position.Y - 4);
            ObjectData pallet = ObjectData.CreateNew(
                new ObjectDataStartParams(player.GameWorld.IDCounter.NextID(), 0, 0, "Pallet00", player.GameWorld.GameOwner));
            _ = player.GameWorld.CreateTile(new SpawnObjectInformation(pallet, palletPos, 0, 1, Vector2.Zero, 0f));
        }

        // Spawn cannon in front of the player, facing away from them
        Vector2 cannonPos = player.Position + new Vector2(dir * 20, 0);
        ObjectData cannon = ObjectData.CreateNew(
            new ObjectDataStartParams(player.GameWorld.IDCounter.NextID(), 0, 0, "Cannon00", player.GameWorld.GameOwner));
        _ = player.GameWorld.CreateTile(new SpawnObjectInformation(cannon, cannonPos, 0, (short)dir, Vector2.Zero, 0f));

        // Spawn 3 cannon balls behind the player in a triangle:
        // Two on the ground, one on top centered between them
        Vector2 ball1Pos = player.Position + new Vector2(behind * 16, 0);
        Vector2 ball2Pos = player.Position + new Vector2(behind * 26, 0);
        Vector2 ball3Pos = player.Position + new Vector2(behind * 21, 9);

        for (int i = 0; i < 3; i++)
        {
            Vector2 pos = i switch
            {
                0 => ball1Pos,
                1 => ball2Pos,
                _ => ball3Pos
            };

            ObjectData ball = ObjectData.CreateNew(
                new ObjectDataStartParams(player.GameWorld.IDCounter.NextID(), 0, 0, "CannonBall00", player.GameWorld.GameOwner));
            _ = player.GameWorld.CreateTile(new SpawnObjectInformation(ball, pos, 0, 1, Vector2.Zero, 0f));
        }

        // Effects and sound
        SoundHandler.PlaySound(powerupItem.Properties.ActivateSoundID, player.Position, player.GameWorld);
        EffectHandler.PlayEffect("EXP", cannonPos, player.GameWorld);
        EffectHandler.PlayEffect("CAM_S", Vector2.Zero, player.GameWorld, 0.5f, 150f, false);

        player.RemovePowerup();
    }

    private static void PushNearbyObjects(Player owner, int dir)
    {
        Vector2 center = owner.Position;
        AABB.Create(out AABB area, center, center, PushRadius);

        foreach (ObjectData obj in owner.GameWorld.GetObjectDataByArea(area, false, PhysicsLayer.Active))
        {
            if (obj.InternalData is Player player && player != owner && !player.IsDead && !player.IsRemoved)
            {
                Vector2 direction = player.Position - center;
                float distance = Vector2.Distance(player.Position, center);
                if (distance < 1f)
                {
                    direction = new Vector2(dir, 0);
                    distance = 1f;
                }

                Vector2 push = direction / distance * PushForce;
                if (push.Length() > PushMaxSpeed)
                {
                    push.Normalize();
                    push *= PushMaxSpeed;
                }
                else if (push.Length() < PushMinSpeed)
                {
                    push.Normalize();
                    push *= PushMinSpeed;
                }

                player.Position += new Vector2(0f, 2f);
                player.SimulateFallWithSpeed(push + new Vector2(0f, 2f));
            }
        }
    }

    public override PItem Copy() => new CannonKit(Properties, Visuals);
}
