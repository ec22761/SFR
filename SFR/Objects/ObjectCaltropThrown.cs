using Box2D.XNA;
using Microsoft.Xna.Framework;
using SFD;
using SFD.Sounds;
using Math = System.Math;
using Player = SFD.Player;

namespace SFR.Objects;

internal sealed class ObjectCaltropThrown : ObjectData
{
    private const float _caltropDamage = 8f;

    internal ObjectCaltropThrown(ObjectDataStartParams startParams) : base(startParams) { }

    public override void Initialize()
    {
        EnableUpdateObject();
        Body.SetLinearDamping(1.5f);
        Body.SetAngularDamping(1.5f);
        Body.SetAngularVelocity(Body.GetAngularVelocity() * 6f);
    }

    public override void UpdateObject(float ms)
    {
        if (GameOwner == GameOwnerEnum.Client) return;

        AABB caltropArea = GetWorldAABB();
        foreach (Player player in GameWorld.Players)
        {
            if (player.IsDead || player.IsRemoved) continue;

            AABB playerArea = player.ObjectData.GetWorldAABB();
            if (playerArea.Overlap(ref caltropArea))
            {
                player.TakeMiscDamage(_caltropDamage);
                Destroy();
                return;
            }
        }
    }

    public override void OnDestroyObject()
    {
        if (GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("ImpactMetal", GetWorldPosition(), GameWorld);
        }
    }
}
