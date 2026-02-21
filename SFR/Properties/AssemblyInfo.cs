using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Superfighters Redux")]
[assembly: AssemblyDescription("Modification for Superfighters Deluxe")]
[assembly: Guid("907e970d-7e11-4461-ac55-999e0e6cf42a")]
[assembly: IgnoresAccessChecksTo("Superfighters Deluxe")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) { }
    }
}