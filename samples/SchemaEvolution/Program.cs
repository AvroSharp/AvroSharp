// Schema evolution with the generic model: data written with one version of a Customer schema, read as another.
// Each section makes one change to the schema and checks what the reader gets.
using System;
using System.Linq;
using System.Text;
using AvroSharp;
using AvroSharp.Generic;
using AvroSharp.Schemas;

var ok = true;

// The idea: the writer's schema decodes the bytes, and the reader's schema shapes the result.
var v1 = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"name","type":"string"},
      {"name":"phone","type":"string"}]}
    """);
var ada = new GenericRecord(v1) { ["id"] = 42, ["name"] = "Ada Lovelace", ["phone"] = "+44 20 7946 0000" };
byte[] v1Bytes = GenericDatumWriter.Create(v1).WriteToArray(ada);

var reordered = AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"phone","type":"string"},
      {"name":"name","type":"string"},
      {"name":"id","type":"int"}]}
    """);
GenericRecord read = GenericDatumReader.Create(writerSchema: v1, readerSchema: reordered).Read(v1Bytes).AsRecord();
Console.WriteLine($"Reordered: {read["id"].AsInt32()} {read["name"].AsString()}");
ok &= read["id"].AsInt32() == 42 && string.Equals(read[0].AsString(), "+44 20 7946 0000", StringComparison.Ordinal);

// Adding a field with a default: data written before the field existed reads with the default.
var withPoints = AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"name","type":"string"},
      {"name":"phone","type":"string"},
      {"name":"loyalty_points","type":"int","default":0}]}
    """);
GenericRecord withDefault = GenericDatumReader.Create(v1, withPoints).Read(v1Bytes).AsRecord();
Console.WriteLine($"Added field: loyalty_points = {withDefault["loyalty_points"].AsInt32()}");
ok &= withDefault["loyalty_points"].AsInt32() == 0;

// Without a default, the old data has no value for the field, and the pair is rejected.
var withoutDefault = AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"name","type":"string"},
      {"name":"phone","type":"string"},
      {"name":"loyalty_points","type":"int"}]}
    """);
try
{
    GenericDatumReader.Create(v1, withoutDefault);
    ok = false;
}
catch (AvroSchemaException ex)
{
    Console.WriteLine($"No default: {ex.Message}");
    ok &= ex.Message.Contains("has no default value", StringComparison.Ordinal);
}

// Removing a field: the reader skips the writer's field it doesn't have.
var withoutPhone = AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"name","type":"string"}]}
    """);
GenericRecord removed = GenericDatumReader.Create(v1, withoutPhone).Read(v1Bytes).AsRecord();
Console.WriteLine($"Removed field: phone present = {removed.TryGetValue("phone", out _)}");
ok &= !removed.TryGetValue("phone", out _) && string.Equals(removed["name"].AsString(), "Ada Lovelace", StringComparison.Ordinal);

// Type promotion: int to long, float to double, and string to bytes.
var narrow = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"balance","type":"float"},
      {"name":"notes","type":"string"}]}
    """);
var wide = AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"long"},
      {"name":"balance","type":"double"},
      {"name":"notes","type":"bytes"}]}
    """);
var grace = new GenericRecord(narrow) { ["id"] = 7, ["balance"] = 12.5f, ["notes"] = "prefers email" };
GenericRecord promoted = GenericDatumReader.Create(narrow, wide).Read(GenericDatumWriter.Create(narrow).WriteToArray(grace)).AsRecord();
Console.WriteLine($"Promoted: id {promoted["id"].Kind}, balance {promoted["balance"].Kind}, notes {promoted["notes"].Kind}");
ok &= promoted["id"].AsInt64() == 7L && promoted["balance"].AsDouble() == 12.5
    && promoted["notes"].AsBytes().SequenceEqual(Encoding.UTF8.GetBytes("prefers email"));

// Renaming through aliases: the reader's field and record list the writer's old names.
var renamed = AvroSchema.Parse("""
    {"type":"record","name":"Client","namespace":"crm","aliases":["shop.Customer"],"fields":[
      {"name":"id","type":"int"},
      {"name":"full_name","type":"string","aliases":["name"]},
      {"name":"phone","type":"string"}]}
    """);
GenericRecord client = GenericDatumReader.Create(v1, renamed).Read(v1Bytes).AsRecord();
Console.WriteLine($"Renamed: {client.Schema.FullName}, full_name = {client["full_name"].AsString()}");
ok &= string.Equals(client.Schema.FullName, "crm.Client", StringComparison.Ordinal)
    && string.Equals(client["full_name"].AsString(), "Ada Lovelace", StringComparison.Ordinal);

// Enum evolution: a symbol the reader's enum lacks takes that enum's default.
var newerTiers = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"tier","type":{"type":"enum","name":"Tier","symbols":["BRONZE","SILVER","GOLD","PLATINUM"]}}]}
    """);
var olderTiers = AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"tier","type":{"type":"enum","name":"Tier","symbols":["UNKNOWN","BRONZE","SILVER","GOLD"],"default":"UNKNOWN"}}]}
    """);
var tier = (EnumSchema)newerTiers.GetField("tier").Schema;
var platinum = new GenericRecord(newerTiers) { ["id"] = 42, ["tier"] = AvroValue.FromEnum(tier, "PLATINUM") };
GenericRecord tiered = GenericDatumReader.Create(newerTiers, olderTiers).Read(GenericDatumWriter.Create(newerTiers).WriteToArray(platinum)).AsRecord();
Console.WriteLine($"Enum: PLATINUM read as {tiered["tier"].AsEnumSymbol()}");
ok &= string.Equals(tiered["tier"].AsEnumSymbol(), "UNKNOWN", StringComparison.Ordinal);

// Unions: making a field nullable. A string reads as the union's string branch.
var nullablePhone = AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"name","type":"string"},
      {"name":"phone","type":["null","string"],"default":null},
      {"name":"email","type":["null","string"],"default":null}]}
    """);
GenericRecord nullable = GenericDatumReader.Create(v1, nullablePhone).Read(v1Bytes).AsRecord();
Console.WriteLine($"Nullable: phone = {nullable["phone"].AsString()}, email is null = {nullable["email"].IsNull}");
ok &= string.Equals(nullable["phone"].AsString(), "+44 20 7946 0000", StringComparison.Ordinal) && nullable["email"].IsNull;

// Checking compatibility up front: Create resolves the whole pair, so a bad one fails before any data is read.
var shortId = AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"}]}
    """);
try
{
    GenericDatumReader.Create(wide, shortId);
    ok = false;
}
catch (AvroSchemaException ex)
{
    Console.WriteLine($"Incompatible: {ex.Message}");
    ok &= ex.Message.Contains("cannot be read as", StringComparison.Ordinal);
}

// Except for a value the data may never hold: an enum symbol the reader lacks, when its enum has no default.
var noDefaultTiers = AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"tier","type":{"type":"enum","name":"Tier","symbols":["BRONZE","SILVER","GOLD"]}}]}
    """);
var strict = GenericDatumReader.Create(newerTiers, noDefaultTiers);
var gold = new GenericRecord(newerTiers) { ["id"] = 7, ["tier"] = AvroValue.FromEnum(tier, "GOLD") };
ok &= string.Equals(strict.Read(GenericDatumWriter.Create(newerTiers).WriteToArray(gold)).AsRecord()["tier"].AsEnumSymbol(), "GOLD", StringComparison.Ordinal);
try
{
    strict.Read(GenericDatumWriter.Create(newerTiers).WriteToArray(platinum));
    ok = false;
}
catch (AvroDataException ex)
{
    Console.WriteLine($"Deferred: {ex.Message}");
    ok &= ex.Message.Contains("'PLATINUM' is not in the reader's enum", StringComparison.Ordinal);
}

// Every reason at once, with where it is: AvroSchemaCompatibility, which also calls the deferred mismatches partial.
AvroCompatibilityResult check = AvroSchemaCompatibility.Check(writerSchema: newerTiers, readerSchema: noDefaultTiers);
Console.WriteLine($"Check: {check.Verdict}, {check.Incompatibilities[0]}");
ok &= check.Verdict == AvroCompatibilityVerdict.Partial && !check.IsCompatible && string.Equals(check.Incompatibilities[0].Path, "$.tier", StringComparison.Ordinal);

// A new version against the earlier ones, at a schema registry's level.
AvroCompatibilityReport report = AvroSchemaCompatibility.CheckVersions(withPoints, [v1, reordered], AvroCompatibilityLevel.BackwardTransitive);
Console.WriteLine($"Backward transitive: {report.Verdict}");
ok &= report.IsCompatible;

Console.WriteLine(ok ? "OK" : "FAILED");
return ok ? 0 : 1;
