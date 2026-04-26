using Microsoft.Xna.Framework;
using SFD;
using SFD.Projectiles;
using SFD.Sounds;

namespace SFR.Objects;

/// <summary>
/// Visual plane that flies horizontally over the target and drops three bunker buster
/// projectiles at preset offsets. Movement is deterministic so server and clients stay in sync;
/// only the server actually spawns the bombs.
/// </summary>
internal sealed class ObjectAirStrikePlane : ObjectData
{
    private float _targetX;
    private float _flightY;
    private bool _flightYSet;
    private short _direction = 1;
    private int _triggerObjectId;
    private readonly bool[] _dropped = new bool[3];
    private bool _configured;

    internal ObjectAirStrikePlane(ObjectDataStartParams startParams) : base(startParams) { }

    internal void Configure(Vector2 targetPos, short direction, int triggerObjectId, float flightY)
    {
        _targetX = targetPos.X;
        _direction = direction == 0 ? (short)1 : direction;
        _triggerObjectId = triggerObjectId;
        _flightY = flightY;
        _flightYSet = true;
        _configured = true;
    }

    public override void Initialize()
    {
        EnableUpdateObject();
        if (Body != null)
        {
            // Plane is purely visual - never collide with anything.
            Body.SetActive(false);
        }
    }

    public override void UpdateObject(float ms)
    {
        if (Body == null)
        {
            return;
        }

        Vector2 pos = GetWorldPosition();
        float newX = pos.X + AirStrikeHandler.PlaneSpeed * ms * _direction;
        // Lock Y to the configured flight altitude (server-set, syncs to clients via spawn Y).
        float lockedY = _flightYSet ? _flightY : pos.Y;
        Body.SetTransform(new Vector2(Converter.WorldToBox2D(newX), Converter.WorldToBox2D(lockedY)), 0f);

        if (GameOwner != GameOwnerEnum.Client && _configured)
        {
            for (int i = 0; i < AirStrikeHandler.DropOffsets.Length; i++)
            {
                if (_dropped[i])
                {
                    continue;
                }

                float dropX = _targetX + AirStrikeHandler.DropOffsets[i] * _direction;
                bool reached = _direction > 0 ? newX >= dropX : newX <= dropX;
                if (reached)
                {
                    _dropped[i] = true;
                    DropBomb(new Vector2(dropX, lockedY));
                }
            }
        }

        float despawnX = _targetX + _direction * AirStrikeHandler.PlaneTravelDistance;
        bool offscreen = _direction > 0 ? newX >= despawnX : newX <= despawnX;
        if (offscreen && GameOwner != GameOwnerEnum.Client)
        {
            Destroy();
        }
    }

    private void DropBomb(Vector2 from)
    {
        // Slight forward lean so bombs visually trail the plane's motion.
        Vector2 dropDir = new(_direction * 0.15f, -1f);
        _ = GameWorld.SpawnProjectile(115, from, dropDir, _triggerObjectId);
        SoundHandler.PlaySound("WpnDraw", from, GameWorld);
    }
}
