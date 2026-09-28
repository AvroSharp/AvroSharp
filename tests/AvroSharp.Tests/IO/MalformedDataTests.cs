using System;
using System.Threading.Tasks;
using AvroSharp.IO;

namespace AvroSharp.Tests.IO;

/// <summary>Malformed or truncated input is rejected with <see cref="AvroDataException"/>, before any large allocation.</summary>
public class MalformedDataTests
{
    private delegate void ReadAction(ref AvroReader reader);

    [Test]
    [Arguments("", "int")]
    [Arguments("80", "int")]
    [Arguments("FFFFFFFFFF", "int")]
    [Arguments("FFFFFFFFFFFFFFFFFFFF", "long")]
    [Arguments("", "boolean")]
    [Arguments("02", "boolean")]
    [Arguments("000000", "float")]
    [Arguments("00000000000000", "double")]
    [Arguments("01", "bytes")]
    [Arguments("0A0102", "bytes")]
    [Arguments("0A6162", "string")]
    [Arguments("FEFFFFFFFFFFFFFFFF01", "string")]
    [Arguments("0102", "fixed4")]
    [Arguments("01", "block")]
    [Arguments("0364", "block")]
    [Arguments("FFFFFFFFFFFFFFFFFF01", "block")]
    [Arguments("0102030405060708", "doubles2")]
    [Arguments("0A", "skipbytes")]
    public async Task MalformedInput_ThrowsAvroDataException(string hex, string what)
    {
        var bytes = Convert.FromHexString(hex);
        ReadAction read = what switch
        {
            "int" => (ref r) => r.ReadInt(),
            "long" => (ref r) => r.ReadLong(),
            "boolean" => (ref r) => r.ReadBoolean(),
            "float" => (ref r) => r.ReadFloat(),
            "double" => (ref r) => r.ReadDouble(),
            "bytes" => (ref r) => r.ReadBytes(),
            "string" => (ref r) => r.ReadString(),
            "fixed4" => (ref r) => r.ReadFixedSpan(4),
            "block" => (ref r) => r.ReadBlockCount(out _),
            "doubles2" => (ref r) => r.ReadDoubles(new double[2]),
            "skipbytes" => (ref r) => r.SkipBytes(),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        // The same input as one span, and split into one-byte segments.
        var fromSpan = Assert.Throws<AvroDataException>(() =>
        {
            var reader = new AvroReader(bytes);
            read(ref reader);
        });
        var fromSequence = Assert.Throws<AvroDataException>(() =>
        {
            var reader = new AvroReader(Segments.ByteByByte(bytes));
            read(ref reader);
        });

        await Assert.That(fromSpan.Message).IsNotEmpty();
        await Assert.That(fromSequence.Message).IsEqualTo(fromSpan.Message);
    }

    [Test]
    public async Task HugeLengthPrefix_IsRejectedWithoutAllocating()
    {
        // A 2^62-byte string claimed by 10 bytes of input: must fail on the length check.
        var bytes = Convert.FromHexString("FEFFFFFFFFFFFFFF7F");
#if NET
        var before = GC.GetAllocatedBytesForCurrentThread();
#endif
        var ex = Assert.Throws<AvroDataException>(() =>
        {
            var reader = new AvroReader(bytes);
            reader.ReadString();
        });

        await Assert.That(ex.Message).Contains("Invalid string length");
#if NET
        // .NET Framework has no per-thread allocation counter.
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(64 * 1024);
#endif
    }

    [Test]
    [Arguments("bytes")]
    [Arguments("string")]
    [Arguments("bytesSequence")]
    public async Task LengthLargerThanInput_IsRejectedWithoutAllocating(string what)
    {
        // A 1 GiB length (a valid int) followed by a few bytes: must fail on the length check, not after
        // allocating or renting a 1 GiB buffer.
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        writer.WriteLong(1L << 30);
        writer.WriteFixed([1, 2, 3]);
        writer.Flush();
        var bytes = output.WrittenSpan.ToArray();

#if NET
        var before = GC.GetAllocatedBytesForCurrentThread();
#endif
        var ex = Assert.Throws<AvroDataException>(() =>
        {
            var reader = string.Equals(what, "bytesSequence", StringComparison.Ordinal) ? new AvroReader(Segments.ByteByByte(bytes)) : new AvroReader(bytes);
            _ = string.Equals(what, "string", StringComparison.Ordinal) ? reader.ReadString().Length : reader.ReadBytes().Length;
        });

        await Assert.That(ex.Message).Contains("needs 1073741824 byte(s)");
#if NET
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(1024 * 1024);
#endif
    }

    [Test]
    public async Task FifthIntByte_HighBitsAreIgnoredLikeTheJavaImplementation()
    {
        // 0x7F in the fifth byte sets bits beyond 32; they are dropped, as Java's BinaryDecoder does.
        var value = new AvroReader(Convert.FromHexString("FFFFFFFF7F")).ReadInt();
        await Assert.That(value).IsEqualTo(int.MinValue);
    }

    [Test]
    public async Task ErrorMessages_IncludeTheOffset()
    {
        var ex = Assert.Throws<AvroDataException>(() =>
        {
            var reader = new AvroReader(Convert.FromHexString("0005"));
            reader.ReadBoolean();
            reader.ReadBoolean();
        });
        await Assert.That(ex.Message).Contains("offset 1");
    }
}
