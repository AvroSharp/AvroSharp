using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AvroSharp.Generic;
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

    /// <summary>
    /// An escaped unpaired surrogate is valid JSON syntax but not Unicode text, and threw InvalidOperationException from
    /// the schema parser (in a name, doc, symbol, default or custom property, and so in a container file's header) and
    /// from the JSON reader. Pairs, and escaped backslashes before a <c>u</c>, are still read.
    /// </summary>
    [Test]
    [Arguments("""{"type":"record","name":"R","doc":"\ud800","fields":[]}""")]
    [Arguments("""{"type":"record","name":"R","doc":"x\udc00y","fields":[]}""")]
    [Arguments("""{"type":"enum","name":"E","symbols":["A"],"doc":"\uD83D"}""")]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"s","type":"string","default":"\ud800"}]}""")]
    [Arguments("""{"type":"int","x":"\ud800"}""")]
    [Arguments("""{"type":"int","x":{"\ud800":1}}""")]
    public async Task AnEscapedUnpairedSurrogate_InASchema_IsASchemaError(string json)
    {
        var ex = Assert.Throws<AvroSchemaException>(() => AvroSchema.Parse(json));

        await Assert.That(ex.Message).StartsWith("The schema JSON escapes an unpaired surrogate");
    }

    /// <summary>
    /// Text that ends in a backslash, an unfinished escape, is a JSON error. The surrogate check, which runs first,
    /// threw ArgumentOutOfRangeException for it (found by the SchemaParse and ContainerFile fuzz targets).
    /// </summary>
    [Test]
    [Arguments("\\")]
    [Arguments("{\"type\":\"int\",\"x\":\"a\\")]
    [Arguments("{\"type\":\"int\",\"x\":\"\\\\\\")]
    [Arguments("{\"type\":\"int\",\"x\":\"\\u12")]
    [Arguments("{\"type\":\"int\",\"x\":\"\\ud800\\")]
    public async Task ABackslashAtTheEnd_IsASchemaError(string json)
    {
        await Assert.That(() => AvroSchema.Parse(json)).Throws<AvroSchemaException>();
    }

    [Test]
    public async Task EscapedSurrogatePairs_AndEscapedBackslashes_AreRead()
    {
        var schema = AvroSchema.Parse("""{"type":"record","name":"R","doc":"\ud83d\ude00 a\\ud800","fields":[]}""");

        await Assert.That(((RecordSchema)schema).Doc).IsEqualTo("\U0001F600 a\\ud800");
        await Assert.That(GenericDatumJsonReader.Create(AvroSchema.String).Read("\"\\ud83d\\ude00\"").AsString()).IsEqualTo("\U0001F600");
    }

    [Test]
    [Arguments("\"\\ud800\"", "\"string\"")]
    [Arguments("{\"\\udc00\":1}", """{"type":"map","values":"int"}""")]
    [Arguments("\"\\ud800\"", """{"type":"enum","name":"E","symbols":["A"]}""")]
    public async Task AnEscapedUnpairedSurrogate_InJsonData_IsADataError(string json, string schemaJson)
    {
        var reader = GenericDatumJsonReader.Create(AvroSchema.Parse(schemaJson));

        var fromString = Assert.Throws<AvroDataException>(() => reader.Read(json));
        var fromUtf8 = Assert.Throws<AvroDataException>(() => reader.Read(Encoding.UTF8.GetBytes(json)));

        await Assert.That(fromString.Message).IsEqualTo("The JSON contains a string that is not Unicode text: invalid UTF-8, or an escaped unpaired surrogate.");
        await Assert.That(fromUtf8.Message).IsEqualTo(fromString.Message);
    }

    private static byte[] Wrap(string before, string hex, string after) =>
        [.. Encoding.UTF8.GetBytes(before), .. Convert.FromHexString(hex), .. Encoding.UTF8.GetBytes(after)];
}
