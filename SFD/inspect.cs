using System;
using System.Reflection;
using System.Linq;

public class Program {
    public static void Main() {
        string path = @"C:\Users\roman\source\repos\ec22761\SFR\SFD\Superfighters Deluxe.pub.dll";
        Assembly asm = Assembly.LoadFrom(path);
        Type[] types;
        try {
            types = asm.GetTypes();
        } catch (ReflectionTypeLoadException e) {
            types = e.Types.Where(t => t != null).ToArray();
        }

        Console.WriteLine("--- Matching Types ---");
        var patterns = new[] { "Chain", "Oil", "Canister", "Barrel", "Fire", "Burn" };
        var interestingTypes = types.Where(t => patterns.Any(p => t.FullName.Contains(p))).ToList();
        foreach (var t in interestingTypes) Console.WriteLine(t.FullName);

        var targetNames = new[] { "SFD.Objects.ObjectBarrelExplosive", "SFD.Objects.ObjectExplosive", "SFD.Objects.ObjectData", "SFD.Player" };
        var targets = types.Where(t => targetNames.Contains(t.FullName) || t.FullName.Contains("Chain") || t.FullName.Contains("Oil")).ToList();

        foreach (var t in targets) {
            Console.WriteLine($"\n--- Members for {t.FullName} ---");
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) {
                Console.WriteLine($"{m.MemberType} {m}");
            }
        }

        Console.WriteLine("\n--- Player Specific Search ---");
        var player = types.FirstOrDefault(t => t.FullName == "SFD.Player");
        if (player != null) {
            var playerPatterns = new[] { "Fire", "Burn", "SetOnFire", "AddImpulse", "ApplyImpulse", "LinearVelocity", "AddVelocity", "Push", "Force", "KnockBack" };
            foreach (var m in player.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) {
                if (playerPatterns.Any(p => m.Name.Contains(p))) {
                    Console.WriteLine($"{m.MemberType} {m}");
                }
            }
        }
    }
}
