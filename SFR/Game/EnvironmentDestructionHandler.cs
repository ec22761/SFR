using System;
using System.Collections.Generic;
using System.Linq;
using Box2D.XNA;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Objects;
using SFD.Sounds;
using SFR.Helper;
using SFR.Misc;
using AABB = Box2D.XNA.AABB;
using IObject = SFDGameScriptInterface.IObject;
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

    /// <summary>
    /// Global floor below which no static tile of any material can break. Acts
    /// as a fast early-out so low-damage explosions don't do AABB sweeps. Set to
    /// match the lowest per-material threshold (glass).
    /// </summary>
    private const float MinDamageToBreakStatic = 12f;

    /// <summary>Base impulse force applied to dislodged tiles. Scaled by damage/distance.</summary>
    private const float DislodgeImpulseScale = 0.75f;

    /// <summary>Hard cap on the launch speed applied to a dislodged tile.</summary>
    private const float MaxDislodgeSpeed = 12f;

    /// <summary>How much tiny flavour-debris to spawn alongside a dislodged tile.</summary>
    private const int FlavourDebrisPerTile = 3;

    /// <summary>Extra flavour-debris burst spawned at the cell centre when a fragment is dislodged.</summary>
    private const int FragmentFlavourDebris = 4;

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
    /// Maximum short-side size for a tile to be considered "long-thin" (a
    /// floor, ceiling, platform, or beam). Floors/ceilings in SFD maps are
    /// typically 8-16 world units tall; anything chunkier is treated as a
    /// wall / pillar and goes through the normal destruction path.
    /// </summary>
    private const float LongThinMaxShortSide = 18f;

    /// <summary>
    /// Minimum aspect ratio (long / short) at which a tile is classified as
    /// long-thin. Below this it's considered roughly square and handled
    /// normally; above this it's treated as a segmented slab whose pieces
    /// must be carved out one blast at a time.
    /// </summary>
    private const float LongThinAspectRatio = 2.5f;

    /// <summary>
    /// Number of tile-units a single blast carves out of a long-thin slab,
    /// minimum. Wider blasts (those whose projected footprint along the
    /// long axis exceeds one unit) carve more. The carved units become
    /// dynamic debris; the surrounding units stay static.
    /// </summary>
    private const int LongThinMinCarveUnits = 1;

    /// <summary>Chunks emitted per carved unit when splitting a long slab.</summary>
    private const int LongThinDebrisChunksPerUnit = 9;

    /// <summary>Hard cap on chunks emitted from one carve so a huge blast doesn't fog the screen with particles.</summary>
    private const int LongThinDebrisMaxChunks = 48;

    /// <summary>
    /// Hard cap on tile-unit cell count a 2D tiled tile can have and still be
    /// pre-fragmented into individual 1x1 cells. Above this, the tile is
    /// treated as too big to explode cell-by-cell (physics cost + visual
    /// chaos) and falls through to the massive-shatter path. Matches the
    /// <c>MaxSize = 64</c> used by the stock DestroyEnvironment map script.
    /// </summary>
    private const int FragmentMaxCells = 64;

    /// <summary>
    /// CustomID applied to every 1x1 cell spawned by the fragmentation path.
    /// Lets the fire tick cheaply locate live fragments via
    /// <see cref="GameWorld.GetObjectDataByCustomID"/> and ensures our own
    /// cells can still be destroyed on subsequent blasts (the eligibility
    /// filter whitelists this ID).
    /// </summary>
    private const string FragmentCustomId = "SFRFrag";

    /// <summary>
    /// Milliseconds between fire-collapse sweeps. The sweep is cheap (single
    /// custom-ID lookup) but there's no point running it every frame.
    /// </summary>
    private const float FireCollapseIntervalMs = 250f;

    /// <summary>
    /// Fraction of the blast radius used when punching a hole in the BG layer.
    /// Smaller than 1.0 so the "blown-up wall" hole stays contained within the
    /// destruction area rather than spilling past it.
    /// </summary>
    private const float BackgroundHoleScale = 0.7f;

    /// <summary>
    /// Jitter applied to the BG-hole radius per tile, as a fraction of the hole
    /// radius. 0.25 means each BG tile uses a threshold in [0.75r, 1.0r], giving
    /// the hole a naturally jagged edge instead of a perfect circle.
    /// </summary>
    private const float BackgroundHoleJitter = 0.25f;

    /// <summary>
    /// Damage threshold above which an explosion is considered "big enough" to
    /// tunnel sideways through adjacent geometry, not just collapse what's above.
    /// </summary>
    private const float HighPenetrationDamage = 150f;

    /// <summary>Max chain-collapse propagation depth for normal blasts.</summary>
    private const int ChainCollapseMaxDepthNormal = 1;

    /// <summary>Max chain-collapse propagation depth for high-damage blasts.</summary>
    private const int ChainCollapseMaxDepthHigh = 3;

    /// <summary>
    /// Multiplier applied to the blast radius when computing the shockwave push
    /// area. Shockwave reaches further than the destruction zone so players
    /// standing just outside the rubble still get knocked around.
    /// </summary>
    private const float ShockwaveRadiusMultiplier = 1.6f;

    /// <summary>Minimum explosion damage required to produce a shockwave push.</summary>
    private const float ShockwaveMinDamage = 25f;

    /// <summary>Peak outward speed (m/s) added to players at blast centre.</summary>
    private const float ShockwavePlayerSpeed = 9f;

    /// <summary>Peak outward speed (m/s) added to dynamic objects at blast centre.</summary>
    private const float ShockwaveObjectSpeed = 12f;

    /// <summary>Peak random angular velocity (rad/s) added to dynamic objects.</summary>
    private const float ShockwaveAngularKick = 6f;

    /// <summary>
    /// Reference damage used to scale shockwave strength. A blast at this damage
    /// hits full strength; smaller blasts scale down linearly.
    /// </summary>
    private const float ShockwaveReferenceDamage = 200f;

    /// <summary>
    /// Object IDs of tiles that have been dislodged by an explosion and should
    /// crush players on impact. Cleared at end of round.
    /// </summary>
    internal static readonly HashSet<int> LaunchedChunks = new();

    /// <summary>
    /// IDs of gargoyles that have already been chain-detonated this round so
    /// the recursive blast doesn't re-trigger the same gargoyle ad infinitum.
    /// </summary>
    private static readonly HashSet<int> _detonatedGargoyles = new();

    /// <summary>
    /// IDs of helicopters already shredded by our handler so we don't spawn
    /// metal showers twice for the same wreck.
    /// </summary>
    private static readonly HashSet<int> _shreddedHelicopters = new();

    /// <summary>
    /// Damage of the secondary explosion fired when a gargoyle is chain-detonated.
    /// Calibrated to be more than enough to chain into another gargoyle without
    /// nuking the rest of the level.
    /// </summary>
    private const float GargoyleChainDamage = 110f;

    /// <summary>Number of metal debris pieces sprayed when a helicopter is destroyed.</summary>
    private const int HelicopterMetalChunks = 32;

    /// <summary>Number of fully-rigid burning metal scrap tiles spawned alongside the debris.</summary>
    private const int HelicopterScrapTiles = 8;

    /// <summary>MapObjectID used for the rigid burning scrap pieces (small metal debris tile).</summary>
    private const string HelicopterScrapMapId = "MetalDebris00A";

    /// <summary>Substring lookups for special-case tiles. Case-insensitive.</summary>
    private static readonly string[] GargoyleKeywords = { "GARGOYLE" };
    private static readonly string[] HelicopterKeywords = { "HELICOPTER" };

    private static readonly string[] WoodDebris =
    {
        "WoodDebris00A", "WoodDebris00B", "WoodDebris00C", "WoodDebris00D", "WoodDebris00E"
    };

    private static readonly string[] StoneDebris =
    {
        "StoneDebris00A", "StoneDebris00B", "StoneDebris00C", "StoneDebris00D", "StoneDebris00E"
    };

    private static readonly string[] MetalDebris =
    {
        "MetalDebris00A", "MetalDebris00B", "MetalDebris00C", "MetalDebris00D", "MetalDebris00E"
    };

    private static readonly string[] GlassDebris =
    {
        "GlassShard00A"
    };

    /// <summary>
    /// Coarse material classification used to decide break thresholds, debris,
    /// and cosmetic feel. Detected from <see cref="ObjectData.MapObjectID"/>
    /// substrings — no runtime introspection of <c>Tile.Material</c> to keep the
    /// path robust across custom maps.
    /// </summary>
    private enum MaterialKind { Stone, Wood, Metal, Glass, Concrete, Marble, Ice, Dirt }

    /// <summary>
    /// Per-material destruction parameters. <see cref="Threshold"/> replaces the
    /// old single <c>MinDamageToBreakStatic</c>: fragile materials (glass, ice)
    /// give way to small-arms fire while tough materials (metal, concrete)
    /// require sizeable explosions.
    /// </summary>
    private sealed class MaterialInfo
    {
        public MaterialKind Kind { get; }
        /// <summary>Minimum explosion damage required to destroy tiles of this material.</summary>
        public float Threshold { get; }
        /// <summary>Flavour/shatter debris set emitted when the tile breaks.</summary>
        public string[] Debris { get; }

        public MaterialInfo(MaterialKind kind, float threshold, string[] debris)
        {
            Kind = kind;
            Threshold = threshold;
            Debris = debris;
        }
    }

    private static readonly MaterialInfo MatGlass    = new(MaterialKind.Glass,    12f, GlassDebris);
    private static readonly MaterialInfo MatIce      = new(MaterialKind.Ice,      20f, GlassDebris);
    private static readonly MaterialInfo MatDirt     = new(MaterialKind.Dirt,     30f, StoneDebris);
    private static readonly MaterialInfo MatWood     = new(MaterialKind.Wood,     40f, WoodDebris);
    private static readonly MaterialInfo MatStone    = new(MaterialKind.Stone,    55f, StoneDebris);
    private static readonly MaterialInfo MatMetal    = new(MaterialKind.Metal,    80f, MetalDebris);
    private static readonly MaterialInfo MatConcrete = new(MaterialKind.Concrete, 90f, StoneDebris);
    private static readonly MaterialInfo MatMarble   = new(MaterialKind.Marble,  110f, StoneDebris);

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
    /// (ladders, water, triggers, spawn points, ropes, etc.). GLASS, ACID and
    /// SHADOW are excluded to match the DestroyEnvironment script's filter —
    /// glass panels have their own break behaviour, acid/shadow surfaces are
    /// visual-only.
    /// </summary>
    private static readonly string[] ExcludedSubstrings =
    {
        "LADDER", "ROPE", "WATER", "LAVA", "ELEVATOR", "TRIGGER", "SPAWN",
        "PORTAL", "TEXT", "SIGN", "MARKER", "INDICATOR", "INVISIBLE",
        "SCRIPT", "AREA", "GHOST", "BUTTON", "DRAW",
        "GLASS", "ACID", "SHADOW",
    };

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.TriggerExplosion), new[] { typeof(Vector2), typeof(float) })]
    private static void AfterTriggerExplosion(GameWorld __instance, Vector2 worldPosition, float explosionDamage)
        => HandleExplosion(__instance, worldPosition, explosionDamage);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.TriggerExplosion), new[] { typeof(Vector2), typeof(float), typeof(bool) })]
    private static void AfterTriggerExplosion2(GameWorld __instance, Vector2 worldPosition, float explosionDamage)
        => HandleExplosion(__instance, worldPosition, explosionDamage);

    // ---- Prefixes: pre-fragment ObjectTileWall before SFD's TriggerExplosion
    // ---- runs. SFD treats ObjectTileWall as a single destructible body, so a
    // ---- blast that hits any part of the wall destroys the WHOLE column at
    // ---- once. By splitting the wall into 1x1 cells before SFD applies
    // ---- explosion damage, only the cells actually inside the blast radius
    // ---- get destroyed by SFD; the rest of the wall remains standing.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.TriggerExplosion), new[] { typeof(Vector2), typeof(float) })]
    private static void BeforeTriggerExplosion(GameWorld __instance, Vector2 worldPosition, float explosionDamage)
        => PreFragmentTileWalls(__instance, worldPosition, explosionDamage);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.TriggerExplosion), new[] { typeof(Vector2), typeof(float), typeof(bool) })]
    private static void BeforeTriggerExplosion2(GameWorld __instance, Vector2 worldPosition, float explosionDamage)
        => PreFragmentTileWalls(__instance, worldPosition, explosionDamage);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), "DisposeAllObjects")]
    private static void DisposeAllObjects()
    {
        LaunchedChunks.Clear();
        _detonatedGargoyles.Clear();
        _shreddedHelicopters.Clear();
    }

    /// <summary>
    /// Accumulated milliseconds since the last burning-fragment sweep.
    /// </summary>
    private static float s_fireSweepElapsedMs;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.Update))]
    private static void AfterGameWorldUpdate(GameWorld __instance, float chunkMs)
    {
        if (__instance == null || __instance.GameOwner == GameOwnerEnum.Client)
        {
            return;
        }

        s_fireSweepElapsedMs += chunkMs;
        if (s_fireSweepElapsedMs < FireCollapseIntervalMs)
        {
            return;
        }
        s_fireSweepElapsedMs = 0f;

        BurnCollapseFragments(__instance);
    }

    /// <summary>
    /// Converts burning 1x1 fragment tiles to dynamic so they fall / tumble.
    /// Mirrors the DestroyEnvironment script's burn check — instead of a
    /// per-fragment timer, we sweep every <see cref="FireCollapseIntervalMs"/>
    /// milliseconds.
    /// </summary>
    private static void BurnCollapseFragments(GameWorld world)
    {
        List<ObjectData> frags;
        try
        {
            frags = world.GetObjectDataByCustomID(FragmentCustomId)?.ToList();
        }
        catch
        {
            return;
        }
        if (frags == null || frags.Count == 0) return;

        foreach (ObjectData frag in frags)
        {
            if (frag == null || frag.RemovalInitiated) continue;
            if (!frag.IsStatic) continue;

            bool burning;
            try { burning = frag.Fire.IsBurning; }
            catch { continue; }

            if (!burning) continue;

            try
            {
                frag.ChangeBodyType(BodyType.Dynamic);
                if (frag.Body != null)
                {
                    frag.Body.SetAwake(true);
                }
            }
            catch { /* tolerate quirky subclasses */ }
        }
    }

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
        bool anyDestroyed = false;
        var seeds = new List<CollapseSeed>();

        // Sanctuary: track every brick tile actually inside the blast so we
        // can chain-detonate gargoyles that sit right next to them (the
        // gargoyles are usually just outside the blast radius itself).
        bool isSanctuary = world.MapName != null
            && world.MapName.IndexOf("Sanctuary", StringComparison.OrdinalIgnoreCase) >= 0;
        List<Vector2> sanctuaryBrickHits = isSanctuary ? new List<Vector2>() : null;

        foreach (ObjectData obj in objects)
        {
            if (obj == null || obj.IsPlayer || obj.RemovalInitiated)
            {
                continue;
            }

            // Per-map / per-tile protection (e.g. Sanctuary edge brick walls).
            if (IsProtectedByMap(world, obj))
            {
                continue;
            }

            if (obj.Destructable)
            {
                // Helicopters: shred into a shower of burning metal scrap
                // instead of just taking script damage. Once shredded the
                // wreck still gets a damage tick so SFD's normal destroy
                // pipeline can finish it.
                if (TryShredHelicopter(world, obj, worldPosition))
                {
                    anyDestroyed = true;
                    // One-shot the wreck: pile on enough script damage that
                    // SFD's normal destroy pipeline finishes the helicopter
                    // on this same hit instead of leaving it intact for a
                    // second bazooka round.
                    try { obj.DealScriptDamage(9999f); } catch { /* tolerate quirky subclasses */ }
                    continue;
                }

                // Already destructible — pile on extra damage so explosions finish it.
                try { obj.DealScriptDamage(damage); } catch { /* tolerate quirky subclasses */ }
                continue;
            }

            // Gargoyles propagate: when an explosion touches one, it
            // detonates a secondary blast at its centre. Recursive — chains
            // through nearby gargoyles. Tracked per-round to avoid loops.
            if (TryChainDetonateGargoyle(world, obj))
            {
                anyDestroyed = true;
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

            MaterialInfo material = ResolveMaterial(mapId);
            if (damage < material.Threshold)
            {
                continue;
            }

            AABB tileAabb;
            try { tileAabb = obj.GetWorldAABB(); }
            catch { continue; }

            // Sanctuary: remember any brick we're about to hit so we can
            // later chain-detonate adjacent gargoyles.
            if (sanctuaryBrickHits != null &&
                mapId.IndexOf("BRICK", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sanctuaryBrickHits.Add((tileAabb.lowerBound + tileAabb.upperBound) * 0.5f);
            }

            Vector2 outward = ((tileAabb.lowerBound + tileAabb.upperBound) * 0.5f) - worldPosition;
            if (outward.LengthSquared() > 0.0001f) outward.Normalize();
            else outward = new Vector2(0f, -1f);

            // Prefer cell fragmentation for any multi-unit tile with a
            // manageable cell count: 1D slabs (long floors/ceilings/beams),
            // 2D tiled walls, and brick-style panels all decompose into
            // independent 1x1 static tiles. Cells inside the blast fly as
            // debris; cells outside stay as static pieces so the roof/wall
            // visibly persists with a gap punched through it. This matches
            // the DestroyEnvironment script's model.
            if (CanFragmentIntoCells(obj))
            {
                if (TryFragmentIntoCells(world, obj, tileAabb, worldPosition, radius, damage, material.Debris, seeds))
                {
                    anyDestroyed = true;
                }
                continue;
            }

            // Oversized 1D slabs (cell count > FragmentMaxCells) fall back to
            // in-place axis-split so we don't spawn hundreds of bodies at once.
            float sp_w = tileAabb.upperBound.X - tileAabb.lowerBound.X;
            float sp_h = tileAabb.upperBound.Y - tileAabb.lowerBound.Y;
            if (TryGetSplitAxis(obj, sp_w, sp_h,
                    out float longAxis, out float shortAxis, out bool horizontal))
            {
                if (HandleLongThinHit(world, obj, tileAabb, worldPosition, radius, damage, material.Debris, longAxis, shortAxis, horizontal, out bool fullyDestroyed))
                {
                    anyDestroyed = true;
                    // Only seed chain-collapse when the slab was removed
                    // entirely. A partial split leaves the surviving units
                    // static and still load-bearing, so probing above it
                    // would incorrectly launch anything resting on them.
                    if (fullyDestroyed)
                    {
                        seeds.Add(new CollapseSeed(tileAabb, outward));
                    }
                }
                continue;
            }

            if (TryShatterMassive(world, obj, worldPosition, damage, material.Debris))
            {
                anyDestroyed = true;
                seeds.Add(new CollapseSeed(tileAabb, outward));
                continue;
            }

            if (DislodgeTile(world, obj, worldPosition, damage, material.Debris))
            {
                anyDestroyed = true;
                seeds.Add(new CollapseSeed(tileAabb, outward));
            }
        }

        // Chain collapse: tiles directly above destroyed ones (and for big blasts,
        // adjacent tiles in the blast direction) lose support and come down.
        if (seeds.Count > 0)
        {
            PropagateCollapse(world, seeds, damage);
        }

        // Always sweep a wider radius around the blast centre for gargoyles
        // and chain-detonate any we find. The per-blast AABB query above is
        // sized for the destruction radius, but gargoyles should react to
        // any explosion in their general vicinity, not just direct hits.
        ChainGargoylesNearPoint(world, worldPosition);

        // Sanctuary: if the blast destroyed any brick tiles, find gargoyles
        // next to those bricks and detonate them. The gargoyles themselves
        // may sit just outside the original blast radius, so we do a wider
        // sweep around each destroyed brick centre.
        if (sanctuaryBrickHits != null && sanctuaryBrickHits.Count > 0)
        {
            ChainGargoylesNearBricks(world, sanctuaryBrickHits);
        }

        // Shockwave push: even tiles that survived still feel the blast — players
        // and dynamic objects in a larger area get shoved outward.
        ApplyShockwave(world, worldPosition, radius * ShockwaveRadiusMultiplier, damage);

        // If the blast actually punched through foreground geometry, drop a
        // jagged visual "scar" decal at the blast site. It renders between the
        // FAR and BG layers so it's visible only where foreground / BG tiles
        // have been blown away — revealing a dynamically-shaped dark hole
        // through the whole background stack down to the sky.
        if (anyDestroyed)
        {
            BlastScarRenderer.AddScar(worldPosition, radius * BackgroundHoleScale);
        }
    }

    /// <summary>
    /// Returns true if the given tile is too large to safely destroy. A tile is
    /// "structural" when it is chunky in BOTH dimensions — i.e. its shorter
    /// side exceeds <see cref="StructuralMinSide"/> AND it has no tile-unit
    /// multiplier we can split along. Splittable tiles (the map author tiled
    /// them out of multiple base units) are NEVER structural: they go through
    /// the split path so a blast punches a hole and leaves the rest standing.
    /// The old area rule remains as a sanity cap for genuinely irreducible
    /// oversized geometry.
    /// </summary>
    private static bool IsStructural(ObjectData tile)
    {
        AABB aabb;
        try { aabb = tile.GetWorldAABB(); }
        catch { return false; }

        float width = aabb.upperBound.X - aabb.lowerBound.X;
        float height = aabb.upperBound.Y - aabb.lowerBound.Y;

        // Splittable tiles (multi-unit along some axis, including chunky
        // tiled walls) are destructible piece-by-piece.
        if (TryGetSplitAxis(tile, width, height, out _, out _, out _))
        {
            return false;
        }

        // 2D tiled walls / floors decompose into individual cells.
        if (CanFragmentIntoCells(tile))
        {
            return false;
        }

        float minSide = Math.Min(width, height);

        // Chunky in both dimensions and not splittable => treat as a real wall.
        if (minSide > StructuralMinSide)
        {
            return true;
        }

        // Fallback: non-chunky oddities beyond the area cap.
        float area = width * height;
        return area > StructuralAreaThreshold;
    }

    /// <summary>
    /// Decides whether a tile should be destroyed by the split path, and if
    /// so, which axis to split along. Only triggers for tiles that are
    /// multi-unit along exactly ONE axis (1D slabs — floors, ceilings,
    /// beams). Tiles that are multi-unit along BOTH axes (2D tiled walls)
    /// are handled by the fragmentation path instead (see
    /// <see cref="TryFragmentIntoCells"/>), which decomposes them into
    /// independent 1x1 cells. Falls back to the legacy long-thin
    /// aspect-ratio heuristic for authored-wide single-unit tiles.
    /// <paramref name="horizontal"/> is true when width is the split (long) axis.
    /// </summary>
    private static bool TryGetSplitAxis(
        ObjectData tile,
        float width,
        float height,
        out float longAxis,
        out float shortAxis,
        out bool horizontal)
    {
        longAxis = 0f;
        shortAxis = 0f;
        horizontal = true;

        int xMul = 1, yMul = 1;
        try { tile.GetObjectSizeMultiplier(out xMul, out yMul); }
        catch { /* keep defaults */ }

        bool xMulti = xMul >= 2;
        bool yMulti = yMul >= 2;

        // Pure 1D slab — one axis tiled, the other is a single unit.
        // Pick the tiled axis as the long axis.
        if (xMulti ^ yMulti)
        {
            if (xMulti)
            {
                longAxis = width;
                shortAxis = height;
                horizontal = true;
            }
            else
            {
                longAxis = height;
                shortAxis = width;
                horizontal = false;
            }
            return shortAxis > 0.0001f;
        }

        // 2D tiled — let the fragmentation path handle it.
        if (xMulti && yMulti)
        {
            return false;
        }

        // No useful multiplier — fall back to legacy long-thin detection so
        // tiles authored as a single wide frame still get segmented.
        return IsLongThin(width, height, out longAxis, out shortAxis, out horizontal);
    }

    /// <summary>
    /// Returns true if the tile was authored as a grid of base units with
    /// at most <see cref="FragmentMaxCells"/> cells, qualifying it for the
    /// cell-fragmentation destruction path. Works for 1D slabs (Nx1 or 1xN)
    /// and 2D tiled panels alike — single-cell tiles are rejected because
    /// there is nothing to fragment.
    /// </summary>
    private static bool CanFragmentIntoCells(ObjectData tile)
    {
        int xMul = 1, yMul = 1;
        try { tile.GetObjectSizeMultiplier(out xMul, out yMul); }
        catch { return false; }

        if (xMul < 1 || yMul < 1) return false;
        int cells = xMul * yMul;
        return cells > 1 && cells <= FragmentMaxCells;
    }

    /// <summary>
    /// Classifies a tile as long-thin based on its width/height. Returns true
    /// for floor/ceiling/platform/beam-shaped tiles (short side small, long
    /// side substantially larger). <paramref name="horizontal"/> is true when
    /// width is the long axis (floor/ceiling shape) and false for vertical
    /// beams.
    /// </summary>
    private static bool IsLongThin(float width, float height, out float longAxis, out float shortAxis, out bool horizontal)
    {
        if (width >= height)
        {
            longAxis = width;
            shortAxis = height;
            horizontal = true;
        }
        else
        {
            longAxis = height;
            shortAxis = width;
            horizontal = false;
        }

        if (shortAxis <= 0.0001f)
        {
            return false;
        }
        return shortAxis <= LongThinMaxShortSide && (longAxis / shortAxis) >= LongThinAspectRatio;
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

    /// <summary>
    /// True split destruction for long-thin tiles (floors, ceilings, platforms,
    /// beams). SFD tiles are tile-multiplier based: a long beam is one
    /// <see cref="ObjectData"/> with <c>FixtureSizeXMultiplier &gt; 1</c> that
    /// renders the same base frame N times. We exploit that to actually carve
    /// the slab into separate static pieces:
    ///   1. Project the blast onto the tile's long axis, expressed in
    ///      tile-unit indices.
    ///   2. Spawn dynamic debris for the carved-out middle units (these
    ///      become the airborne rubble).
    ///   3. Resize the original tile down to the LARGER surviving side and
    ///      reposition its body so that side stays exactly where it was.
    ///   4. If a smaller surviving side exists on the other end, spawn a NEW
    ///      static tile of the same MapObjectID for it. Net effect: one long
    ///      slab becomes two shorter static slabs with a hole between them,
    ///      while the airborne rubble looks like the bit that got blown out.
    /// Returns true if the original tile was destroyed or replaced (so
    /// chain-collapse can seed off the now-missing geometry).
    /// </summary>
    private static bool HandleLongThinHit(
        GameWorld world,
        ObjectData tile,
        AABB aabb,
        Vector2 explosionCenter,
        float blastRadius,
        float damage,
        string[] debris,
        float longAxis,
        float shortAxis,
        bool horizontal,
        out bool fullyDestroyed)
    {
        fullyDestroyed = false;
        if (longAxis <= 0.0001f || debris == null || debris.Length == 0)
        {
            return false;
        }

        // Read the tile's current size multiplier. Without it we have no way
        // to split discretely; fall back to whole-slab destruction.
        int xMul = 1, yMul = 1;
        try { tile.GetObjectSizeMultiplier(out xMul, out yMul); }
        catch { /* leave defaults */ }

        int longMul = horizontal ? xMul : yMul;
        int shortMul = horizontal ? yMul : xMul;
        if (longMul < 2)
        {
            // Nothing to split — slab is a single tile-unit. Destroy whole.
            fullyDestroyed = true;
            return DestroyLongThinWhole(world, tile, aabb, explosionCenter, damage, debris);
        }

        float unitLength = longAxis / longMul;
        if (unitLength <= 0.001f)
        {
            fullyDestroyed = true;
            return DestroyLongThinWhole(world, tile, aabb, explosionCenter, damage, debris);
        }

        // Project blast centre onto the tile's long axis in world coordinates.
        float axisMin = horizontal ? aabb.lowerBound.X : aabb.lowerBound.Y;
        float axisMax = horizontal ? aabb.upperBound.X : aabb.upperBound.Y;
        float blastAxisPos = horizontal ? explosionCenter.X : explosionCenter.Y;

        // Perpendicular distance from blast centre to the tile's long-axis line
        // shrinks the effective radius along the long axis (pythagoras).
        float perpCenter = horizontal
            ? (aabb.lowerBound.Y + aabb.upperBound.Y) * 0.5f
            : (aabb.lowerBound.X + aabb.upperBound.X) * 0.5f;
        float perpBlast = horizontal ? explosionCenter.Y : explosionCenter.X;
        float perpDist = Math.Max(0f, Math.Abs(perpBlast - perpCenter) - shortAxis * 0.5f);
        if (perpDist >= blastRadius)
        {
            return false;
        }

        float halfExtent = (float)Math.Sqrt(blastRadius * blastRadius - perpDist * perpDist);
        float segStart = Math.Max(axisMin, blastAxisPos - halfExtent);
        float segEnd = Math.Min(axisMax, blastAxisPos + halfExtent);
        if (segEnd <= segStart)
        {
            return false;
        }

        // Convert blast overlap to tile-unit indices [hitStart, hitEnd) and
        // ensure at least LongThinMinCarveUnits get carved out.
        int hitStartUnit = (int)Math.Floor((segStart - axisMin) / unitLength);
        int hitEndUnit = (int)Math.Ceiling((segEnd - axisMin) / unitLength);
        if (hitStartUnit < 0) hitStartUnit = 0;
        if (hitEndUnit > longMul) hitEndUnit = longMul;
        if (hitEndUnit - hitStartUnit < LongThinMinCarveUnits)
        {
            // Centre the minimum carve on the blast position.
            int blastUnit = (int)Math.Floor((blastAxisPos - axisMin) / unitLength);
            if (blastUnit < 0) blastUnit = 0;
            if (blastUnit >= longMul) blastUnit = longMul - 1;
            hitStartUnit = Math.Max(0, blastUnit - LongThinMinCarveUnits / 2);
            hitEndUnit = Math.Min(longMul, hitStartUnit + LongThinMinCarveUnits);
            hitStartUnit = Math.Max(0, hitEndUnit - LongThinMinCarveUnits);
        }

        int leftUnits = hitStartUnit;
        int rightUnits = longMul - hitEndUnit;
        int hitUnits = hitEndUnit - hitStartUnit;
        if (hitUnits <= 0)
        {
            return false;
        }

        // Spawn dynamic debris for the carved middle segment.
        float carveStart = axisMin + hitStartUnit * unitLength;
        float carveEnd = axisMin + hitEndUnit * unitLength;
        Vector2 carveCenter = horizontal
            ? new Vector2((carveStart + carveEnd) * 0.5f, perpCenter)
            : new Vector2(perpCenter, (carveStart + carveEnd) * 0.5f);

        int chunks = Math.Min(LongThinDebrisMaxChunks, Math.Max(2, hitUnits * LongThinDebrisChunksPerUnit));
        float scatterRadius = MathHelper.Clamp((carveEnd - carveStart) * 0.5f, 6f, 16f);
        try { world.SpawnDebris(tile, carveCenter, scatterRadius, debris, (short)chunks, true); }
        catch { /* tolerate */ }

        // Visual scar so the BG layer shows through the new gap.
        float scarRadius = Math.Max(shortAxis * 0.75f,
            Math.Min((carveEnd - carveStart) * 0.5f, blastRadius * BackgroundHoleScale));
        BlastScarRenderer.AddScar(carveCenter, scarRadius);

        // Compute body-anchor offset relative to the tile's AABB lower bound.
        // SFD tiles are anchored at one corner of their footprint; using the
        // AABB.lower-relative offset works regardless of which corner.
        Vector2 currentBodyPos_box2d;
        try { currentBodyPos_box2d = tile.Body.GetPosition(); }
        catch
        {
            // No body -> fall back to whole-slab destroy.
            fullyDestroyed = true;
            return DestroyLongThinWhole(world, tile, aabb, explosionCenter, damage, debris);
        }

        Vector2 currentBodyPos_world = Converter.ConvertBox2DToWorld(currentBodyPos_box2d);
        Vector2 anchorOffset_world = currentBodyPos_world - aabb.lowerBound;
        float bodyAngle = 0f;
        try { bodyAngle = tile.Body.GetAngle(); } catch { /* tolerate */ }

        // No survivors at all => destroy outright.
        if (leftUnits == 0 && rightUnits == 0)
        {
            try { tile.Destroy(); } catch { try { tile.Remove(); } catch { /* tolerate */ } }
            fullyDestroyed = true;
            return true;
        }

        // Pick the larger surviving side as the "primary" — that's the one we
        // resize the original tile down to (no need to spawn a fresh body).
        // The smaller surviving side becomes a new spawned tile.
        int primaryStartUnit, primaryUnits, secondaryStartUnit, secondaryUnits;
        if (leftUnits >= rightUnits)
        {
            primaryStartUnit = 0;
            primaryUnits = leftUnits;
            secondaryStartUnit = hitEndUnit;
            secondaryUnits = rightUnits;
        }
        else
        {
            primaryStartUnit = hitEndUnit;
            primaryUnits = rightUnits;
            secondaryStartUnit = 0;
            secondaryUnits = leftUnits;
        }

        // Resize the original tile in place to the primary surviving side.
        bool resized = ResizeLongThinInPlace(
            tile, horizontal, primaryStartUnit, primaryUnits, shortMul,
            aabb, anchorOffset_world, unitLength, bodyAngle);

        if (!resized)
        {
            // Couldn't resize for some reason — fall back to whole destroy so
            // we don't leave an inconsistent collision body.
            fullyDestroyed = true;
            return DestroyLongThinWhole(world, tile, aabb, explosionCenter, damage, debris);
        }

        // Spawn a new static tile for the smaller surviving side, if any.
        if (secondaryUnits > 0)
        {
            SpawnLongThinSegment(
                world, tile.MapObjectID, horizontal, secondaryStartUnit, secondaryUnits, shortMul,
                aabb, anchorOffset_world, unitLength, bodyAngle);
        }

        return true;
    }

    /// <summary>
    /// Resizes a long-thin tile in place: changes its size multiplier,
    /// recreates the fixture at the new size, and shifts the body so that
    /// the surviving units stay anchored where they were before the carve.
    /// </summary>
    private static bool ResizeLongThinInPlace(
        ObjectData tile,
        bool horizontal,
        int startUnit,
        int units,
        int shortMul,
        AABB oldAabb,
        Vector2 anchorOffset_world,
        float unitLength,
        float bodyAngle)
    {
        if (units <= 0 || tile.Body == null)
        {
            return false;
        }

        int newX = horizontal ? units : shortMul;
        int newY = horizontal ? shortMul : units;

        // New AABB lower bound after carve = old lower + startUnit * unitLength
        // along the long axis. Body position = newAABBLower + anchorOffset.
        Vector2 newAabbLower_world = oldAabb.lowerBound;
        if (horizontal) newAabbLower_world.X += startUnit * unitLength;
        else            newAabbLower_world.Y += startUnit * unitLength;

        Vector2 newBodyPos_world = newAabbLower_world + anchorOffset_world;
        Vector2 newBodyPos_box2d = new(
            Converter.WorldToBox2D(newBodyPos_world.X),
            Converter.WorldToBox2D(newBodyPos_world.Y));

        try
        {
            tile.SetObjectSizeMultiplier(newX, newY);
            tile.RecreateFixture(newX, newY);
            tile.Body.SetTransform(newBodyPos_box2d, bodyAngle);
            tile.Body.SetAwake(true);
        }
        catch
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Spawns a fresh static tile of the given <paramref name="mapObjectId"/>
    /// representing a surviving sub-segment of a split long-thin slab. The
    /// new tile gets the same anchor/orientation as the original so it sits
    /// exactly where the surviving piece used to be inside the larger slab.
    /// </summary>
    private static void SpawnLongThinSegment(
        GameWorld world,
        string mapObjectId,
        bool horizontal,
        int startUnit,
        int units,
        int shortMul,
        AABB oldAabb,
        Vector2 anchorOffset_world,
        float unitLength,
        float bodyAngle)
    {
        if (units <= 0 || string.IsNullOrEmpty(mapObjectId))
        {
            return;
        }

        int newX = horizontal ? units : shortMul;
        int newY = horizontal ? shortMul : units;

        Vector2 newAabbLower_world = oldAabb.lowerBound;
        if (horizontal) newAabbLower_world.X += startUnit * unitLength;
        else            newAabbLower_world.Y += startUnit * unitLength;

        Vector2 newBodyPos_world = newAabbLower_world + anchorOffset_world;

        try
        {
            ObjectData child = ObjectData.CreateNew(new ObjectDataStartParams(
                world.IDCounter.NextID(), 0, 0, mapObjectId, world.GameOwner));
            if (child == null)
            {
                return;
            }
            child.SetObjectSizeMultiplier(newX, newY);
            world.CreateTile(new SpawnObjectInformation(child, newBodyPos_world, bodyAngle));
        }
        catch
        {
            // Tolerate — losing one of two split halves is better than crashing.
        }
    }

    /// <summary>
    /// Fallback used when a long-thin tile can't be split (single-unit slab,
    /// missing body, etc.). Distributes debris along the slab's full length
    /// and removes it as a single body. Returns true so chain-collapse can
    /// proceed.
    /// </summary>
    private static bool DestroyLongThinWhole(GameWorld world, ObjectData tile, AABB aabb, Vector2 explosionCenter, float damage, string[] debris)
    {
        Vector2 center = (aabb.lowerBound + aabb.upperBound) * 0.5f;
        float width = aabb.upperBound.X - aabb.lowerBound.X;
        float height = aabb.upperBound.Y - aabb.lowerBound.Y;
        float scatter = MathHelper.Clamp(Math.Max(width, height) * 0.25f, 6f, 14f);
        try { world.SpawnDebris(tile, center, scatter, debris, (short)Math.Min(LongThinDebrisMaxChunks, 6), true); }
        catch { /* tolerate */ }

        Vector2 outward = center - explosionCenter;
        if (outward.LengthSquared() > 0.0001f) outward.Normalize();
        else outward = new Vector2(0f, -1f);
        float falloff = MathHelper.Clamp(1f - (center - explosionCenter).Length() / MaxRadius, 0.25f, 1f);
        float speed = Math.Min(MaxDislodgeSpeed, damage * DislodgeImpulseScale * falloff * 0.08f + 3f);
        Vector2 launchVel = outward * speed + new Vector2(0f, -1f);
        float angVel = (float)(Globals.Random.NextDouble() * 2.0 - 1.0) * 6f;
        return LaunchAsDynamic(world, tile, launchVel, angVel, debris, 0);
    }

    /// <summary>
    /// Script-style fragmentation path. Decomposes a 2D tiled tile (both
    /// axes &ge; 2) into a grid of independent 1x1 static tiles, then
    /// immediately destroys whichever cells fall inside the explosion
    /// radius. Surviving cells stay as static 1x1 tiles that the normal
    /// dislodge path can handle on subsequent blasts.
    /// <para>
    /// Tiles wired into custom-map logic (referenced by an
    /// <see cref="ObjectOnDestroyedTrigger"/>) are left intact so scripted
    /// maps continue to function.
    /// </para>
    /// </summary>
    /// <summary>
    /// PREFIX-side hook for <see cref="GameWorld.TriggerExplosion"/>: scans for
    /// any <see cref="ObjectTileWall"/> whose body footprint overlaps the
    /// (about-to-happen) blast area and pre-splits it into 1x1 cell tiles.
    /// SFD treats the whole wall as a single destructible body, so without
    /// this pre-split a blast that touches the top of a tall column would
    /// destroy the entire column. After splitting, SFD's explosion damage
    /// only kills the cells actually inside the blast radius.
    /// </summary>
    /// <summary>
    /// MapObjectIDs that should always be treated as indestructible scenery,
    /// regardless of map. Currently just <c>STONE07A</c> — the heavy
    /// stone/brick column tile used as decorative load-bearing scenery on
    /// Sanctuary's map edges (and similar maps). Without this guard our
    /// fragmentation/dislodge code happily blew the columns away when the
    /// player exploded anything next to them.
    /// </summary>
    private static readonly HashSet<string> IndestructibleMapObjectIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "STONE07A",
    };

    /// <summary>
    /// Returns true when the given object is a tile the active map
    /// explicitly protects from being destroyed by our handler.
    /// </summary>
    private static bool IsProtectedByMap(GameWorld world, ObjectData obj)
    {
        if (obj == null)
        {
            return false;
        }

        string mapId = obj.MapObjectID;
        if (!string.IsNullOrEmpty(mapId) && IndestructibleMapObjectIds.Contains(mapId))
        {
            return true;
        }

        return false;
    }

    // ---- Block SFD's native damage paths for map-protected walls so bullets,
    // ---- explosions, and script damage all bounce off Sanctuary's edge bricks.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ObjectTileWall), nameof(ObjectTileWall.ExplosionHit))]
    private static bool BeforeWallExplosionHit(ObjectTileWall __instance)
        => !IsProtectedByMap(__instance?.GameWorld, __instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ObjectTileWall), nameof(ObjectTileWall.ProjectileHit))]
    private static bool BeforeWallProjectileHit(ObjectTileWall __instance)
        => !IsProtectedByMap(__instance?.GameWorld, __instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ObjectTileWall), nameof(ObjectTileWall.Damage))]
    private static bool BeforeWallDamage(ObjectTileWall __instance)
        => !IsProtectedByMap(__instance?.GameWorld, __instance);

    private static void PreFragmentTileWalls(GameWorld world, Vector2 worldPosition, float damage)
    {
        if (world == null || world.GameOwner == GameOwnerEnum.Client || damage <= 0f)
        {
            return;
        }

        // Use a slightly inflated query area so walls only just clipped by the
        // blast still get pre-fragmented (otherwise their body centre might
        // land outside the AABB and we'd miss them).
        float radius = Math.Min(MaxRadius, damage * RadiusPerDamage);
        if (radius < 6f) radius = 6f;
        float queryRadius = radius * 1.25f;
        AABB.Create(out AABB area, worldPosition, worldPosition, queryRadius);

        List<ObjectData> objects;
        try
        {
            objects = world.GetObjectDataByArea(area, false, PhysicsLayer.Active).ToList();
        }
        catch { return; }

        foreach (ObjectData obj in objects)
        {
            if (obj is not ObjectTileWall || obj.RemovalInitiated)
            {
                continue;
            }

            // A single-cell wall has nothing to fragment into.
            if (!CanFragmentIntoCells(obj))
            {
                continue;
            }

            // Per-map protection: don't pre-fragment walls the map
            // explicitly protects (e.g. Sanctuary's far-left/right brick
            // columns), otherwise SFD's TriggerExplosion would still kill
            // the resulting cells inside the blast radius.
            if (IsProtectedByMap(world, obj))
            {
                continue;
            }

            // Map-trigger targets stay intact so scripted maps don't break.
            if (IsMapTriggerTarget(world, obj))
            {
                continue;
            }

            // Spawn 1x1 cell children in place of the parent wall. Pass
            // explosionCenter == worldPosition with radius 0 so no cell is
            // dislodged from this prefix call — SFD's TriggerExplosion runs
            // immediately after and will damage the cells that fall inside
            // the real blast radius.
            try
            {
                MaterialInfo material = ResolveMaterial(obj.MapObjectID ?? string.Empty);
                AABB tileAabb = obj.GetWorldAABB();
                var dummySeeds = new List<CollapseSeed>();
                TryFragmentIntoCells(world, obj, tileAabb, worldPosition, /*radius*/ 0f, damage, material.Debris, dummySeeds);
            }
            catch { /* one wall failing isn't fatal */ }
        }
    }

    private static bool TryFragmentIntoCells(
        GameWorld world,
        ObjectData tile,
        AABB tileAabb,
        Vector2 explosionCenter,
        float radius,
        float damage,
        string[] debris,
        List<CollapseSeed> seeds)
    {
        int xMul, yMul;
        try { tile.GetObjectSizeMultiplier(out xMul, out yMul); }
        catch { return false; }

        int cells = xMul * yMul;
        if (cells <= 1 || cells > FragmentMaxCells)
        {
            return false;
        }

        // Custom-map logic protection: don't shatter tiles the map author
        // wired into an OnDestroyedTrigger — they may be load-bearing for
        // scripted sequences (doors that open when a wall is destroyed,
        // cutscene cues, etc.).
        if (IsMapTriggerTarget(world, tile))
        {
            return false;
        }

        int baseWidth, baseHeight;
        try { tile.GetObjectBaseSize(out baseWidth, out baseHeight); }
        catch { return false; }

        if (baseWidth <= 0 || baseHeight <= 0 || tile.Body == null)
        {
            return false;
        }

        Vector2 parentBodyPos_world;
        float angle;
        try
        {
            parentBodyPos_world = Converter.ConvertBox2DToWorld(tile.Body.GetPosition());
            angle = tile.Body.GetAngle();
        }
        catch { return false; }

        string mapId = tile.MapObjectID;
        GameOwnerEnum owner = world.GameOwner;
        float cosA = (float)Math.Cos(angle);
        float sinA = (float)Math.Sin(angle);

        var fragments = new List<ObjectData>(cells);

        // Each 1x1 cell's centre, expressed as an offset from the parent
        // body centre in the tile's LOCAL frame, is
        //   local = ( (cx - (xMul-1)/2) * baseW,  (cy - (yMul-1)/2) * baseH )
        // Then rotate by the tile's angle to get the world-space offset.
        for (int cy = 0; cy < yMul; cy++)
        {
            for (int cx = 0; cx < xMul; cx++)
            {
                float lx = (cx - (xMul - 1) * 0.5f) * baseWidth;
                float ly = (cy - (yMul - 1) * 0.5f) * baseHeight;
                float wx = parentBodyPos_world.X + cosA * lx - sinA * ly;
                float wy = parentBodyPos_world.Y + sinA * lx + cosA * ly;
                Vector2 cellPos = new(wx, wy);

                ObjectData child;
                try
                {
                    child = ObjectData.CreateNew(new ObjectDataStartParams(
                        world.IDCounter.NextID(), 0, 0, mapId, owner));
                    if (child == null) continue;
                    child.SetObjectSizeMultiplier(1, 1);
                }
                catch { continue; }

                try
                {
                    world.CreateTile(new SpawnObjectInformation(child, cellPos, angle));
                }
                catch { continue; }

                // Tag so the fire sweep and future eligibility checks can
                // distinguish our fragments from user-tagged objects.
                try { child.CustomIDName = FragmentCustomId; } catch { /* best effort */ }
                // Preserve palette.
                try
                {
                    if (tile.HasColors)
                    {
                        string[] cols = tile.ColorsCopy;
                        if (cols != null)
                        {
                            child.ApplyColors(cols, false);
                        }
                    }
                }
                catch { /* best effort */ }

                fragments.Add(child);
            }
        }

        if (fragments.Count == 0)
        {
            return false;
        }

        // Remove the original parent tile now that all cells exist.
        try { tile.Destroy(); }
        catch
        {
            try { tile.Remove(); } catch { /* tolerate */ }
        }

        // Apply radius destruction to the freshly-spawned cells that fall
        // inside the blast (same-tick effect — the player sees the hit cells
        // fly, the rest remain standing).
        float radiusSq = radius * radius;
        foreach (ObjectData frag in fragments)
        {
            AABB fAabb;
            try { fAabb = frag.GetWorldAABB(); }
            catch { continue; }

            Vector2 fCenter = (fAabb.lowerBound + fAabb.upperBound) * 0.5f;
            if (Vector2.DistanceSquared(fCenter, explosionCenter) > radiusSq)
            {
                continue;
            }

            if (DislodgeTile(world, frag, explosionCenter, damage, debris))
            {
                // Puff of tiny bits alongside each blown cell — makes the
                // hole look explosively carved rather than a grid of boxes
                // silently flipping to dynamic.
                if (debris != null && debris.Length > 0)
                {
                    try
                    {
                        world.SpawnDebris(frag, fCenter, 6f, debris, (short)FragmentFlavourDebris, true);
                    }
                    catch { /* not critical */ }
                }

                Vector2 fOut = fCenter - explosionCenter;
                if (fOut.LengthSquared() > 0.0001f) fOut.Normalize();
                else fOut = new Vector2(0f, -1f);
                seeds.Add(new CollapseSeed(fAabb, fOut));
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true if the given tile is referenced by any
    /// <see cref="ObjectOnDestroyedTrigger"/> in the world — i.e. the map
    /// author wired a script trigger to this tile's destruction. Fragmenting
    /// such a tile would break the scripted sequence because the original
    /// object ID would disappear, so the fragmentation path leaves these
    /// tiles intact.
    /// </summary>
    private static bool IsMapTriggerTarget(GameWorld world, ObjectData tile)
    {
        int targetId = tile.ObjectID;
        try
        {
            var triggers = world.GetObjectDataByType<ObjectOnDestroyedTrigger>();
            if (triggers == null) return false;
            foreach (ObjectOnDestroyedTrigger odt in triggers)
            {
                if (odt == null) continue;
                var tracked = odt.m_trackingObjects;
                if (tracked == null) continue;
                for (int i = 0; i < tracked.Count; i++)
                {
                    if (tracked[i] != null && tracked[i].ObjectID == targetId)
                    {
                        return true;
                    }
                }
            }
        }
        catch { /* best effort — if we can't tell, err on the safe side and fragment */ }
        return false;
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

    private static bool HasSubstring(string s, string sub)
        => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Coarse material lookup driven by <see cref="ObjectData.MapObjectID"/>
    /// substrings. Order matters — more specific materials (glass, marble,
    /// concrete, metal) are checked before the generic wood/stone fallback so a
    /// "MARBLESTATUE" doesn't get misclassified as stone.
    /// </summary>
    private static MaterialInfo ResolveMaterial(string mapObjectId)
    {
        if (HasSubstring(mapObjectId, "GLASS") || HasSubstring(mapObjectId, "WINDOW") || HasSubstring(mapObjectId, "BOTTLE"))
        {
            return MatGlass;
        }
        if (HasSubstring(mapObjectId, "ICE") || HasSubstring(mapObjectId, "SNOW"))
        {
            return MatIce;
        }
        if (HasSubstring(mapObjectId, "MARBLE"))
        {
            return MatMarble;
        }
        if (HasSubstring(mapObjectId, "CONCRETE"))
        {
            return MatConcrete;
        }
        if (HasSubstring(mapObjectId, "METAL") || HasSubstring(mapObjectId, "STEEL") ||
            HasSubstring(mapObjectId, "IRON") || HasSubstring(mapObjectId, "PIPE") ||
            HasSubstring(mapObjectId, "TANK"))
        {
            return MatMetal;
        }
        if (HasSubstring(mapObjectId, "DIRT") || HasSubstring(mapObjectId, "SAND") ||
            HasSubstring(mapObjectId, "MUD") || HasSubstring(mapObjectId, "GRASS"))
        {
            return MatDirt;
        }
        for (int i = 0; i < WoodKeywords.Length; i++)
        {
            if (mapObjectId.IndexOf(WoodKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return MatWood;
            }
        }
        return MatStone;
    }

    /// <summary>
    /// Switches a static tile to dynamic and launches it with the given linear
    /// and angular velocity, registering it as a lethal launched chunk and
    /// emitting a small burst of flavour debris. Returns false if the tile
    /// couldn't be made dynamic (e.g. jointed, portal-linked) — in that case the
    /// caller should treat the tile as "not destroyed".
    /// </summary>
    private static bool LaunchAsDynamic(GameWorld world, ObjectData tile, Vector2 velocity, float angVel, string[] flavourDebris, int flavourCount)
    {
        Vector2 tilePos;
        try { tilePos = tile.GetWorldPosition(); }
        catch { return false; }

        try
        {
            tile.ChangeBodyType(BodyType.Dynamic);
        }
        catch
        {
            return false;
        }

        if (tile.Body == null)
        {
            return false;
        }

        try
        {
            tile.Body.SetLinearVelocity(velocity);
            tile.Body.SetAngularVelocity(angVel);
            tile.Body.SetAwake(true);
        }
        catch
        {
            // Ignore — chunk still exists as dynamic even without impulse.
        }

        // Mark as missile so the fallen chunk persists visually after it
        // comes to rest — without this flag, SFD's cleanup treats an
        // ex-static tile in the Dynamic body state as transient and will
        // quietly remove it. We want the rubble to stay on the floor.
        try
        {
            if (tile.GetScriptBridge() is IObject io)
            {
                io.TrackAsMissile(true);
            }
        }
        catch { /* tolerate — missile tracking is cosmetic */ }

        LaunchedChunks.Add(tile.ObjectID);

        if (flavourCount > 0 && flavourDebris != null && flavourDebris.Length > 0)
        {
            try
            {
                world.SpawnDebris(tile, tilePos, 6f, flavourDebris, (short)flavourCount, true);
            }
            catch { /* not critical */ }
        }

        return true;
    }

    private static bool DislodgeTile(GameWorld world, ObjectData tile, Vector2 explosionCenter, float damage, string[] flavourDebris)
    {
        Vector2 tilePos;
        try { tilePos = tile.GetWorldPosition(); }
        catch { return false; }

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

        // Falloff: strongest at centre, weaker at edges.
        float falloff = MathHelper.Clamp(1f - dist / MaxRadius, 0.25f, 1f);
        float speed = Math.Min(MaxDislodgeSpeed, damage * DislodgeImpulseScale * falloff * 0.1f + 4f);

        Vector2 launchVel = dir * speed + new Vector2(0f, -1.5f); // small upward kick
        float angVel = (float)(Globals.Random.NextDouble() * 2.0 - 1.0) * 8f;

        return LaunchAsDynamic(world, tile, launchVel, angVel, flavourDebris, FlavourDebrisPerTile);
    }

    /// <summary>
    /// Record of a destroyed tile used to seed chain-collapse propagation.
    /// Stores the tile's original world-space AABB (so we can probe above it
    /// even after the object has been removed) and the outward unit vector from
    /// the explosion centre (so high-damage blasts can keep tunnelling in that
    /// direction).
    /// </summary>
    private readonly struct CollapseSeed
    {
        public readonly AABB Aabb;
        public readonly Vector2 OutwardDir;

        public CollapseSeed(AABB aabb, Vector2 outwardDir)
        {
            Aabb = aabb;
            OutwardDir = outwardDir;
        }
    }

    /// <summary>
    /// Walks outward from every tile destroyed by the initial blast, knocking
    /// down tiles that now have no support. Two probe directions per seed:
    ///   * Upward (-Y): models gravity — tiles resting on the destroyed tile
    ///     fall down.
    ///   * Outward (only for blasts ≥ <see cref="HighPenetrationDamage"/>):
    ///     models shaped / penetrating blasts that drill sideways tunnels
    ///     through adjacent geometry rather than just collapsing ceilings.
    /// Depth and per-step fan-out are capped so a single blast can't chain-
    /// react the entire map.
    /// </summary>
    private static void PropagateCollapse(GameWorld world, List<CollapseSeed> seeds, float damage)
    {
        bool penetrate = damage >= HighPenetrationDamage;
        int maxDepth = penetrate ? ChainCollapseMaxDepthHigh : ChainCollapseMaxDepthNormal;
        var visited = new HashSet<int>();
        var queue = new Queue<(AABB Aabb, Vector2 Outward, int Depth)>();
        foreach (CollapseSeed seed in seeds)
        {
            queue.Enqueue((seed.Aabb, seed.OutwardDir, 0));
        }

        // Probe upward (negative Y in SFD) = "support collapse".
        Vector2 upward = new(0f, -1f);

        while (queue.Count > 0)
        {
            var (aabb, outward, depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            // Gravity pass: always probe the tile directly above the removed one.
            ProbeAndCollapse(world, aabb, upward, depth, isPenetration: false, damage, visited, queue);

            // Penetration pass: high-damage blasts also drill sideways/outward.
            if (penetrate && outward.LengthSquared() > 0.0001f)
            {
                ProbeAndCollapse(world, aabb, outward, depth, isPenetration: true, damage, visited, queue);
            }
        }
    }

    private static void ProbeAndCollapse(
        GameWorld world,
        AABB aabb,
        Vector2 direction,
        int depth,
        bool isPenetration,
        float damage,
        HashSet<int> visited,
        Queue<(AABB Aabb, Vector2 Outward, int Depth)> queue)
    {
        Vector2 center = (aabb.lowerBound + aabb.upperBound) * 0.5f;
        Vector2 halfSize = (aabb.upperBound - aabb.lowerBound) * 0.5f;

        // Step roughly one tile away along the probe direction, plus a 1-unit gap
        // so we don't re-pick up the seed's own (now destroyed) footprint.
        float stepX = halfSize.X * 2f + 1f;
        float stepY = halfSize.Y * 2f + 1f;
        float step = (Math.Abs(direction.X) > Math.Abs(direction.Y)) ? stepX : stepY;
        Vector2 probeCenter = center + direction * step;

        // Probe radius slightly smaller than the seed so we don't spill into
        // diagonal neighbors that weren't actually supported by the seed.
        float probeRadius = Math.Max(4f, Math.Max(halfSize.X, halfSize.Y) * 0.85f);
        AABB.Create(out AABB probeArea, probeCenter, probeCenter, probeRadius);

        List<ObjectData> neighbors;
        try
        {
            neighbors = world.GetObjectDataByArea(probeArea, false, PhysicsLayer.Active).ToList();
        }
        catch
        {
            return;
        }

        foreach (ObjectData neighbor in neighbors)
        {
            if (neighbor == null || neighbor.IsPlayer || neighbor.RemovalInitiated || !neighbor.IsStatic)
            {
                continue;
            }
            if (!visited.Add(neighbor.ObjectID))
            {
                continue;
            }

            string mapId = neighbor.MapObjectID;
            if (string.IsNullOrEmpty(mapId) || Excluded.Contains(mapId))
            {
                continue;
            }
            if (mapId.StartsWith("BG", StringComparison.OrdinalIgnoreCase) ||
                mapId.StartsWith("FAR", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (IsExcludedBySubstring(mapId) || IsStructural(neighbor))
            {
                continue;
            }

            // Per-map protection: skip tiles the map explicitly marks as
            // indestructible (e.g. Sanctuary's STONE07A edge columns).
            // Without this guard a collapse cascade could still dislodge
            // or fragment them via the propagation path.
            if (IsProtectedByMap(world, neighbor))
            {
                continue;
            }

            AABB nAabb;
            try { nAabb = neighbor.GetWorldAABB(); }
            catch { continue; }

            MaterialInfo material = ResolveMaterial(mapId);
            // Propagation uses a softer velocity than the primary blast: collapse
            // tiles are meant to fall/tumble, not launch.
            Vector2 vel;
            if (isPenetration)
            {
                // Tunnelling: keep momentum in the same outward direction.
                vel = direction * 5f + new Vector2(0f, -1f);
            }
            else
            {
                // Gravity collapse: small horizontal jitter, let gravity do the work.
                float jitterX = ((float)Globals.Random.NextDouble() - 0.5f) * 2f;
                vel = new Vector2(jitterX, 1.5f);
            }
            float angVel = ((float)Globals.Random.NextDouble() * 2f - 1f) * 4f;

            // If the dislodged neighbour is a multi-cell ObjectTileWall, split
            // it into 1x1 cells first so it falls as a shower of chunks rather
            // than one giant slab. Same fragmentation path used by direct
            // explosion hits, so the visual is consistent.
            if (neighbor is ObjectTileWall && CanFragmentIntoCells(neighbor) &&
                !IsProtectedByMap(world, neighbor) && !IsMapTriggerTarget(world, neighbor))
            {
                // Pretend the "explosion" sits at the seed (the tile that was
                // just destroyed) and only reaches one tile-width into the
                // neighbour. That way only the cells of the big tile that
                // were directly supported by / adjacent to the seed get
                // dislodged — the rest of the slab stays put, exactly like
                // a direct explosion would do.
                Vector2 seedCenter = (aabb.lowerBound + aabb.upperBound) * 0.5f;
                Vector2 seedHalf = (aabb.upperBound - aabb.lowerBound) * 0.5f;
                float seedReach = Math.Max(seedHalf.X, seedHalf.Y);
                float fragRadius = seedReach + 6f;
                var newSeeds = new List<CollapseSeed>();
                bool fragmented = false;
                try
                {
                    fragmented = TryFragmentIntoCells(
                        world, neighbor, nAabb, seedCenter, fragRadius, damage, material.Debris, newSeeds);
                }
                catch { fragmented = false; }

                if (fragmented)
                {
                    // Each newly-dislodged cell can in turn dislodge tiles
                    // above it — feed them back into the queue so the
                    // collapse keeps walking through the structure.
                    foreach (CollapseSeed s in newSeeds)
                    {
                        queue.Enqueue((s.Aabb, s.OutwardDir, depth + 1));
                    }
                    continue;
                }
            }

            if (!LaunchAsDynamic(world, neighbor, vel, angVel, material.Debris, 1))
            {
                continue;
            }

            queue.Enqueue((nAabb, direction, depth + 1));
        }
    }

    /// <summary>
    /// Applies an outward impulse to every player and dynamic object inside
    /// <paramref name="radius"/>. Strength falls off linearly with distance and
    /// scales by <paramref name="damage"/> up to <see cref="ShockwaveReferenceDamage"/>.
    /// Static tiles and freshly-launched chunks are deliberately untouched here
    /// (their kinematics are already set by the destruction pass).
    /// </summary>
    private static void ApplyShockwave(GameWorld world, Vector2 center, float radius, float damage)
    {
        if (damage < ShockwaveMinDamage || radius < 4f)
        {
            return;
        }

        float radiusSq = radius * radius;
        float strengthScale = Math.Min(1f, damage / ShockwaveReferenceDamage);

        // Players
        if (world.Players != null)
        {
            foreach (Player player in world.Players)
            {
                if (player == null || player.IsDead || player.IsRemoved)
                {
                    continue;
                }

                Vector2 delta;
                try { delta = player.Position - center; }
                catch { continue; }

                float distSq = delta.LengthSquared();
                if (distSq > radiusSq || distSq < 0.0001f)
                {
                    continue;
                }

                float dist = (float)Math.Sqrt(distSq);
                float falloff = 1f - dist / radius;
                Vector2 dir = delta / dist;
                Vector2 push = dir * (ShockwavePlayerSpeed * falloff * strengthScale);
                // Always add a small vertical kick so the push reads as "thrown"
                // rather than "slid".
                push.Y -= 2.5f * falloff * strengthScale;

                try { player.SimulateFallWithSpeed(push); } catch { /* tolerate */ }
            }
        }

        // Dynamic objects (weapons, crates, previously-launched chunks, rag bits).
        AABB.Create(out AABB area, center, center, radius);
        List<ObjectData> dynamics;
        try
        {
            dynamics = world.GetObjectDataByArea(area, false, PhysicsLayer.Active).ToList();
        }
        catch
        {
            return;
        }

        foreach (ObjectData obj in dynamics)
        {
            if (obj == null || obj.IsPlayer || obj.IsStatic || obj.RemovalInitiated || obj.Body == null)
            {
                continue;
            }

            Vector2 pos;
            try { pos = obj.GetWorldPosition(); }
            catch { continue; }

            Vector2 delta = pos - center;
            float distSq = delta.LengthSquared();
            if (distSq > radiusSq || distSq < 0.0001f)
            {
                continue;
            }

            float dist = (float)Math.Sqrt(distSq);
            float falloff = 1f - dist / radius;
            Vector2 dir = delta / dist;
            Vector2 push = dir * (ShockwaveObjectSpeed * falloff * strengthScale);
            push.Y -= 2f * falloff * strengthScale;

            try
            {
                Vector2 cur = obj.Body.GetLinearVelocity();
                obj.Body.SetLinearVelocity(cur + push);
                float curAng = obj.Body.GetAngularVelocity();
                float kick = ((float)Globals.Random.NextDouble() * 2f - 1f) * ShockwaveAngularKick * falloff;
                obj.Body.SetAngularVelocity(curAng + kick);
                obj.Body.SetAwake(true);
            }
            catch { /* tolerate exotic body states */ }
        }
    }

    /// <summary>
    /// True when <paramref name="mapId"/> contains any of the given keywords
    /// (case-insensitive).
    /// </summary>
    private static bool MapIdMatches(string mapId, string[] keywords)
    {
        if (string.IsNullOrEmpty(mapId)) return false;
        for (int i = 0; i < keywords.Length; i++)
        {
            if (mapId.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// If <paramref name="obj"/> is a gargoyle that hasn't already been
    /// detonated this round, schedules a secondary explosion at its centre
    /// (chain reaction). Returns true when a chain blast was scheduled so the
    /// caller can skip its normal static-tile handling and let the secondary
    /// blast finish the gargoyle.
    /// </summary>
    private static bool TryChainDetonateGargoyle(GameWorld world, ObjectData obj)
    {
        string mapId = obj?.MapObjectID;
        if (!MapIdMatches(mapId, GargoyleKeywords))
        {
            return false;
        }

        int id = obj.ObjectID;
        if (!_detonatedGargoyles.Add(id))
        {
            return false; // already chained — don't recurse
        }

        Vector2 centre;
        try { centre = obj.GetWorldPosition(); }
        catch { return false; }

        // Trigger the chain blast. This re-enters HandleExplosion via the
        // Harmony postfix on TriggerExplosion; the _detonatedGargoyles guard
        // breaks the recursion when it loops back to the same gargoyle.
        try { world.TriggerExplosion(centre, GargoyleChainDamage); }
        catch { /* tolerate */ }

        // Spawn a chunky burst of stone debris and physically remove the
        // gargoyle tile — gargoyles are ObjectDefault statics that SFD's
        // explosion code won't destroy on its own.
        try { world.SpawnDebris(obj, centre, 14f, MatStone.Debris, 14, true); }
        catch { /* tolerate */ }

        try { obj.Destroy(); }
        catch
        {
            try { obj.Remove(); } catch { /* tolerate */ }
        }

        return true;
    }

    /// <summary>
    /// World-units sweep radius around each destroyed brick centre when
    /// hunting for adjacent gargoyles to chain-detonate on Sanctuary.
    /// Calibrated to one tile + a small slack so a brick directly next to a
    /// gargoyle reaches it without snagging gargoyles across the room.
    /// </summary>
    private const float SanctuaryBrickGargoyleSweep = 32f;

    /// <summary>
    /// For each destroyed brick centre, scan a small AABB and chain-detonate
    /// any gargoyle inside it. Used on Sanctuary so blowing up the brick
    /// columns next to the gargoyle statues sets the gargoyles off too.
    /// </summary>
    private static void ChainGargoylesNearBricks(GameWorld world, List<Vector2> brickCentres)
    {
        for (int i = 0; i < brickCentres.Count; i++)
        {
            Vector2 c = brickCentres[i];
            AABB.Create(out AABB area, c, c, SanctuaryBrickGargoyleSweep);

            List<ObjectData> nearby;
            try { nearby = world.GetObjectDataByArea(area, false, PhysicsLayer.Active).ToList(); }
            catch { continue; }

            foreach (ObjectData obj in nearby)
            {
                if (obj == null || obj.RemovalInitiated) continue;
                if (!MapIdMatches(obj.MapObjectID, GargoyleKeywords)) continue;
                TryChainDetonateGargoyle(world, obj);
            }
        }
    }

    /// <summary>
    /// World-units sweep radius around an explosion centre when hunting for
    /// gargoyles to chain-detonate. Generous enough that a blast in the
    /// general vicinity of a gargoyle still sets it off.
    /// </summary>
    private const float GargoyleProximitySweep = 48f;

    /// <summary>
    /// Sweeps a generous AABB around <paramref name="centre"/> and
    /// chain-detonates any gargoyle found inside it. Called after every
    /// explosion so gargoyles react to nearby blasts even when not in the
    /// destruction-radius AABB.
    /// </summary>
    private static void ChainGargoylesNearPoint(GameWorld world, Vector2 centre)
    {
        AABB.Create(out AABB area, centre, centre, GargoyleProximitySweep);

        List<ObjectData> nearby;
        try { nearby = world.GetObjectDataByArea(area, false, PhysicsLayer.Active).ToList(); }
        catch { return; }

        foreach (ObjectData obj in nearby)
        {
            if (obj == null || obj.RemovalInitiated) continue;
            if (!MapIdMatches(obj.MapObjectID, GargoyleKeywords)) continue;
            TryChainDetonateGargoyle(world, obj);
        }
    }

    /// <summary>
    /// If <paramref name="obj"/> is a helicopter (and we haven't already shredded
    /// this one), spawns a generous shower of metal debris and a few rigid
    /// burning scrap chunks, plus a flavour secondary blast. Returns true when
    /// the helicopter was shredded so the caller can flag the round as having
    /// destroyed something.
    /// </summary>
    private static bool TryShredHelicopter(GameWorld world, ObjectData obj, Vector2 explosionCenter)
    {
        string mapId = obj?.MapObjectID;
        if (!MapIdMatches(mapId, HelicopterKeywords))
        {
            return false;
        }

        int id = obj.ObjectID;
        if (!_shreddedHelicopters.Add(id))
        {
            return false;
        }

        Vector2 centre;
        try { centre = obj.GetWorldPosition(); }
        catch { return false; }

        // Spray of small metal debris particles via SFD's normal SpawnDebris path.
        try
        {
            world.SpawnDebris(obj, centre, 28f, MetalDebris, (short)HelicopterMetalChunks, true);
        }
        catch { /* tolerate */ }

        // Set the helicopter itself fully ablaze so its wreck burns visibly.
        try { obj.SetMaxFire(); } catch { /* tolerate */ }

        // Plus a handful of rigid burning scrap tiles flying outward.
        for (int i = 0; i < HelicopterScrapTiles; i++)
        {
            try
            {
                ObjectData scrap = ObjectData.CreateNew(new ObjectDataStartParams(
                    world.IDCounter.NextID(), 0, 0, HelicopterScrapMapId, world.GameOwner));
                if (scrap == null) continue;

                // Spawn slightly offset around the helicopter centre.
                double a = Globals.Random.NextDouble() * Math.PI * 2.0;
                float r = (float)Globals.Random.NextDouble() * 6f + 2f;
                Vector2 spawnPos = centre + new Vector2((float)Math.Cos(a) * r, (float)Math.Sin(a) * r);

                world.CreateTile(new SpawnObjectInformation(scrap, spawnPos, 0f));

                try { scrap.ChangeBodyType(BodyType.Dynamic); } catch { /* tolerate */ }
                if (scrap.Body != null)
                {
                    Vector2 dir = spawnPos - explosionCenter;
                    if (dir.LengthSquared() < 0.0001f)
                    {
                        dir = new Vector2((float)(Globals.Random.NextDouble() * 2.0 - 1.0), -1f);
                    }
                    dir.Normalize();
                    float speed = 14f + (float)Globals.Random.NextDouble() * 10f;
                    Vector2 vel = dir * speed + new Vector2(0f, -8f);
                    float angVel = ((float)Globals.Random.NextDouble() * 2f - 1f) * 18f;

                    try
                    {
                        scrap.Body.SetLinearVelocity(vel);
                        scrap.Body.SetAngularVelocity(angVel);
                        scrap.Body.SetAwake(true);
                    }
                    catch { /* tolerate */ }
                }

                try { scrap.SetMaxFire(); } catch { /* tolerate */ }

                try
                {
                    if (scrap.GetScriptBridge() is IObject io)
                    {
                        io.TrackAsMissile(true);
                    }
                }
                catch { /* tolerate */ }

                LaunchedChunks.Add(scrap.ObjectID);
            }
            catch { /* one scrap failing isn't fatal */ }
        }

        return true;
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

/// <summary>
/// Implements the "blast scar" background mask. Each explosion registers a
/// jagged-silhouette shape at its centre; for as long as that shape is active,
/// any <see cref="ObjectData.Draw"/> call for a BG / FAR tile whose world
/// position falls inside the silhouette is suppressed (prefix returns
/// <c>false</c>). The result is a real mask: background tiles inside the
/// irregular shape simply don't render, so whatever sits behind them — the
/// deepest backdrop or the black void — shows through. Nothing is drawn on
/// top of the scene, so the foreground is never visually covered.
///
/// The silhouette is defined by an array of per-angle radii perturbed by
/// layered sine lobes plus random noise (not a grid of tile-sized boxes), so
/// the culled region has a naturally ragged boundary.
/// </summary>
[HarmonyPatch]
internal static class BlastScarRenderer
{
    /// <summary>Number of angular radius samples defining the jagged silhouette.</summary>
    private const int EdgeSamples = 64;

    /// <summary>
    /// Max width/height (world units) of a BG/FAR tile that can be masked by a
    /// scar. Tiles larger than this are full-map backdrops — masking them by
    /// their anchor point would erase the entire background of the level (e.g.
    /// the bug seen on Hazardous, where one giant BG tile vanished after any
    /// nearby explosion). Comfortable upper bound for per-room BG elements.
    /// </summary>
    private const float MaxMaskableBgSize = 192f;

    /// <summary>
    /// Amplitude of the per-vertex radial jitter, as a fraction of the base
    /// radius. Higher = more ragged / irregular.
    /// </summary>
    private const float JitterAmplitude = 0.28f;

    private sealed class Scar
    {
        public Vector2 Center;
        public float Radius;
        public float[] EdgeRadii; // length == EdgeSamples
    }

    private static readonly List<Scar> _scars = new();
    private static readonly object _scarLock = new();

    /// <summary>
    /// Registers a new blast scar centred at <paramref name="worldPosition"/>
    /// with the given world-space radius. Each scar gets its own independently
    /// jagged silhouette so repeated blasts at the same spot don't look
    /// identical. Thread-safe.
    /// </summary>
    public static void AddScar(Vector2 worldPosition, float radius)
    {
        if (radius < 4f)
        {
            return;
        }

        float[] radii = new float[EdgeSamples];
        double lobePhase1 = Globals.Random.NextDouble() * Math.PI * 2.0;
        double lobePhase2 = Globals.Random.NextDouble() * Math.PI * 2.0;
        double lobePhase3 = Globals.Random.NextDouble() * Math.PI * 2.0;
        for (int i = 0; i < EdgeSamples; i++)
        {
            double t = (i / (double)EdgeSamples) * Math.PI * 2.0;
            double lobe = 0.55 * Math.Sin(t * 3.0 + lobePhase1)
                        + 0.30 * Math.Sin(t * 5.0 + lobePhase2)
                        + 0.15 * Math.Sin(t * 9.0 + lobePhase3);
            double noise = (Globals.Random.NextDouble() - 0.5) * 0.9;
            double jitter = (lobe + noise) * JitterAmplitude;
            radii[i] = (float)(radius * (1.0 + jitter));
        }

        var scar = new Scar
        {
            Center = worldPosition,
            Radius = radius,
            EdgeRadii = radii,
        };

        lock (_scarLock)
        {
            _scars.Add(scar);
        }
    }

    /// <summary>
    /// Prefix on <see cref="ObjectData.Draw"/>: if the object is a background
    /// tile (MapObjectID prefixed with <c>BG</c> or <c>FAR</c>) AND its world
    /// position lies inside any active scar's jagged silhouette, suppress the
    /// draw call. This removes the tile from the frame without destroying it,
    /// producing an irregular see-through hole in the background stack.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ObjectData), nameof(ObjectData.Draw), new[] { typeof(SpriteBatch), typeof(float) })]
    private static bool BeforeObjectDraw(ObjectData __instance)
    {
        return !ShouldMaskObject(__instance);
    }

    /// <summary>
    /// Same as <see cref="BeforeObjectDraw"/> but covers the colour-tinted
    /// overload of <see cref="ObjectData.Draw"/>. Both overloads must be
    /// suppressed or masked tiles still render via the unhooked path.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ObjectData), nameof(ObjectData.Draw), new[] { typeof(SpriteBatch), typeof(float), typeof(Color) })]
    private static bool BeforeObjectDrawTinted(ObjectData __instance)
    {
        return !ShouldMaskObject(__instance);
    }

    /// <summary>
    /// Belt-and-braces hook on <see cref="ObjectData.DrawBase"/>: some tiles
    /// call DrawBase directly without going through Draw. Suppress it too so
    /// masked BG tiles are truly invisible.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ObjectData), nameof(ObjectData.DrawBase))]
    private static bool BeforeDrawBase(ObjectData __instance)
    {
        return !ShouldMaskObject(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), "DisposeAllObjects")]
    private static void OnDisposeAll()
    {
        lock (_scarLock)
        {
            _scars.Clear();
        }
    }

    private static bool ShouldMaskObject(ObjectData obj)
    {
        if (obj == null)
        {
            return false;
        }

        string mapId = obj.MapObjectID;
        if (string.IsNullOrEmpty(mapId))
        {
            return false;
        }

        bool isBackground = mapId.StartsWith("BG", StringComparison.OrdinalIgnoreCase)
                         || mapId.StartsWith("FAR", StringComparison.OrdinalIgnoreCase);
        if (!isBackground)
        {
            return false;
        }

        // Cheap scar count peek without locking on the hot path: if there are
        // no scars, exit fast. Reads of _scars.Count are racy but only cause a
        // spurious lock entry, never incorrect culling.
        if (_scars.Count == 0)
        {
            return false;
        }

        // Don't mask huge full-map backdrops: a single giant BG tile (e.g. the
        // sky/wall on Hazardous) has a footprint that dwarfs any blast scar,
        // and its anchor point can easily land inside a scar — which would
        // suppress the WHOLE backdrop and make the entire background vanish.
        // Only small per-room BG elements should be eligible for masking.
        AABB bgAabb;
        try { bgAabb = obj.GetWorldAABB(); }
        catch { return false; }

        float w = bgAabb.upperBound.X - bgAabb.lowerBound.X;
        float h = bgAabb.upperBound.Y - bgAabb.lowerBound.Y;
        if (w > MaxMaskableBgSize || h > MaxMaskableBgSize)
        {
            return false;
        }

        Vector2 pos;
        try { pos = obj.GetWorldPosition(); }
        catch { return false; }

        lock (_scarLock)
        {
            for (int i = 0; i < _scars.Count; i++)
            {
                if (IsInsideScar(_scars[i], pos))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsInsideScar(Scar scar, Vector2 worldPos)
    {
        float dx = worldPos.X - scar.Center.X;
        float dy = worldPos.Y - scar.Center.Y;
        float distSq = dx * dx + dy * dy;

        // Quick reject on outer bounding circle (max possible jittered radius).
        float maxR = scar.Radius * (1f + JitterAmplitude);
        if (distSq > maxR * maxR)
        {
            return false;
        }

        // Quick accept on inner bounding circle (min possible jittered radius).
        float minR = scar.Radius * (1f - JitterAmplitude);
        if (distSq < minR * minR)
        {
            return true;
        }

        // Between the two — compare against the angular edge sample.
        float angle = (float)Math.Atan2(dy, dx);
        if (angle < 0f)
        {
            angle += (float)(Math.PI * 2.0);
        }

        float sampleF = (angle / (float)(Math.PI * 2.0)) * EdgeSamples;
        int i0 = (int)Math.Floor(sampleF) % EdgeSamples;
        int i1 = (i0 + 1) % EdgeSamples;
        float f = sampleF - (float)Math.Floor(sampleF);
        float boundary = MathHelper.Lerp(scar.EdgeRadii[i0], scar.EdgeRadii[i1], f);

        return distSq <= boundary * boundary;
    }
}
