using System;
using dnlib.DotNet;

class Publicizer
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: Publicizer.exe <input.dll> [output.dll]");
            return;
        }

        string input = args[0];
        string output = args.Length > 1 ? args[1] : input;

        var module = ModuleDefMD.Load(input);
        int publicized = 0;

        foreach (var type in module.GetTypes())
        {
            if (!type.IsPublic && !type.IsNestedPublic)
            {
                if (type.IsNested)
                    type.Attributes = (type.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPublic;
                else
                    type.Attributes = (type.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.Public;
                publicized++;
            }

            foreach (var method in type.Methods)
            {
                if (!method.IsPublic)
                {
                    method.Attributes = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                    publicized++;
                }
            }

            foreach (var field in type.Fields)
            {
                if (!field.IsPublic)
                {
                    field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public;
                    publicized++;
                }
            }
        }

        module.Write(output + ".tmp");
        module.Dispose();

        if (System.IO.File.Exists(output))
            System.IO.File.Delete(output);
        System.IO.File.Move(output + ".tmp", output);

        Console.WriteLine("Publicized " + publicized + " members in " + System.IO.Path.GetFileName(output));
    }
}
