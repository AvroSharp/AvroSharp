using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.IO;
using CsCheck;
using ApacheDecoder = Avro.IO.BinaryDecoder;
using ApacheEncoder = Avro.IO.BinaryEncoder;

namespace AvroSharp.Interop.Tests;

/// <summary>AvroSharp's binary encoding must be byte-identical to Apache.Avro (C#), in both directions.</summary>
public class ApacheAvroBinaryInteropTests
{
    private const int Iterations = 5_000;

    /// <summary>A value of one Avro primitive kind.</summary>
    private readonly record struct Value(int Kind, long Long, double Double, string Text, byte[] Bytes);

    private static readonly Gen<Value> s_value = Gen.OneOf(
        Gen.Int.Select(i => new Value(0, i, 0, string.Empty, [])),
        Gen.Long.Select(l => new Value(1, l, 0, string.Empty, [])),
        Gen.Bool.Select(b => new Value(2, b ? 1 : 0, 0, string.Empty, [])),
        Gen.Float.Select(f => new Value(3, 0, f, string.Empty, [])),
        Gen.Double.Select(d => new Value(4, 0, d, string.Empty, [])),
        Gen.String.Select(s => new Value(5, 0, 0, s, [])),
        Gen.Byte.Array[0, 300].Select(b => new Value(6, 0, 0, string.Empty, b)),
        Gen.Byte.Array[1, 20].Select(b => new Value(7, 0, 0, string.Empty, b)));

    [Test]
    public async Task RandomValues_EncodeToTheSameBytesAsApache()
    {
        s_value.Array[0, 50].Sample(
            values =>
            {
                var ours = WriteWithAvroSharp(values);
                var theirs = WriteWithApache(values);
                if (!ours.AsSpan().SequenceEqual(theirs))
                {
                    throw new InvalidOperationException(
                        $"Encodings differ.\nAvroSharp: {Convert.ToHexString(ours)}\nApache:    {Convert.ToHexString(theirs)}");
                }
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task RandomValues_WrittenByApache_AreReadByAvroSharp()
    {
        s_value.Array[0, 50].Sample(
            values =>
            {
                var bytes = WriteWithApache(values);
                var reader = new AvroReader(bytes);
                foreach (var value in values)
                {
                    CheckRead(ref reader, value);
                }

                if (!reader.IsAtEnd)
                {
                    throw new InvalidOperationException("Bytes left over.");
                }
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task RandomValues_WrittenByAvroSharp_AreReadByApache()
    {
        s_value.Array[0, 50].Sample(
            values =>
            {
                using var stream = new MemoryStream(WriteWithAvroSharp(values));
                var decoder = new ApacheDecoder(stream);
                foreach (var value in values)
                {
                    var read = value.Kind switch
                    {
                        0 => decoder.ReadInt() == (int)value.Long,
                        1 => decoder.ReadLong() == value.Long,
                        2 => decoder.ReadBoolean() == (value.Long == 1),
                        3 => BitConverter.SingleToInt32Bits(decoder.ReadFloat()) == BitConverter.SingleToInt32Bits((float)value.Double),
                        4 => BitConverter.DoubleToInt64Bits(decoder.ReadDouble()) == BitConverter.DoubleToInt64Bits(value.Double),
                        5 => string.Equals(decoder.ReadString(), value.Text, StringComparison.Ordinal),
                        6 => decoder.ReadBytes().AsSpan().SequenceEqual(value.Bytes),
                        _ => ReadApacheFixed(decoder, value.Bytes.Length).AsSpan().SequenceEqual(value.Bytes),
                    };

                    if (!read)
                    {
                        throw new InvalidOperationException($"Apache read a different value for kind {value.Kind}.");
                    }
                }

                if (stream.Position != stream.Length)
                {
                    throw new InvalidOperationException("Bytes left over.");
                }
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task Arrays_EncodeLikeApache()
    {
        Gen.Long.Array[0, 40].Array[0, 5].Sample(
            blocks =>
            {
                var output = new ArrayBufferWriter<byte>();
                var writer = new AvroWriter(output);
                foreach (var block in blocks.Where(b => b.Length > 0))
                {
                    writer.WriteBlockCount(block.Length);
                    foreach (var item in block)
                    {
                        writer.WriteLong(item);
                    }
                }

                writer.WriteBlockEnd();
                writer.Flush();

                using var stream = new MemoryStream();
                var encoder = new ApacheEncoder(stream);
                encoder.WriteArrayStart();
                foreach (var block in blocks.Where(b => b.Length > 0))
                {
                    encoder.SetItemCount(block.Length);
                    foreach (var item in block)
                    {
                        encoder.StartItem();
                        encoder.WriteLong(item);
                    }
                }

                encoder.WriteArrayEnd();
                encoder.Flush();

                if (!output.WrittenSpan.SequenceEqual(stream.ToArray()))
                {
                    throw new InvalidOperationException($"Array encodings differ.\nAvroSharp: {Convert.ToHexString(output.WrittenSpan.ToArray())}\nApache:    {Convert.ToHexString(stream.ToArray())}");
                }
            },
            iter: 1_000);

        await Task.CompletedTask;
    }

    private static byte[] WriteWithAvroSharp(Value[] values)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        foreach (var value in values)
        {
            switch (value.Kind)
            {
                case 0: writer.WriteInt((int)value.Long); break;
                case 1: writer.WriteLong(value.Long); break;
                case 2: writer.WriteBoolean(value.Long == 1); break;
                case 3: writer.WriteFloat((float)value.Double); break;
                case 4: writer.WriteDouble(value.Double); break;
                case 5: writer.WriteString(value.Text); break;
                case 6: writer.WriteBytes(value.Bytes); break;
                default: writer.WriteFixed(value.Bytes); break;
            }
        }

        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static byte[] WriteWithApache(Value[] values)
    {
        using var stream = new MemoryStream();
        var encoder = new ApacheEncoder(stream);
        foreach (var value in values)
        {
            switch (value.Kind)
            {
                case 0: encoder.WriteInt((int)value.Long); break;
                case 1: encoder.WriteLong(value.Long); break;
                case 2: encoder.WriteBoolean(value.Long == 1); break;
                case 3: encoder.WriteFloat((float)value.Double); break;
                case 4: encoder.WriteDouble(value.Double); break;
                case 5: encoder.WriteString(value.Text); break;
                case 6: encoder.WriteBytes(value.Bytes); break;
                default: encoder.WriteFixed(value.Bytes); break;
            }
        }

        encoder.Flush();
        return stream.ToArray();
    }

    private static void CheckRead(ref AvroReader reader, Value value)
    {
        var ok = value.Kind switch
        {
            0 => reader.ReadInt() == (int)value.Long,
            1 => reader.ReadLong() == value.Long,
            2 => reader.ReadBoolean() == (value.Long == 1),
            3 => BitConverter.SingleToInt32Bits(reader.ReadFloat()) == BitConverter.SingleToInt32Bits((float)value.Double),
            4 => BitConverter.DoubleToInt64Bits(reader.ReadDouble()) == BitConverter.DoubleToInt64Bits(value.Double),
            5 => string.Equals(reader.ReadString(), value.Text, StringComparison.Ordinal),
            6 => reader.ReadBytesSpan().SequenceEqual(value.Bytes),
            _ => reader.ReadFixedSpan(value.Bytes.Length).SequenceEqual(value.Bytes),
        };

        if (!ok)
        {
            throw new InvalidOperationException($"AvroSharp read a different value for kind {value.Kind}.");
        }
    }

    private static byte[] ReadApacheFixed(ApacheDecoder decoder, int size)
    {
        var buffer = new byte[size];
        decoder.ReadFixed(buffer);
        return buffer;
    }
}
