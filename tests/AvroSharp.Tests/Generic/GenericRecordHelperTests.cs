using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;

namespace AvroSharp.Tests.Generic;

/// <summary>Records with defaults, the builder, validation without writing, and deep copies (#176).</summary>
public class GenericRecordHelperTests
{
    private const string UserSchema = """
        {"type":"record","name":"User","namespace":"app","fields":[
          {"name":"id","type":"long"},
          {"name":"name","type":"string","default":"anonymous"},
          {"name":"email","type":["null","string"],"default":null},
          {"name":"level","type":["int","null"],"default":1},
          {"name":"tags","type":{"type":"array","items":"string"},"default":["new"]},
          {"name":"limits","type":{"type":"map","values":"long"},"default":{"daily":10}},
          {"name":"address","type":{"type":"record","name":"Address","fields":[
            {"name":"city","type":"string","default":"Budapest"},
            {"name":"zip","type":["null","string"],"default":null}]},
           "default":{"city":"Szeged"}},
          {"name":"note","type":["null","string"]}
        ]}
        """;

    [Test]
    public async Task CreateWithDefaults_FillsEveryFieldThatHasADefault()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserSchema);

        var user = GenericRecord.CreateWithDefaults(schema);

        await Assert.That(user["id"].IsNull).IsTrue();
        await Assert.That(user["note"].IsNull).IsTrue();
        await Assert.That(user["name"].AsString()).IsEqualTo("anonymous");
        await Assert.That(user["email"].IsNull).IsTrue();
        await Assert.That(user["level"].AsInt32()).IsEqualTo(1);
        await Assert.That(user["tags"].AsArray().Count).IsEqualTo(1);
        await Assert.That(user["tags"].AsArray()[0].AsString()).IsEqualTo("new");
        await Assert.That(user["limits"].AsMap()["daily"].AsInt64()).IsEqualTo(10L);

        // The field's default, not the nested record's own: a missing zip takes the nested default.
        var address = user["address"].AsRecord();
        await Assert.That(address["city"].AsString()).IsEqualTo("Szeged");
        await Assert.That(address["zip"].IsNull).IsTrue();
    }

    [Test]
    public async Task CreateWithDefaults_GivesEachRecordItsOwnCopyOfADefault()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserSchema);

        var first = GenericRecord.CreateWithDefaults(schema);
        var second = GenericRecord.CreateWithDefaults(schema);

        await Assert.That(first["tags"].AsArray()).IsNotSameReferenceAs(second["tags"].AsArray());
        await Assert.That(first["limits"].AsMap()).IsNotSameReferenceAs(second["limits"].AsMap());
        await Assert.That(first["address"].AsRecord()).IsNotSameReferenceAs(second["address"].AsRecord());
        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task Builder_UsesTheValuesSet_AndTheDefaultsOfTheRest()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserSchema);

        var user = new GenericRecordBuilder(schema)
            .Set("id", 7L)
            .Set("email", "ada@example.com")
            .Set(schema.GetField("note").Position, AvroValue.Null)
            .Build();

        await Assert.That(user["id"].AsInt64()).IsEqualTo(7L);
        await Assert.That(user["email"].AsString()).IsEqualTo("ada@example.com");
        await Assert.That(user["name"].AsString()).IsEqualTo("anonymous");
        await Assert.That(user["address"].AsRecord()["city"].AsString()).IsEqualTo("Szeged");
        await Assert.That(GenericDatumWriter.Create(schema).TryValidate(user, out _)).IsTrue();
    }

    [Test]
    public async Task Builder_RejectsAFieldWithNoValueAndNoDefault_ByName()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserSchema);
        var builder = new GenericRecordBuilder(schema).Set("id", 7L);

        var ex = Assert.Throws<AvroException>(() => builder.Build());

        await Assert.That(ex.Message).Contains("'note'");
        await Assert.That(ex.Message).Contains("app.User");
        await Assert.That(builder.IsSet("id")).IsTrue();
        await Assert.That(builder.IsSet("note")).IsFalse();
    }

    [Test]
    public async Task Builder_BuildsNewRecords_WithTheirOwnDefaults()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserSchema);
        var builder = new GenericRecordBuilder(schema).Set("id", 1L).Set("note", AvroValue.Null);

        var first = builder.Build();
        var second = builder.Build();

        await Assert.That(first).IsNotSameReferenceAs(second);
        await Assert.That(first["tags"].AsArray()).IsNotSameReferenceAs(second["tags"].AsArray());
    }

    [Test]
    public async Task Builder_RejectsUnknownFields()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserSchema);
        var builder = new GenericRecordBuilder(schema);

        Assert.Throws<KeyNotFoundException>(() => builder.Set("missing", 1));
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => builder.Set(schema.Fields.Count, 1));

        await Assert.That(ex.ParamName).IsEqualTo("position");
    }

    [Test]
    public async Task Validate_AcceptsAValueTheWriterAccepts()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserSchema);
        var user = new GenericRecordBuilder(schema).Set("id", 1L).Set("note", "hi").Build();
        var writer = GenericDatumWriter.Create(schema);

        writer.Validate(user);

        await Assert.That(writer.TryValidate(user, out var error)).IsTrue();
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Validate_NamesTheFieldOfTheFirstProblem()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserSchema);
        var user = new GenericRecordBuilder(schema).Set("id", 1L).Set("note", AvroValue.Null).Build();
        user["address"].AsRecord()["city"] = 42;
        var writer = GenericDatumWriter.Create(schema);

        var ex = Assert.Throws<AvroException>(() => writer.Validate(user));
        var valid = writer.TryValidate(user, out var error);

        await Assert.That(ex.Message).Contains("app.Address.city");
        await Assert.That(valid).IsFalse();
        await Assert.That(error).IsEqualTo(ex.Message);
    }

    [Test]
    public async Task Validate_RejectsAMissingRequiredValue()
    {
        var schema = (RecordSchema)AvroSchema.Parse(UserSchema);
        var user = GenericRecord.CreateWithDefaults(schema);

        var valid = GenericDatumWriter.Create(schema).TryValidate(user, out var error);

        await Assert.That(valid).IsFalse();
        await Assert.That(error).Contains("app.User.id");
    }

    [Test]
    public async Task DeepCopy_CopiesEverythingThatCanChange()
    {
        var schema = (RecordSchema)AvroSchema.Parse("""
            {"type":"record","name":"Box","fields":[
              {"name":"inner","type":{"type":"record","name":"Inner","fields":[{"name":"n","type":"int"}]}},
              {"name":"items","type":{"type":"array","items":"string"}},
              {"name":"longs","type":{"type":"array","items":"long"}},
              {"name":"map","type":{"type":"map","values":"int"}},
              {"name":"data","type":"bytes"},
              {"name":"id","type":{"type":"fixed","name":"Id","size":2}},
              {"name":"name","type":"string"}]}
            """);
        var inner = new GenericRecord((RecordSchema)schema.GetField("inner").Schema) { ["n"] = 1 };
        var items = new List<AvroValue> { "a" };
        var map = new Dictionary<string, AvroValue>(StringComparer.Ordinal) { ["k"] = 1 };
        var data = new byte[] { 1, 2 };
        var id = new GenericFixed((FixedSchema)schema.GetField("id").Schema, [3, 4]);
        var original = new GenericRecord(schema)
        {
            ["inner"] = inner,
            ["items"] = AvroValue.FromArray(items),
            ["longs"] = AvroValue.FromInt64Array(new long[] { 5, 6 }),
            ["map"] = AvroValue.FromMap(map),
            ["data"] = data,
            ["id"] = id,
            ["name"] = "box",
        };

        var copy = ((AvroValue)original).DeepCopy();

        await Assert.That(copy).IsEqualTo((AvroValue)original);
        var record = copy.AsRecord();
        await Assert.That(record).IsNotSameReferenceAs(original);
        await Assert.That(record["inner"].AsRecord()).IsNotSameReferenceAs(inner);
        await Assert.That(record["items"].AsArray()).IsNotSameReferenceAs(items);
        await Assert.That(record["map"].AsMap()).IsNotSameReferenceAs(map);
        await Assert.That(record["data"].AsBytes()).IsNotSameReferenceAs(data);
        await Assert.That(record["id"].AsFixed()).IsNotSameReferenceAs(id);
        await Assert.That(record["longs"].TryGetInt64Array(out var longs)).IsTrue();
        await Assert.That(longs.ToArray()).IsEquivalentTo(new long[] { 5, 6 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        // Changing the copy leaves the original alone.
        record["inner"].AsRecord()["n"] = 2;
        record["data"].AsBytes()[0] = 9;
        await Assert.That(inner["n"].AsInt32()).IsEqualTo(1);
        await Assert.That(data[0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task DeepCopy_RejectsAValueThatContainsItself()
    {
        var items = new List<AvroValue>();
        items.Add(AvroValue.FromArray(items));

        var ex = Assert.Throws<AvroException>(() => AvroValue.FromArray(items).DeepCopy());

        await Assert.That(ex.Message).Contains("contain itself");
    }

    [Test]
    public async Task DeepCopy_OfAnImmutableValue_IsTheValue()
    {
        AvroValue text = "same";

        var copy = text.DeepCopy();

        await Assert.That(copy.AsString()).IsSameReferenceAs(text.AsString());
        await Assert.That(((AvroValue)42L).DeepCopy()).IsEqualTo((AvroValue)42L);
        await Assert.That(AvroValue.Null.DeepCopy().IsNull).IsTrue();
    }
}
