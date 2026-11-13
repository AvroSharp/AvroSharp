using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Interop.Tests;

namespace AvroSharp.Generators.Consumer.Tests;

/// <summary>Types in two files that need each other (Schemas/cycle-left.avsc and cycle-right.avsc) generate and round-trip (#16).</summary>
public class CrossFileCycleTests
{
    [Test]
    public async Task TypesThatNeedEachOther_RoundTrip()
    {
        var left = new cycle.Left
        {
            Id = 1,
            Right = new cycle.Right { Label = "r", Kind = cycle.Kind.DOWN, Left = new cycle.Left { Id = 2 } },
            Rights = [new cycle.Right { Label = "s", Kind = cycle.Kind.UP }],
        };

        var read = cycle.Left.FromAvroBytes(left.ToAvroBytes());

        await Assert.That(read.Right!.Left!.Id).IsEqualTo(2);
        await Assert.That(read.Right.Kind).IsEqualTo(cycle.Kind.DOWN);
        await Assert.That(read.Rights[0].Label).IsEqualTo("s");
        await Assert.That(read.ToAvroBytes()).IsEquivalentTo(left.ToAvroBytes(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task TypesThatNeedEachOther_RoundTripRandomValues()
    {
        var checkedSamples = 0;
        for (var seed = 0; seed < 50; seed++)
        {
            if (new RandomValues(seed).TryCreate(cycle.Right.Schema) is not { } value)
            {
                continue;
            }

            var bytes = GenericDatumWriter.Create(cycle.Right.Schema).WriteToArray(value);
            await Assert.That(cycle.Right.FromAvroBytes(bytes).ToAvroBytes()).IsEquivalentTo(bytes, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            checkedSamples++;
        }

        await Assert.That(checkedSamples).IsEqualTo(50);
    }
}
