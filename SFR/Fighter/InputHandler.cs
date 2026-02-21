using HarmonyLib;
using SFD;
using SFD.GameKeyboard;
using Player = SFD.Player;

namespace SFR.Fighter;

/// <summary>
///     Fixes a base-game bug where controller analog stick inputs can get stuck
///     sending the player the wrong direction. When the stick moves quickly between
///     directions, the release event for the old direction can be missed, leaving
///     the virtual keyboard in an inconsistent state. This patch ensures opposing
///     stick directions are mutually exclusive.
/// </summary>
[HarmonyPatch]
internal static class InputHandler
{
    /// <summary>
    ///     When a left-stick direction is pressed, force-release the opposing
    ///     direction's virtual key to prevent stuck or inverted movement.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), "HandleGamePadCodeEvent")]
    private static void FixStickDirection(GamePadCode gamePadCode, VIRTUAL_KEY_EVENT virtualKeyEvent, Player __instance)
    {
        if (virtualKeyEvent != VIRTUAL_KEY_EVENT.Pressed)
        {
            return;
        }

        // Virtual key indices:
        // 0 = K_AIM_CLIMB_UP
        // 1 = K_AIM_CLIMB_DOWN
        // 2 = K_AIM_RUN_LEFT
        // 3 = K_AIM_RUN_RIGHT
        switch (gamePadCode._xboxButtonCode)
        {
            case XBoxGamePadButtonCode.LeftStickLeft:
                __instance.VirtualKeyboard.KeysPressed[3] = false;
                __instance.VirtualKeyboard.KeysPressedTimes[3] = 0f;
                break;
            case XBoxGamePadButtonCode.LeftStickRight:
                __instance.VirtualKeyboard.KeysPressed[2] = false;
                __instance.VirtualKeyboard.KeysPressedTimes[2] = 0f;
                break;
            case XBoxGamePadButtonCode.LeftStickUp:
                __instance.VirtualKeyboard.KeysPressed[1] = false;
                __instance.VirtualKeyboard.KeysPressedTimes[1] = 0f;
                break;
            case XBoxGamePadButtonCode.LeftStickDown:
                __instance.VirtualKeyboard.KeysPressed[0] = false;
                __instance.VirtualKeyboard.KeysPressedTimes[0] = 0f;
                break;
        }
    }
}
