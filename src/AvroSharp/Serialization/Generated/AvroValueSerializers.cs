using System.ComponentModel;
using AvroSharp.IO;

#pragma warning disable CA1815 // The serializers are stateless; generated code only uses default(TSerializer).

namespace AvroSharp.Serialization.Generated;

/// <summary>The argument of a generated record's constructor for readers, which skips the property initializers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroUninitialized
{
}

/// <summary>The serializer of <c>boolean</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroBooleanSerializer : IAvroValueSerializer<bool>
{
    /// <inheritdoc />
    public bool Read(ref AvroReader reader, int depth) => reader.ReadBoolean();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, bool value, int depth) => writer.WriteBoolean(value);
}

/// <summary>The serializer of <c>int</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroIntSerializer : IAvroValueSerializer<int>
{
    /// <inheritdoc />
    public int Read(ref AvroReader reader, int depth) => reader.ReadInt();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, int value, int depth) => writer.WriteInt(value);
}

/// <summary>The serializer of <c>long</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroLongSerializer : IAvroValueSerializer<long>
{
    /// <inheritdoc />
    public long Read(ref AvroReader reader, int depth) => reader.ReadLong();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, long value, int depth) => writer.WriteLong(value);
}

/// <summary>The serializer of <c>float</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroFloatSerializer : IAvroValueSerializer<float>
{
    /// <inheritdoc />
    public float Read(ref AvroReader reader, int depth) => reader.ReadFloat();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, float value, int depth) => writer.WriteFloat(value);
}

/// <summary>The serializer of <c>double</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroDoubleSerializer : IAvroValueSerializer<double>
{
    /// <inheritdoc />
    public double Read(ref AvroReader reader, int depth) => reader.ReadDouble();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, double value, int depth) => writer.WriteDouble(value);
}

/// <summary>The serializer of <c>string</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroStringSerializer : IAvroValueSerializer<string>
{
    /// <inheritdoc />
    public string Read(ref AvroReader reader, int depth) => reader.ReadString();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, string value, int depth) => writer.WriteString(value);
}

/// <summary>The serializer of <c>bytes</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroBytesSerializer : IAvroValueSerializer<byte[]>
{
    /// <inheritdoc />
    public byte[] Read(ref AvroReader reader, int depth) => reader.ReadBytes();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, byte[] value, int depth) => writer.WriteBytes(value);
}
