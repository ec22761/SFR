using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using SFD;
using SFD.Effects;
using SFD.Sounds;
using SFR.Helper;
using Color = Microsoft.Xna.Framework.Color;
using MathHelper = Microsoft.Xna.Framework.MathHelper;
using Player = SFD.Player;
using PlayerInputMode = SFDGameScriptInterface.PlayerInputMode;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace SFR.Fighter;

/// <summary>
///     Handles teammate revive mechanic: crouching over a dead teammate
///     for 5 seconds revives them. Only works for players on the same team.
///     The reviver's input is locked during the channel, and taking damage
///     interrupts the revive. A pixel-art circle indicator shows progress.
/// </summary>
internal static class ReviveHandler
{
    private const float ReviveTime = 5000f; // 5 seconds in ms
    private const float ReviveRange = 14f;
    private const float SoundInterval = 500f; // ms between looping sound ticks
    private const int CircleSegments = 12;
    private const float CircleRadius = 8f;

    private static readonly Dictionary<int, ReviveState> _activeRevives = new();

    private sealed class ReviveState
    {
        internal Player Target;
        internal float Progress;
        internal float LastHealth;
        internal float SoundTimer;
    }

    /// <summary>
    ///     Called each frame from the Player.Update postfix.
    ///     Tracks revive progress for alive, crouching players near dead teammates.
    /// </summary>
    internal static void Update(Player player, float ms)
    {
        if (player.IsDead || player.IsRemoved)
        {
            CancelRevive(player);
            return;
        }

        int id = player.ObjectID;

        // Must be crouching to revive
        if (!player.Crouching)
        {
            CancelRevive(player);
            return;
        }

        // Find closest dead teammate within range
        Player target = FindClosestDeadTeammate(player);
        if (target == null)
        {
            CancelRevive(player);
            return;
        }

        if (!_activeRevives.TryGetValue(id, out ReviveState state))
        {
            state = new ReviveState
            {
                Target = target,
                Progress = 0f,
                LastHealth = player.Health.CurrentValue,
                SoundTimer = 0f
            };
            _activeRevives[id] = state;
        }

        // Reset progress if target changed
        if (state.Target?.ObjectID != target.ObjectID)
        {
            state.Target = target;
            state.Progress = 0f;
            state.LastHealth = player.Health.CurrentValue;
            state.SoundTimer = 0f;
        }

        // Interrupt if the reviver took damage
        if (player.Health.CurrentValue < state.LastHealth)
        {
            CancelRevive(player);
            return;
        }

        state.LastHealth = player.Health.CurrentValue;

        state.Progress += ms;

        // Looping sound tick
        state.SoundTimer += ms;
        if (state.SoundTimer >= SoundInterval)
        {
            state.SoundTimer -= SoundInterval;
            SoundHandler.PlaySound("GetSlomo", player.Position, player.GameWorld);
        }

        if (state.Progress >= ReviveTime)
        {
            // Only the server performs the actual revive; clients see the state sync
            if (player.GameOwner != GameOwnerEnum.Client)
            {
                RevivePlayer(target);
                SoundHandler.PlaySound("Syringe", target.Position, player.GameWorld);
                EffectHandler.PlayEffect("S_P", target.Position, player.GameWorld);
                EffectHandler.PlayEffect("S_P", target.Position + new Vector2(0, 8f), player.GameWorld);
                EffectHandler.PlayEffect("CAM_S", Vector2.Zero, player.GameWorld, 0.5f, 150f, false);
            }

            _activeRevives.Remove(player.ObjectID);
        }
    }

    private static void CancelRevive(Player player)
    {
        _activeRevives.Remove(player.ObjectID);
    }

    private static Player FindClosestDeadTeammate(Player player)
    {
        var team = player.m_currentTeam;
        if (team == Team.Independent)
        {
            return null;
        }

        Player closest = null;
        float closestDist = ReviveRange * ReviveRange;

        foreach (Player other in player.GameWorld.Players)
        {
            if (other == player || !other.IsDead || other.IsRemoved || other.m_isDisposed)
            {
                continue;
            }

            if (other.m_currentTeam != team)
            {
                continue;
            }

            float dist = Vector2.DistanceSquared(player.Position, other.Position);
            if (dist < closestDist)
            {
                closestDist = dist;
                closest = other;
            }
        }

        return closest;
    }

    /// <summary>
    ///     Revive logic copied from Defib. Resets death state and sets health to half.
    /// </summary>
    private static void RevivePlayer(Player target)
    {
        target.IsDead = false;
        target.DeadTime = 0f;
        target.DeathKneeling = false;
        target.CancelDeathKneel();

        target.m_states[11] = false; // Dead
        target.m_states[15] = false; // Removed

        target.SetNewHealth(50f);

        target.CurrentAction = PlayerAction.Idle;
        target.SetInputEnabled(true);
        target.EnableRectFixture();
    }

    /// <summary>
    ///     Draws a pixel-art circular progress indicator above a dead player being revived.
    /// </summary>
    internal static void DrawReviveIndicator(Player deadPlayer, Vector2 screenPos, float scale)
    {
        if (!deadPlayer.IsDead || deadPlayer.IsRemoved)
        {
            return;
        }

        float progress = GetReviveProgress(deadPlayer);
        if (progress < 0f)
        {
            return;
        }

        float radius = CircleRadius * scale;
        int filledSegments = (int)(progress * CircleSegments);
        float partialFill = progress * CircleSegments - filledSegments;
        int dotSize = Math.Max(1, (int)(2f * scale));

        for (int i = 0; i < CircleSegments; i++)
        {
            // Start from top, go clockwise
            float angle = i * MathHelper.TwoPi / CircleSegments - MathHelper.PiOver2;
            int x = (int)(screenPos.X + radius * (float)Math.Cos(angle));
            int y = (int)(screenPos.Y + radius * (float)Math.Sin(angle));

            Rectangle dot = new(x - dotSize / 2, y - dotSize / 2, dotSize, dotSize);

            Color color;
            if (i < filledSegments)
            {
                color = new Color(0, 255, 0);
            }
            else if (i == filledSegments)
            {
                color = Color.Lerp(new Color(64, 64, 64), new Color(0, 255, 0), partialFill);
            }
            else
            {
                color = new Color(64, 64, 64);
            }

            deadPlayer.m_spriteBatch.Draw(Constants.WhitePixel, dot, color);
        }
    }

    private static float GetReviveProgress(Player deadPlayer)
    {
        foreach (var kvp in _activeRevives)
        {
            if (kvp.Value.Target?.ObjectID == deadPlayer.ObjectID)
            {
                return Math.Min(kvp.Value.Progress / ReviveTime, 1f);
            }
        }

        return -1f;
    }

    /// <summary>
    ///     Clear all revive state. Called on round end.
    /// </summary>
    internal static void Reset()
    {
        _activeRevives.Clear();
    }
}
