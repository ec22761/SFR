using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFR.Helper;
using SFR.Misc;
using Player = SFD.Player;

namespace SFR.Fighter;

/// <summary>
/// Here we handle all the HUD or visual effects regarding players, such as dev icons.
/// </summary>
[HarmonyPatch]
internal static class GadgetHandler
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerHUD), "DrawTeamIcon")]
    private static bool DrawHudTeamIcon(Player player, GameUser user, int x, int y, SpriteBatch spriteBatch, float elapsed)
    {
        Texture2D teamIcon = Constants.GetTeamIcon(user.GameSlotTeam);
        if (teamIcon != null)
        {
            if (player is not null && !player.IsRemoved && !player.IsDead && !player.IsBot && user is not null)
            {
                if (DevHandler.GetDeveloperIcon(player, user) is { } devIcon)
                {
                    teamIcon = devIcon;
                }
            }

            spriteBatch.Draw(teamIcon, new Rectangle(x - 8, y - 6, teamIcon.Width * 2, teamIcon.Height * 2), Color.White);
        }

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.DrawPlates))]
    private static bool DrawExtraInfo(float ms, Player __instance)
    {
        ExtendedPlayer ext = __instance.GetExtension();
        if (ext.Spectral)
        {
            return false;
        }

        Vector2 vector = Camera.ConvertWorldToScreen(__instance.Position + new Vector2(0f, 24f));
        float num = MathHelper.Max(Camera.Zoom * 0.4f, 1f);

        NameIconHandler.Draw(__instance, vector, num);

        // Handle message icons.
        if (__instance is { IsDead: false, IsRemoved: false, ChatActive: true })
        {
            if (__instance.m_chatIconTimer > 250f)
            {
                __instance.m_chatIconFrame = (__instance.m_chatIconFrame + 1) % 4;
                __instance.m_chatIconTimer -= 250f;
            }
            else
            {
                __instance.m_chatIconTimer += ms;
            }

            __instance.m_spriteBatch.Draw(Constants.ChatIcon,
                new Vector2(vector.X + __instance.m_nameTextSize.X * 0.25f * num, vector.Y - __instance.m_nameTextSize.Y * num),
                new Rectangle(1 + __instance.m_chatIconFrame * 13, 1, 12, 12), ColorCorrection.FromXNAToCustom(Constants.COLORS.CHAT_ICON), 0f, Vector2.Zero,
                num, SpriteEffects.None, 1f);
        }

        StatusBarHandler.Draw(__instance, vector, num);

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), "DrawColor", MethodType.Getter)]
    private static bool GetPlayerDrawColor(Player __instance, ref Color __result)
    {
        ExtendedPlayer extendedPlayer = __instance.GetExtension();

        if (extendedPlayer.AdrenalineBoost)
        {
            __result = ColorCorrection.CreateCustom(Globals.RageBoost);
            return false;
        }

        if (extendedPlayer.LeapBoost)
        {
            __result = ColorCorrection.CreateCustom(Globals.LeapBoost);
            return false;
        }

        if (extendedPlayer.Spectral)
        {
            __result = Color.White * 0.1f;
            return false;
        }

        return true;
    }
}