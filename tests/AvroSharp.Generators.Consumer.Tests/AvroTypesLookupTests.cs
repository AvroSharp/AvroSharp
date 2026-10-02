using System;
using System.Threading.Tasks;
using AvroSharp.Serialization;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>
/// AvroTypes finds a generated type whose assembly is loaded but whose code hasn't run, so its module initializer
/// hasn't registered it yet: the lookup runs the initializer. Message frameworks find their message types this way,
/// by name or through a handler's signature.
/// </summary>
public class AvroTypesLookupTests
{
#if NET5_0_OR_GREATER
    [Test]
    public async Task ATypeWhoseAssemblyHasntRun_IsFoundByType()
    {
        // Loaded by name, as a resolver does: nothing in this assembly calls AvroSharp.Generators.LateTypes.
        var type = Type.GetType("AvroSharp.Generators.LateTypes.LateEvent, AvroSharp.Generators.LateTypes", throwOnError: true)!;

        var found = AvroTypes.TryGet(type, out var info);

        await Assert.That(found).IsTrue();
        await Assert.That(((AvroSharp.Schemas.RecordSchema)info!.Schema).FullName).IsEqualTo("AvroSharp.Generators.LateTypes.LateEvent");
    }
#endif

    [Test]
    public async Task ATypeThatIsntGenerated_IsStillNotFound()
    {
        await Assert.That(AvroTypes.TryGet(typeof(Uri), out _)).IsFalse();
        await Assert.That(AvroTypes.TryGet<Uri>(out _)).IsFalse();
    }
}
