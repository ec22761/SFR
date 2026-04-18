using System;
using System.Collections.Generic;
using System.Linq;
using Box2D.XNA;
using HarmonyLib;
using Microsoft.Xna.Framework;
using SFD;
using SFD.Objects;
using SFD.Sounds;
using SFR.Misc;
using AABB = Box2D.XNA.AABB;
using PhysicsLayer = SFDGameScriptInterface.PhysicsLayer;
using Player = SFD.Player;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace SFR.Game;

/// <summary>
/// Makes the static environment destructible by explosions. Instead of simply
/// vaporising tiles into tiny debris, the tile itself is dislodged: its Box2D
/// body is switched to dynamic and an outward impulse is applied. The chunk then
/// flies as a normal physics object, remains interactable, and lethally crushes
/// any player it slams into at speed (via the <see cref="CrushingHitPatches"/>
/// global hit patches below). A small amount of tiny debris is still emitted for
/// visual flavour.
/// </summary>
[HarmonyPatch]
internal static class EnvironmentDestructionHandler
{
    /// <summary>World-units reached per point of explosion damage.</summary>
    private const float RadiusPerDamage = 0.22f;

    /// <summary>Hard cap on the blast reach.</summary>
    private const float MaxRadius = 48f;

    /// <summary>Minimum explosion damage required to dislodge static environment tiles.</summary>
    private const float MinDamageToBreakStatic = 55f;

    /// <summary>Base impulse force applied to dislodged tiles. Scaled by damage/distance.</summary>
    private const float DislodgeImpulseScale = 0.75f;

    /// <summary>Hard cap on the launch speed applied to a dislodged tile.</summary>
    private const float MaxDislodgeSpeed = 12f;

    /// <summary>How much tiny flavour-debris to spawn alongside a dislodged tile.</summary>
    private const int FlavourDebrisPerTile = 1;

    /// <summary>
    /// Tiles whose footprint area (width × height in world units²) exceeds this
    /// threshold, OR whose shorter dimension exceeds <see cref="StructuralMinSide"/>,
    /// are considered "structural" and left alone. This protects bulky wall
    /// blocks and oversized geometry while still allowing long thin slabs,
    /// platforms, and beams to be destroyed.
    /// </summary>
    private const float StructuralAreaThreshold = 2200f;

    /// <summary>
    /// Even non-bulky tiles are protected if their shorter side exceeds this
    /// value (i.e. the tile is "chunky" in both dimensions).
    /// </summary>
    private const float StructuralMinSide = 32f;

    /// <summary>
    /// Tiles whose largest world-space dimension exceeds this threshold are
    /// considered "massive" and shatter into multiple debris chunks instead of
    /// being launched as a single rigid body.
    /// </summary>
    private const float MassiveTileSize = 24f;

    /// <summary>Approximate world-space size of a single shatter chunk.</summary>
    private const float ChunkSize = 12f;

    /// <summary>Hard cap on the number of chunks a single tile can split into.</summary>
    private const int MaxChunksPerTile = 24;

    /// <summary>
    /// Object IDs of tiles that have been dislodged by an explosion and should
    /// crush players on impact. Cleared at end of round.
    /// </summary>
    internal static readonly HashSet<int> LaunchedChunks = new();

    private static readonly string[] WoodDebris =
    {
        "WoodDebris00A", "WoodDebris00B", "WoodDebris00C", "WoodDebris00D", "WoodDebris00E"
    };

    private static readonly string[] StoneDebris =
    {
        "StoneDebris00A", "StoneDebris00B", "StoneDebris00C", "StoneDebris00D", "StoneDebris00E"
    };

    /// <summary>
    /// MapObjectID substrings that suggest the tile is "wood-flavoured", so we
    /// emit wood debris alongside it. Anything that doesn't match falls through
    /// to <see cref="StoneDebris"/> as the generic fallback.
    /// </summary>
    private static readonly string[] WoodKeywords =
    {
        "WOOD", "PLANK", "CRATE", "FENCE", "TABLE", "CHAIR", "DOOR", "BED",
        "DESK", "BARREL", "TREE", "LOG", "SHELF", "CABINET", "PALLET",
    };

    /// <summary>
    /// MapObjectIDs left alone because they have their own special-case handling
    /// elsewhere, are interactive triggers, or shouldn't physically dislodge.
    /// </summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "WOODDOOR00",
        "WOODBARREL02",
        "WOODBARRELEXPLOSIVE00",
        "MARBLESTATUE00", "MARBLESTATUE00_D", "MARBLESTATUE00_DD",
        "MARBLESTATUE01", "MARBLESTATUE01_D", "MARBLESTATUE01_DD",
        "MARBLESTATUE02", "MARBLESTATUE02_D", "MARBLESTATUE02_DD",
        "TILEINDICATOR",
    };

    /// <summary>
    /// Substrings that, if present in a MapObjectID, mark the tile as off-limits
    /// (ladders, water, triggers, spawn points, ropes, etc.).
    /// </summary>
    private static readonly string[] ExcludedSubstrings =
    {
        "LADDER", "ROPE", "WATER", "LAVA", "ELEVATOR", "TRIGGER", "SPAWN",
        "PORTAL", "TEXT", "SIGN", "MARKER", "INDICATOR", "INVISIBLE",
        "SCRIPT", "AREA", "GHOST", "BUTTON", "DRAW",
    };

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.TriggerExplosion), new[] { typeof(Vector2), typeof(float) })]
    private static void AfterTriggerExplosion(GameWorld __instance, Vector2 worldPosition, float explosionDamage)
        => HandleExplosion(__instance, worldPosition, explosionDamage);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.TriggerExplosion), new[] { typeof(Vector2), typeof(float), typeof(bool) })]
    private static void AfterTriggerExplosion2(GameWorld __instance, Vector2 worldPosition, float explosionDamage)
        => HandleExplosion(__instance, worldPosition, explosionDamage);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), "DisposeAllObjects")]
    private static void DisposeAllObjects() => LaunchedChunks.Clear();

    private static void HandleExplosion(GameWorld world, Vector2 worldPosition, float damage)
    {
        if (world == null || world.GameOwner == GameOwnerEnum.Client || damage <= 0f)
        {
            return;
        }

        float radius = Math.Min(MaxRadius, damage * RadiusPerDamage);
        if (radius < 6f)
        {
            radius = 6f;
        }

        AABB.Create(out AABB area, worldPosition, worldPosition, radius);

        // Snapshot because we mutate the world while iterating.
        List<ObjectData> objects;
        try
        {
            objects = world.GetObjectDataByArea(area, false, PhysicsLayer.Active).ToList();
        }
        catch
        {
            return;
        }

        bool canBreakStatic = damage >= MinDamageToBreakStatic;

        foreach (ObjectData obj in objects)
        {
            if (obj == null || obj.IsPlayer || obj.RemovalInitiated)
            {
                continue;
            }

            if (obj.Destructable)
            {
                // Already destructible — pile on extra damage so explosions finish it.
                try { obj.DealScriptDamage(damage); } catch { /* tolerate quirky subclasses */ }
                continue;
            }

            if (!canBreakStatic || !obj.IsStatic)
            {
                continue;
            }

            string mapId = obj.MapObjectID;
            if (string.IsNullOrEmpty(mapId) || Excluded.Contains(mapId))
            {
                continue;
            }

            if (mapId.StartsWith("BG", StringComparison.OrdinalIgnoreCase) ||
                mapId.StartsWith("FAR", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsExcludedBySubstring(mapId))
            {
                continue;
            }

            // Skip structural map geometry (huge walls/floors) so explosions can't
            // blast holes in core layout or fling whole rooms off-screen.
            if (IsStructural(obj))
            {
                continue;
            }

            string[] debris = ResolveDebris(mapId);
            if (TryShatterMassive(world, obj, worldPosition, damage, debris))
            {
                continue;
            }

            DislodgeTile(world, obj, worldPosition, damage, debris);
        }
    }

    /// <summary>
    /// Returns true if the given tile is too large to safely destroy. A tile is
    /// "structural" if either:
    ///   * its footprint area exceeds <see cref="StructuralAreaThreshold"/> (huge
    ///     by any measure — wall blocks, room-sized geometry), or
    ///   * its shorter dimension exceeds <see cref="StructuralMinSide"/> (chunky
    ///     in both axes — true wall sections rather than long thin slabs).
    /// Long thin platforms / slabs / beams stay destructible because their area
    /// is modest and their thickness is small.
    /// </summary>
    private static bool IsStructural(ObjectData tile)
    {
        AABB aabb;
        try { aabb = tile.GetWorldAABB(); }
        catch { return false; }

        float width = aabb.upperBound.X - aabb.lowerBound.X;
        float height = aabb.upperBound.Y - aabb.lowerBound.Y;
        float area = width * height;
        float minSide = Math.Min(width, height);

        return area > StructuralAreaThreshold || minSide > StructuralMinSide;
    }

    /// <summary>
    /// Splits a large tile into a scattered cloud of debris pieces and removes
    /// the original. Returns true if the tile was massive and was shattered.
    /// </summary>
    private static bool TryShatterMassive(GameWorld world, ObjectData tile, Vector2 explosionCenter, float damage, string[] debris)
    {
        if (debris == null || debris.Length == 0)
        {
            return false;
        }

        AABB aabb;
        try { aabb = tile.GetWorldAABB(); }
        catch { return false; }

        Vector2 lower = aabb.lowerBound;
        Vector2 upper = aabb.upperBound;
        float width = upper.X - lower.X;
        float height = upper.Y - lower.Y;

        if (width <= MassiveTileSize && height <= MassiveTileSize)
        {
            return false;
        }

        Vector2 center = (lower + upper) * 0.5f;
        float halfMax = Math.Max(width, height) * 0.5f;

        // Approximate chunk count from area, capped.
        int chunks = (int)Math.Round((width * height) / (ChunkSize * ChunkSize));
        if (chunks < 3) chunks = 3;
        if (chunks > MaxChunksPerTile) chunks = MaxChunksPerTile;

        Vector2 dir = center - explosionCenter;
        float dist = dir.Length();
        if (dist > 0.001f)
        {
            dir /= dist;
        }
        else
        {
            dir = new Vector2(0f, -1f);
        }

        // Larger radius => SpawnDebris scatters pieces over the tile's footprint
        // and gives them more outward velocity.
        float scatterRadius = MathHelper.Clamp(halfMax, 8f, 32f);

        try
        {
            world.SpawnDebris(tile, center, scatterRadius, debris, (short)chunks, true);
        }
        catch
        {
            return false;
        }

        // Optional secondary burst biased away from the explosion centre to give
        // the cloud directional momentum (looks more like a blast, less like a puff).
        try
        {
            world.SpawnDebris(tile, center + dir * (halfMax * 0.5f), scatterRadius, debris, (short)Math.Max(2, chunks / 3), true);
        }
        catch { /* not critical */ }

        try { tile.Destroy(); }
        catch
        {
            try { tile.Remove(); } catch { /* tolerate */ }
        }

        return true;
    }

    private static bool IsExcludedBySubstring(string mapObjectId)
    {
        for (int i = 0; i < ExcludedSubstrings.Length; i++)
        {
            if (mapObjectId.IndexOf(ExcludedSubstrings[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string[] ResolveDebris(string mapObjectId)
    {
        for (int i = 0; i < WoodKeywords.Length; i++)
        {
            if (mapObjectId.IndexOf(WoodKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return WoodDebris;
            }
        }
        return StoneDebris;
    }

    private static void DislodgeTile(GameWorld world, ObjectData tile, Vector2 explosionCenter, float damage, string[] flavourDebris)
    {
        Vector2 tilePos;
        try { tilePos = tile.GetWorldPosition(); }
        catch { return; }

        Vector2 dir = tilePos - explosionCenter;
        float dist = dir.Length();
        if (dist > 0.001f)
        {
            dir /= dist;
        }
        else
        {
            dir = new Vector2((float)(Globals.Random.NextDouble() - 0.5), 1f);
            dir.Normalize();
        }

        // Switch tile to dynamic so it becomes a physics object.
        try
        {
            tile.ChangeBodyType(BodyType.Dynamic);
        }
        catch
        {
            // Some tiles can't be made dynamic (joints, portals, etc.) — leave them.
            return;
        }

        if (tile.Body == null)
        {
            return;
        }

        // Falloff: strongest at centre, weaker at edges.
        float falloff = MathHelper.Clamp(1f - dist / MaxRadius, 0.25f, 1f);
        float speed = Math.Min(MaxDislodgeSpeed, damage * DislodgeImpulseScale * falloff * 0.1f + 4f);

        Vector2 launchVel = dir * speed + new Vector2(0f, -1.5f); // small upward kick
        float angVel = (float)(Globals.Random.NextDouble() * 2.0 - 1.0) * 8f;

        try
        {
            tile.Body.SetLinearVelocity(launchVel);
            tile.Body.SetAngularVelocity(angVel);
            tile.Body.SetAwake(true);
        }
        catch
        {
            // Ignore — chunk still exists as dynamic even without impulse.
        }

        LaunchedChunks.Add(tile.ObjectID);

        // Flavour: a little tiny debris for the "shattered" feeling.
        if (FlavourDebrisPerTile > 0 && flavourDebris != null && flavourDebris.Length > 0)
        {
            try
            {
                world.SpawnDebris(tile, tilePos + dir * 2f, 6f, flavourDebris, FlavourDebrisPerTile, true);
            }
            catch { /* not critical */ }
        }
    }
}

/// <summary>
/// Global per-object hit patches that make any tile in
/// <see cref="EnvironmentDestructionHandler.LaunchedChunks"/> lethal when it slams
/// into a player, and clean up the tracking set on removal.
/// </summary>
[HarmonyPatch]
internal static class CrushingHitPatches
{
    /// <summary>Minimum impact speed (Box2D m/s) before a chunk damages a player.</summary>
    private const float MinSpeedForDamage = 4.5f;

    /// <summary>Damage per m/s of impact speed.</summary>
    private const float DamageScale = 1.4f;

    /// <summary>Cap on damage from a single chunk hit.</summary>
    private const float MaxDamage = 50f;

    /// <summary>Per-chunk cooldown between damage applications (ms).</summary>
    private const float CooldownMs = 250f;

    /// <summary>Last hit time per chunk, keyed by ObjectID.</summary>
    private static readonly Dictionary<int, float> _lastHit = new();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ObjectData), nameof(ObjectData.MissileHitPlayer))]
    private static void OnMissileHitPlayer(ObjectData __instance, Player player) => TryCrush(__instance, player);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ObjectData), nameof(ObjectData.ImpactHit))]
    private static void OnImpactHit(ObjectData __instance, ObjectData otherObject)
    {
        if (otherObject != null && otherObject.IsPlayer && otherObject.InternalData is Player p)
        {
            TryCrush(__instance, p);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ObjectData), nameof(ObjectData.OnRemoveObject))]
    private static void OnRemove(ObjectData __instance)
    {
        if (__instance == null)
        {
            return;
        }

        EnvironmentDestructionHandler.LaunchedChunks.Remove(__instance.ObjectID);
        _lastHit.Remove(__instance.ObjectID);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), "DisposeAllObjects")]
    private static void DisposeAllObjects() => _lastHit.Clear();

    private static void TryCrush(ObjectData chunk, Player player)
    {
        if (chunk == null || player == null || player.IsDead || player.IsRemoved)
        {
            return;
        }

        if (chunk.GameOwner == GameOwnerEnum.Client || chunk.Body == null)
        {
            return;
        }

        if (!EnvironmentDestructionHandler.LaunchedChunks.Contains(chunk.ObjectID))
        {
            return;
        }

        float now = chunk.GameWorld?.ElapsedTotalGameTime ?? 0f;
        if (_lastHit.TryGetValue(chunk.ObjectID, out float last) && now - last < CooldownMs)
        {
            return;
        }

        Vector2 velocity = chunk.Body.GetLinearVelocity();
        float speed = velocity.Length();
        if (speed < MinSpeedForDamage)
        {
            return;
        }

        float damage = speed * DamageScale;
        if (damage > MaxDamage)
        {
            damage = MaxDamage;
        }

        player.TakeMiscDamage(damage, sourceID: chunk.ObjectID);
        player.SimulateFallWithSpeed(velocity * 0.5f + new Vector2(0f, 2f));
        SoundHandler.PlaySound("ImpactFlesh", player.Position, chunk.GameWorld);

        _lastHit[chunk.ObjectID] = now;
    }
}
