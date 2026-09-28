using System.ComponentModel;
using AvroSharp.IO;

#pragma warning disable CA1815 // The codecs are stateless; generated code only uses default(TCodec).

namespace AvroSharp.Serialization;

/// <summary>The argument of a generated record's constructor for readers, which skips the property initializers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroUninitialized
{
}

/// <summary>The codec of <c>boolean</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroBooleanCodec : IAvroCodec<bool>
{
    /// <inheritdoc />
    public bool Read(ref AvroReader reader, int depth) => reader.ReadBoolean();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, bool value, int depth) => writer.WriteBoolean(value);
}

/// <summary>The codec of <c>int</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroIntCodec : IAvroCodec<int>
{
    /// <inheritdoc />
    public int Read(ref AvroReader reader, int depth) => reader.ReadInt();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, int value, int depth) => writer.WriteInt(value);
}

/// <summary>The codec of <c>long</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroLongCodec : IAvroCodec<long>
{
    /// <inheritdoc />
    public long Read(ref AvroReader reader, int depth) => reader.ReadLong();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, long value, int depth) => writer.WriteLong(value);
}

/// <summary>The codec of <c>float</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroFloatCodec : IAvroCodec<float>
{
    /// <inheritdoc />
    public float Read(ref AvroReader reader, int depth) => reader.ReadFloat();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, float value, int depth) => writer.WriteFloat(value);
}

/// <summary>The codec of <c>double</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroDoubleCodec : IAvroCodec<double>
{
    /// <inheritdoc />
    public double Read(ref AvroReader reader, int depth) => reader.ReadDouble();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, double value, int depth) => writer.WriteDouble(value);
}

/// <summary>The codec of <c>string</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroStringCodec : IAvroCodec<string>
{
    /// <inheritdoc />
    public string Read(ref AvroReader reader, int depth) => reader.ReadString();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, string value, int depth) => writer.WriteString(value);
}

/// <summary>The codec of <c>bytes</c> values, for <see cref="AvroGeneratedCode"/>'s helpers.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct AvroBytesCodec : IAvroCodec<byte[]>
{
    /// <inheritdoc />
    public byte[] Read(ref AvroReader reader, int depth) => reader.ReadBytes();

    /// <inheritdoc />
    public void Write(ref AvroWriter writer, byte[] value, int depth) => writer.WriteBytes(value);
}
