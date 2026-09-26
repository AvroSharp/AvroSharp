namespace AvroSharp.Containers;

/// <summary>Constants of the object container file format.</summary>
internal static class AvroContainerFormat
{
    /// <summary>The first four bytes of every file: <c>Obj</c> and the format version 1.</summary>
    public static readonly byte[] Magic = [(byte)'O', (byte)'b', (byte)'j', 1];

    public const int SyncSize = 16;

    public const string SchemaKey = "avro.schema";

    public const string CodecKey = "avro.codec";

    public const string ReservedPrefix = "avro.";
}
