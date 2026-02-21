using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Tiles;
using Player = SFD.Player;

namespace SFR.Fighter;

/// <summary>
/// Official team members will have a special icon in-game.
/// </summary>
internal static class DevHandler
{
    private static readonly Dictionary<string, string> _developerIcons = new()
    {
        { "913199347", "Odex" }, // Odex
        { "962495701", "Dxse" }, // Dxse
        { "319194249", "Motto73" }, // Motto
        { "887205100", "Danila015" }, // Danila01
        { "390932643", "Eiga" }, // Eiga
        { "156328261", "Samwow" }, // Samwow
        { "340339546", "Vixfor" }, // Shock
        { "310827315", "Mimyuu" }, // Mimyuu
        { "294075097", string.Empty }, // Argon
        { "457000463", "KLI" } // KLI
    };

    private static string GetAccountId(Player player, GameUser user)
    {
        if (user == null || player?.GameWorld?.GameInfo?.AccountNameInfo == null) return null;
        if (player.GameWorld.GameInfo.AccountNameInfo.TryGetLegacyAccountID(user.UserIdentifier, out string legacyId))
            return legacyId;
        return null;
    }

    internal static bool IsDeveloper(string accountId) => accountId != null && accountId.Length > 1 && _developerIcons.ContainsKey(accountId.Substring(1));

    internal static bool IsDeveloper(Player player, GameUser user) => IsDeveloper(GetAccountId(player, user));

    internal static Texture2D GetDeveloperIcon(string accountId)
    {
        if (!IsDeveloper(accountId))
        {
            return null;
        }

        // User accounts start with S. Remove it before checking it's a dev.
        string iconName = _developerIcons[accountId.Substring(1)];
        return Textures.GetTexture(iconName == string.Empty ? "developer" : iconName);
    }

    internal static Texture2D GetDeveloperIcon(Player player, GameUser user) => GetDeveloperIcon(GetAccountId(player, user));
}