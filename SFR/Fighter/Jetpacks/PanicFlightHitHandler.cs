using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using SFD;
using SFD.Projectiles;
using SFR.Helper;
using SFR.Misc;

namespace SFR.Fighter.Jetpacks;

[HarmonyPatch]
internal static class PanicFlightHitHandler
{
    private const float ProjectilePanicFlightChance = 0.25f;
    private const float ThrownTriggerCooldownMs = 100f;
    private static readonly Dictionary<long, float> _lastThrownTriggers = [];

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.TakeProjectileDamage))]
    private static void OnTakeProjectileDamage(Player __instance, Projectile projectile)
    {
        if (!TryGetReadyJetpack(__instance, out ExtendedPlayer extendedPlayer, out GenericJetpack jetpack))
        {
            return;
        }

        if (Globals.Random.NextFloat() >= ProjectilePanicFlightChance)
        {
            return;
        }

        int launchDirection = GetHorizontalDirection(projectile?.Direction ?? Vector2.Zero, __instance.LastDirectionX);
        jetpack.StartPanicFlight(extendedPlayer, launchDirection);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.HitByMissile))]
    private static void OnHitByMissile(Player __instance, ObjectData od)
    {
        TryStartFromThrownObject(__instance, od);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ObjectData), nameof(ObjectData.ImpactHit))]
    private static void OnImpactHit(ObjectData __instance, ObjectData otherObject)
    {
        if (otherObject?.IsPlayer == true && otherObject.InternalData is Player player)
        {
            TryStartFromThrownObject(player, __instance);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameWorld), "DisposeAllObjects")]
    private static void ClearThrownTriggerCache() => _lastThrownTriggers.Clear();

    private static void TryStartFromThrownObject(Player player, ObjectData thrownObject)
    {
        if (!IsThrownWeaponHit(thrownObject) || !TryGetReadyJetpack(player, out ExtendedPlayer extendedPlayer, out GenericJetpack jetpack))
        {
            return;
        }

        long triggerKey = ((long)player.ObjectID << 32) ^ (uint)thrownObject.ObjectID;
        float now = player.GameWorld?.ElapsedTotalGameTime ?? 0f;
        if (_lastThrownTriggers.TryGetValue(triggerKey, out float lastTrigger) && now - lastTrigger < ThrownTriggerCooldownMs)
        {
            return;
        }

        _lastThrownTriggers[triggerKey] = now;
        if (_lastThrownTriggers.Count > 512)
        {
            _lastThrownTriggers.Clear();
        }

        Vector2 velocity = thrownObject.Body?.GetLinearVelocity() ?? Vector2.Zero;
        int fallbackDirection = thrownObject.MissileData?.PlayerSource?.LastDirectionX ?? player.LastDirectionX;
        int launchDirection = GetHorizontalDirection(velocity, fallbackDirection);
        jetpack.StartPanicFlight(extendedPlayer, launchDirection);
    }

    private static bool TryGetReadyJetpack(Player player, out ExtendedPlayer extendedPlayer, out GenericJetpack jetpack)
    {
        extendedPlayer = null;
        jetpack = null;

        if (player == null || player.GameOwner == GameOwnerEnum.Client)
        {
            return false;
        }

        if (!ExtendedPlayer.ExtendedPlayersTable.TryGetValue(player, out extendedPlayer) || extendedPlayer.GenericJetpack is not { } activeJetpack)
        {
            return false;
        }

        if (activeJetpack.PanicFlightActive || activeJetpack.State != JetpackState.Flying)
        {
            return false;
        }

        jetpack = activeJetpack;
        return true;
    }

    private static bool IsThrownWeaponHit(ObjectData thrownObject)
    {
        return thrownObject?.MissileData?.Status == ObjectMissileStatus.Thrown;
    }

    private static int GetHorizontalDirection(Vector2 direction, int fallbackDirection)
    {
        if (Math.Abs(direction.X) > 0.05f)
        {
            return direction.X > 0f ? 1 : -1;
        }

        if (fallbackDirection != 0)
        {
            return fallbackDirection > 0 ? 1 : -1;
        }

        return 1;
    }
}