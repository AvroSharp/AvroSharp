#if NETFRAMEWORK
using System.Linq;
using System.Reflection;
using AvroSharp.Serialization;

namespace AvroSharp.Tests.Framework;

/// <summary>
/// On .NET Framework, generated types don't register themselves (that needs a module initializer, .NET 5 and later),
/// so the tests register every generated type of this assembly first, with the call a .NET Framework application
/// makes: <c>AvroTypes.Register(Order.AvroTypeInfo)</c>. Shared by the add-on test projects.
/// </summary>
public static class FrameworkRegistration
{
    [Before(HookType.Assembly)]
    public static void RegisterGeneratedTypes()
    {
        var register = typeof(AvroTypes).GetMethod(nameof(AvroTypes.Register))!;
        foreach (var type in typeof(FrameworkRegistration).Assembly.GetTypes().Where(t => !t.ContainsGenericParameters))
        {
            var info = type.GetProperty("AvroTypeInfo", BindingFlags.Public | BindingFlags.Static);
            if (info?.PropertyType == typeof(AvroTypeInfo<>).MakeGenericType(type))
            {
                register.MakeGenericMethod(type).Invoke(null, [info.GetValue(null)]);
            }
        }
    }
}
#endif
