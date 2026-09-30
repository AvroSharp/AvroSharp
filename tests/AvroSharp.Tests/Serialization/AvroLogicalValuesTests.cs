using System;
using System.Buffers;
using System.Globalization;
using System.Threading.Tasks;
using AvroSharp.IO;
using AvroSharp.Serialization;

namespace AvroSharp.Tests.Serialization;

/// <summary>Edge cases of the logical-type conversions that generated code uses. net481 runs the netstandard paths.</summary>
public class AvroLogicalValuesTests
{
    [Test]
    [Arguments("9999999999999999999999999999", 0, 28)]    // the largest 28-digit value
    [Arguments("-9999999999999999999999999999", 0, 28)]
    [Arguments("0", 4, 12)]
    [Arguments("-0.0001", 4, 12)]
    [Arguments("12345678.9012", 4, 12)]
    [Arguments("-128", 0, 3)]                             // one byte: 0x80
    [Arguments("128", 0, 3)]                              // two bytes: 0x00 0x80
    public async Task Decimals_RoundTripThroughBytesAndFixed(string text, int scale, int precision)
    {
        var value = decimal.Parse(text, CultureInfo.InvariantCulture);

        var bytes = Write((ref w) => AvroLogicalValues.WriteDecimalBytes(ref w, value, scale, precision));
        var fromBytes = Read(bytes, (ref r) => AvroLogicalValues.ReadDecimalBytes(ref r, scale));

        // A fixed larger than 16 bytes is sign-extended.
        var fixedBytes = Write((ref w) => AvroLogicalValues.WriteDecimalFixed(ref w, value, scale, precision, 20));
        var fromFixed = Read(fixedBytes, (ref r) => AvroLogicalValues.ReadDecimalFixed(ref r, scale, 20));

        await Assert.That(fromBytes).IsEqualTo(value);
        await Assert.That(fromFixed).IsEqualTo(value);
        await Assert.That(fixedBytes.Length).IsEqualTo(20);
    }

    [Test]
    public async Task Decimals_UseTheFewestBytes()
    {
        await Assert.That(Convert.ToHexString(Write((ref w) => AvroLogicalValues.WriteDecimalBytes(ref w, -128m, 0, 3)))).IsEqualTo("0280");
        await Assert.That(Convert.ToHexString(Write((ref w) => AvroLogicalValues.WriteDecimalBytes(ref w, 128m, 0, 3)))).IsEqualTo("040080");
        await Assert.That(Convert.ToHexString(Write((ref w) => AvroLogicalValues.WriteDecimalBytes(ref w, 0m, 0, 3)))).IsEqualTo("0200");
    }

    [Test]
    public async Task Decimals_WithRedundantSignBytes_AreRead()
    {
        // Other writers may pad: 20 bytes of 0xFF ... 0x6A is still -150.
        var bytes = new byte[20];
        bytes.AsSpan().Fill(0xFF);
        bytes[19] = 0x6A;
        var encoded = Write((ref w) => w.WriteBytes(bytes));

        await Assert.That(Read(encoded, (ref r) => AvroLogicalValues.ReadDecimalBytes(ref r, 2))).IsEqualTo(-1.50m);
    }

    [Test]
    public async Task DecimalsBeyond96Bits_AreRejectedWhenReading()
    {
        // 2^96 needs 13 significant bytes with a set top bit: 0x01 followed by twelve zero bytes.
        var tooLarge = new byte[13];
        tooLarge[0] = 0x01;
        var encoded = Write((ref w) => w.WriteBytes(tooLarge));

        var ex = Assert.Throws<AvroDataException>(() => Read(encoded, (ref r) => AvroLogicalValues.ReadDecimalBytes(ref r, 0)));
        await Assert.That(ex.Message).Contains("does not fit in System.Decimal");
    }

    [Test]
    public async Task DecimalsWithNoBytes_AreRejected()
    {
        // The unscaled value is a two's-complement integer of at least one byte, as Java's BigInteger requires (#111).
        var encoded = Write((ref w) => w.WriteBytes([]));
        var ex = Assert.Throws<AvroDataException>(() => Read(encoded, (ref r) => AvroLogicalValues.ReadDecimalBytes(ref r, 2)));
        await Assert.That(ex.Message).Contains("has no bytes");
    }

    [Test]
    public async Task Uuids_UseRfc4122ByteOrderOnFixed()
    {
        var uuid = new Guid("00112233-4455-6677-8899-aabbccddeeff");

        var bytes = Write((ref w) => AvroLogicalValues.WriteUuidFixed(ref w, uuid));

        await Assert.That(Convert.ToHexString(bytes)).IsEqualTo("00112233445566778899AABBCCDDEEFF");
        await Assert.That(Read(bytes, AvroLogicalValues.ReadUuidFixed)).IsEqualTo(uuid);
    }

    [Test]
    public async Task UuidStrings_AreTheLowerCaseDForm_AndReadInEitherCase()
    {
        var uuid = new Guid("00112233-4455-6677-8899-aabbccddeeff");

        var bytes = Write((ref w) => AvroLogicalValues.WriteUuidString(ref w, uuid));
        var upper = Write((ref w) => w.WriteString("00112233-4455-6677-8899-AABBCCDDEEFF"));

        await Assert.That(Read(bytes, (ref r) => r.ReadString())).IsEqualTo("00112233-4455-6677-8899-aabbccddeeff");
        await Assert.That(Read(bytes, AvroLogicalValues.ReadUuidString)).IsEqualTo(uuid);
        await Assert.That(Read(upper, AvroLogicalValues.ReadUuidString)).IsEqualTo(uuid);
    }

    [Test]
    [Arguments("{00112233-4455-6677-8899-aabbccddeeff}")]
    [Arguments("00112233445566778899aabbccddeeff")]
    [Arguments("00112233-4455-6677-8899-aabbccddeeff ")]
    [Arguments("00112233-4455-6677-8899-aabbccddeefg")]
    [Arguments("")]
    public async Task UuidStrings_InAnotherForm_AreRejected(string text)
    {
        var bytes = Write((ref w) => w.WriteString(text));

        var ex = Assert.Throws<AvroDataException>(() => Read(bytes, AvroLogicalValues.ReadUuidString));

        await Assert.That(ex.Message).IsEqualTo($"'{text}' is not a UUID in the form xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.");
    }

    [Test]
    [Arguments(-1L, "1969-12-31T23:59:59.9990000+00:00")]
    [Arguments(0L, "1970-01-01T00:00:00.0000000+00:00")]
    [Arguments(-62_135_596_800_000L, "0001-01-01T00:00:00.0000000+00:00")]
    public async Task Timestamps_ConvertBothWays(long milliseconds, string expected)
    {
        var timestamp = AvroLogicalValues.TimestampFromMilliseconds(milliseconds);

        await Assert.That(timestamp.ToString("O", CultureInfo.InvariantCulture)).IsEqualTo(expected);
        await Assert.That(AvroLogicalValues.MillisecondsFromTimestamp(timestamp)).IsEqualTo(milliseconds);
    }

    [Test]
    public async Task TimestampsOutsideDateTime_AreRejected()
    {
        var ex = Assert.Throws<AvroDataException>(() => AvroLogicalValues.TimestampFromMilliseconds(long.MaxValue));
        await Assert.That(ex.Message).Contains("outside the range of DateTime");
    }

    [Test]
    public async Task ADecimalThatDoesNotFitTheFixedSize_IsRejected()
    {
        // 1000 needs two bytes; the precision allows it, the size does not.
        var ex = Assert.Throws<AvroException>(() => Write((ref w) => AvroLogicalValues.WriteDecimalFixed(ref w, 1000m, 0, 4, 1)));
        await Assert.That(ex.Message).IsEqualTo("The decimal 1000 does not fit in 1 bytes.");
    }

    [Test]
    [Arguments(29, 29)]
    [Arguments(0, 29)]
    [Arguments(29, 1)]
    public async Task DecimalScalesAndPrecisionsAbove28_AreRejectedWhenWriting(int scale, int precision)
    {
        var bytes = Assert.Throws<AvroException>(() => Write((ref w) => AvroLogicalValues.WriteDecimalBytes(ref w, 1m, scale, precision)));
        var fixedSize = Assert.Throws<AvroException>(() => Write((ref w) => AvroLogicalValues.WriteDecimalFixed(ref w, 1m, scale, precision, 16)));

        var expected = $"A decimal(precision {precision}, scale {scale}) is outside the range of System.Decimal.";
        await Assert.That(bytes.Message).IsEqualTo(expected);
        await Assert.That(fixedSize.Message).IsEqualTo(expected);
    }

    [Test]
    public async Task DecimalScalesAbove28_AreRejectedWhenReading()
    {
        var encoded = Write((ref w) => w.WriteBytes([0x01]));
        var ex = Assert.Throws<AvroDataException>(() => Read(encoded, (ref r) => AvroLogicalValues.ReadDecimalBytes(ref r, 29)));
        await Assert.That(ex.Message).IsEqualTo("A decimal scale of 29 is outside the range of System.Decimal (0 to 28).");
    }

    [Test]
    public async Task TimesOfDay_AreChecked()
    {
        var day = Assert.Throws<AvroException>(() => AvroLogicalValues.MillisecondsFromTime(TimeSpan.FromHours(24)));
        var negative = Assert.Throws<AvroException>(() => AvroLogicalValues.MicrosecondsFromTime(TimeSpan.FromTicks(-1)));
        await Assert.That(day.Message).EndsWith(" is not a time of day (from zero up to 24 hours).");
        await Assert.That(negative.Message).EndsWith(" is not a time of day (from zero up to 24 hours).");
        await Assert.That(AvroLogicalValues.MicrosecondsFromTime(TimeSpan.FromTicks(19))).IsEqualTo(1L);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(86_400_000)]
    public async Task TimeMillisOutsideADay_AreRejectedWhenReading(int milliseconds)
    {
        var ex = Assert.Throws<AvroDataException>(() => AvroLogicalValues.TimeFromMilliseconds(milliseconds));
        await Assert.That(ex.Message).IsEqualTo("The time-millis value is not a time of day.");
    }

    [Test]
    [Arguments(-1L)]
    [Arguments(86_400_000_000L)]
    public async Task TimeMicrosOutsideADay_AreRejectedWhenReading(long microseconds)
    {
        var ex = Assert.Throws<AvroDataException>(() => AvroLogicalValues.TimeFromMicroseconds(microseconds));
        await Assert.That(ex.Message).IsEqualTo($"The time-micros value {microseconds.ToString(CultureInfo.InvariantCulture)} is not a time of day.");
    }

    private static byte[] Write(WriteAction write)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new AvroWriter(output);
        write(ref writer);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static T Read<T>(byte[] data, ReadFunc<T> read)
    {
        var reader = new AvroReader(data);
        return read(ref reader);
    }

    private delegate void WriteAction(ref AvroWriter writer);

    private delegate T ReadFunc<T>(ref AvroReader reader);
}
