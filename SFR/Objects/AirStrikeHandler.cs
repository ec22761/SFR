using Microsoft.Xna.Framework;
using SFD;
using SFD.Sounds;
using Player = SFD.Player;
using AABB = Box2D.XNA.AABB;
using PhysicsLayer = SFDGameScriptInterface.PhysicsLayer;

namespace SFR.Objects;

/// <summary>
/// Coordinates an Air Strike fly-by: spawns the plane object that flies overhead
/// and drops three bunker busters around the target marker position.
/// </summary>
internal static class AirStrikeHandler
{
    /// <summary>Minimum vertical offset above the target where the plane flies.</summary>
    internal const float PlaneAltitude = 220f;

    /// <summary>Extra clearance kept above the tallest detected object in the flight column.</summary>
    internal const float PlaneClearance = 50f;

    /// <summary>Half-width of the X band scanned to find the tallest obstacle along the flight path.</summary>
    internal const float ObstacleScanHalfWidth = 220f;

    /// <summary>Cap on how high above the target the plane can fly. Prevents the plane disappearing offscreen on tall maps.</summary>
    internal const float MaxAltitudeAboveTarget = 300f;

    /// <summary>Horizontal distance from target where the plane spawns and despawns.
    /// Made large enough that the plane is always well off-screen at spawn even on wide maps / zoomed-out cameras.</summary>
    internal const float PlaneTravelDistance = 1200f;

    /// <summary>Travel speed in game units per millisecond. Bumped to keep total flight time reasonable across the larger travel distance.</summary>
    internal const float PlaneSpeed = 0.85f;

    /// <summary>Per-bomb horizontal offsets relative to the target X (in flight direction).</summary>
    internal static readonly float[] DropOffsets = { -42f, 0f, 42f };

    internal static void LaunchStrike(GameWorld gameWorld, Vector2 targetPosition, Player triggeringPlayer)
    {
        if (gameWorld == null || gameWorld.GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

        // Pick flight direction based on triggering player's facing (default right).
        short direction = 1;
        if (triggeringPlayer is { IsRemoved: false })
        {
            direction = (short)(triggeringPlayer.LastDirectionX >= 0 ? 1 : -1);
        }

        // Find the tallest object Y in a column around the target so the plane flies above buildings.
        float planeY = ComputeFlightAltitude(gameWorld, targetPosition);

        Vector2 spawnPos = new(targetPosition.X - direction * PlaneTravelDistance, planeY);

        ObjectAirStrikePlane plane = (ObjectAirStrikePlane)gameWorld.IDCounter.NextObjectData("AirStrikePlane");
        int triggerId = triggeringPlayer is { IsRemoved: false } ? triggeringPlayer.ObjectID : 0;
        plane.Configure(targetPosition, direction, triggerId, planeY);
        _ = gameWorld.CreateTile(new SpawnObjectInformation(plane, spawnPos, 0f, direction, Vector2.Zero, 0f));

        // Play flyby sound at the spawn position so it arrives with the plane.
        SoundHandler.PlaySound("PlaneFlyby", spawnPos, gameWorld);
        SoundHandler.PlaySound("C4Detonate", targetPosition, gameWorld);
    }

    /// <summary>
    /// Returns the world Y at which the plane should fly: the higher of
    /// (target + PlaneAltitude) and (tallest object in the flight column + clearance).
    /// </summary>
    private static float ComputeFlightAltitude(GameWorld gameWorld, Vector2 targetPosition)
    {
        float baseAltitude = targetPosition.Y + PlaneAltitude;
        float maxAltitude = targetPosition.Y + MaxAltitudeAboveTarget;
        float highestObstacleY = float.NegativeInfinity;

        try
        {
            // Vertical column from target Y up to the altitude cap.
            // Scanning higher than that is pointless because we'll clamp to maxAltitude anyway,
            // and ignoring distant ceiling geometry keeps the plane in view.
            Vector2 lower = new(targetPosition.X, targetPosition.Y);
            Vector2 upper = new(targetPosition.X, maxAltitude);
            AABB.Create(out AABB aabb, lower, upper, ObstacleScanHalfWidth);
            var hits = gameWorld.GetObjectDataByArea(aabb, false, PhysicsLayer.Active);
            foreach (var od in hits)
            {
                if (od == null)
                {
                    continue;
                }

                float y = od.GetWorldPosition().Y;
                if (y > highestObstacleY)
                {
                    highestObstacleY = y;
                }
            }
        }
        catch
        {
            // If anything goes wrong with the query, fall back to base altitude.
        }

        float chosen = baseAltitude;
        if (!float.IsNegativeInfinity(highestObstacleY))
        {
            float clearedAltitude = highestObstacleY + PlaneClearance;
            if (clearedAltitude > chosen)
            {
                chosen = clearedAltitude;
            }
        }

        // Never fly higher than the cap, so the plane stays roughly in camera view.
        if (chosen > maxAltitude)
        {
            chosen = maxAltitude;
        }

        return chosen;
    }
}
