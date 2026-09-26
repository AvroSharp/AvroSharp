using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Tests;

/// <summary>
/// Invalid UTF-8 inside JSON strings passes the JSON tokenizer, so it is checked separately. On net481 these run the
/// netstandard validator; on net8+ the BCL's.
/// </summary>
public class Utf8ValidationTests
{
    [Test]
    [Arguments("C0AF", "overlong two-byte form")]
    [Arguments("E080AF", "overlong three-byte form")]
    [Arguments("EDA080", "UTF-16 surrogate")]
    [Arguments("F4908080", "above U+10FFFF")]
    [Arguments("E282", "truncated sequence")]
    [Arguments("80", "continuation byte without a lead byte")]
    [Arguments("FF", "byte that never appears in UTF-8")]
    [Arguments("E228A1", "bad continuation byte")]
    public async Task InvalidUtf8InAString_IsReportedAsAnAvroError(string hex, string description)
    {
        var schemaJson = Wrap("""{"type":"enum","name":"E","symbols":["A"],"doc":" """, hex, """ "}""");
        var schemaError = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse(schemaJson));
        await Assert.That(schemaError.Message).Contains("not valid UTF-8").Because(description);

        var reader = GenericDatumJsonReader.Create(AvroSchema.Parse("\"string\""));
        var dataError = Assert.Throws<AvroDataException>(() => reader.Read(Wrap("\"", hex, "\"")));
        await Assert.That(dataError.Message).Contains("not valid UTF-8").Because(description);
    }

    [Test]
    [Arguments("7A", "z")]
    [Arguments("C3A9", "é")]
    [Arguments("E697A5", "日")]
    [Arguments("EFBFBF", "￿")]
    [Arguments("F09F8E89", "🎉")]
    [Arguments("F48FBFBF", "\U0010FFFF")]
    public async Task ValidUtf8_IsAccepted(string hex, string expected)
    {
        var reader = GenericDatumJsonReader.Create(AvroSchema.Parse("\"string\""));
        await Assert.That(reader.Read(Wrap("\"", hex, "\"")).AsString()).IsEqualTo(expected);
    }

    private static byte[] Wrap(string before, string hex, string after) =>
        [.. Encoding.UTF8.GetBytes(before), .. Convert.FromHexString(hex), .. Encoding.UTF8.GetBytes(after)];
}
