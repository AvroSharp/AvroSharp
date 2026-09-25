using System.Threading.Tasks;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Schemas;

/// <summary>The specification's table of field default values.</summary>
public class DefaultValueTests
{
    private static AvroSchema ParseField(string type, string defaultValue, AvroSchemaParseOptions? options = null) =>
        AvroSchema.Parse($$"""{"type":"record","name":"R","fields":[{"name":"f","type":{{type}},"default":{{defaultValue}}}]}""", options);

    [Test]
    [Arguments("\"null\"", "null")]
    [Arguments("\"boolean\"", "true")]
    [Arguments("\"int\"", "-2147483648")]
    [Arguments("\"long\"", "9223372036854775807")]
    [Arguments("\"float\"", "1.5")]
    [Arguments("\"double\"", "1e300")]
    [Arguments("\"double\"", "3")]
    [Arguments("\"bytes\"", "\"\\u00ff\\u0000abc\"")]
    [Arguments("\"string\"", "\"foo\"")]
    [Arguments("""{"type":"fixed","name":"F","size":2}""", "\"\\u00ffa\"")]
    [Arguments("""{"type":"enum","name":"E","symbols":["A","B"]}""", "\"B\"")]
    [Arguments("""{"type":"array","items":"int"}""", "[1,2,3]")]
    [Arguments("""{"type":"map","values":"long"}""", """{"a":1}""")]
    [Arguments("""{"type":"record","name":"Inner","fields":[{"name":"a","type":"int"},{"name":"b","type":"string","default":"x"}]}""", """{"a":1}""")]
    [Arguments("""["null","string"]""", "null")]
    [Arguments("""["null","string"]""", "\"s\"")]
    [Arguments("""["string","null"]""", "null")]
    [Arguments("""{"type":"int","logicalType":"date"}""", "19000")]
    public async Task ValidDefault_IsAccepted(string type, string defaultValue)
    {
        var record = (RecordSchema)ParseField(type, defaultValue);
        await Assert.That(record.Fields[0].HasDefaultValue).IsTrue();
        await Assert.That(record.Fields[0].DefaultValue!.Value.GetRawText()).IsEqualTo(defaultValue);
    }

    [Test]
    [Arguments("\"null\"", "0")]
    [Arguments("\"boolean\"", "\"true\"")]
    [Arguments("\"int\"", "2147483648")]
    [Arguments("\"int\"", "1.5")]
    [Arguments("\"long\"", "\"1\"")]
    [Arguments("\"float\"", "\"NaN\"")]
    [Arguments("\"bytes\"", "\"\\u0100\"")]
    [Arguments("\"string\"", "1")]
    [Arguments("""{"type":"fixed","name":"F","size":2}""", "\"a\"")]
    [Arguments("""{"type":"enum","name":"E","symbols":["A"]}""", "\"C\"")]
    [Arguments("""{"type":"array","items":"int"}""", "[1,\"x\"]")]
    [Arguments("""{"type":"map","values":"long"}""", "[1]")]
    [Arguments("""{"type":"record","name":"Inner","fields":[{"name":"a","type":"int"}]}""", "{}")]
    [Arguments("""["null","string"]""", "1")]
    public async Task InvalidDefault_IsRejectedAtItsLocation(string type, string defaultValue)
    {
        var ex = Assert.Throws<AvroSchemaException>(() => ParseField(type, defaultValue));
        await Assert.That(ex.Path).IsEqualTo("$.fields[0].default");
        await Assert.That(ex.Reason!).Contains("does not match its schema");
    }

    [Test]
    public async Task InvalidDefault_IsAcceptedWhenValidationIsDisabled()
    {
        var record = (RecordSchema)ParseField("\"int\"", "\"not a number\"", new AvroSchemaParseOptions { ValidateDefaults = false });
        await Assert.That(record.Fields[0].DefaultValue!.Value.GetString()).IsEqualTo("not a number");
    }

    [Test]
    public async Task Default_MayUseTheRecordBeingDefined()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"Node","fields":[
              {"name":"value","type":"int","default":0},
              {"name":"children","type":{"type":"array","items":"Node"},"default":[{"value":1,"children":[]}]}
            ]}
            """);
        await Assert.That(schema.Fields[1].HasDefaultValue).IsTrue();
    }
}
