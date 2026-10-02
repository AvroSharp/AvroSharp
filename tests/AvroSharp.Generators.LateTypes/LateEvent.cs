using AvroSharp.Serialization;

namespace AvroSharp.Generators.LateTypes;

/// <summary>A message type whose assembly no test code calls.</summary>
[AvroSerializable]
public sealed partial class LateEvent
{
    public string Name { get; set; } = "";

    public int Count { get; set; }
}
