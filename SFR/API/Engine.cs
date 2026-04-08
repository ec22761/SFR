using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using ScriptEngine;
using ScriptEngine.Ros;

namespace SFR.API;

/// <summary>
/// The game's script engine was rewritten for .NET 8 using Roslyn and AssemblyLoadContext.
/// The original AppDomain-based sandbox and CSharpCodeProvider patches are no longer needed.
/// This class now patches the new script engine to ensure SFR compatibility.
/// </summary>
[HarmonyPatch]
internal static class Engine
{
    /// <summary>
    /// Patch ScriptAssemblyLoadContext to also resolve SFR's assembly when scripts
    /// reference types from the modded game.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ScriptAssemblyLoadContext), "Load")]
    private static void LoadAssemblyFallback(AssemblyName assemblyName, ref Assembly __result)
    {
        if (__result is null)
        {
            // Try loading from the current domain if the script engine can't resolve it
            try
            {
                __result = Assembly.Load(assemblyName);
            }
            catch
            {
                // Ignore - assembly genuinely not found
            }
        }
    }
}