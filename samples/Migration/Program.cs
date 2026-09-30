// Code written for Apache.Avro, and the same with AvroSharp: the examples of docs/migrating-from-apache-avro.md. Each
// part writes with one library and reads with the other, so both stay interchangeable on the wire while code moves.
using System;
using System.IO;
using System.Linq;
using Avro.Specific;
using AvroSharp;
using AvroSharp.Containers;
using AvroSharp.Generic;
using AvroSharp.Schemas;

const string UserJson = """
    {"type":"record","name":"User","namespace":"generic","fields":[
      {"name":"id","type":"long"},
      {"name":"name","type":"string"},
      {"name":"email","type":["null","string"],"default":null},
      {"name":"status","type":{"type":"enum","name":"Status","symbols":["ACTIVE","SUSPENDED"]}}]}
    """;
var ok = true;

// --- Schemas ---
var apacheSchema = (Avro.RecordSchema)Avro.Schema.Parse(UserJson);
long apacheFingerprint = Avro.SchemaNormalization.ParsingFingerprint64(apacheSchema);

var schema = (RecordSchema)AvroSchema.Parse(UserJson);
long fingerprint = schema.Fingerprint64;
string canonical = schema.CanonicalForm;
ok &= fingerprint == apacheFingerprint;
Console.WriteLine($"Schemas: fingerprint {fingerprint:X16}, the same in both libraries: {fingerprint == apacheFingerprint}");

// --- The generic model: writing ---
var apacheRecord = new Avro.Generic.GenericRecord(apacheSchema);
apacheRecord.Add("id", 42L);
apacheRecord.Add("name", "Ada");
apacheRecord.Add("email", null);
apacheRecord.Add("status", new Avro.Generic.GenericEnum((Avro.EnumSchema)apacheSchema["status"].Schema, "ACTIVE"));
using var apacheOutput = new MemoryStream();
new Avro.Generic.GenericDatumWriter<Avro.Generic.GenericRecord>(apacheSchema).Write(apacheRecord, new Avro.IO.BinaryEncoder(apacheOutput));
byte[] apacheBytes = apacheOutput.ToArray();

var record = new GenericRecord(schema)
{
    ["id"] = 42L,
    ["name"] = "Ada",
    ["email"] = AvroValue.Null,
    ["status"] = AvroValue.FromEnum((EnumSchema)schema.GetField("status").Schema, "ACTIVE"),
};
byte[] bytes = GenericDatumWriter.Create(schema).WriteToArray(record);

ok &= bytes.AsSpan().SequenceEqual(apacheBytes);
Console.WriteLine($"Generic write: {bytes.Length} bytes, the same in both libraries: {bytes.AsSpan().SequenceEqual(apacheBytes)}");

// --- The generic model: reading, each library the other's bytes ---
var apacheRead = new Avro.Generic.GenericDatumReader<Avro.Generic.GenericRecord>(apacheSchema, apacheSchema)
    .Read(null!, new Avro.IO.BinaryDecoder(new MemoryStream(bytes)));
var apacheName = (string)apacheRead["name"];
var apacheStatus = ((Avro.Generic.GenericEnum)apacheRead["status"]).Value;

var read = GenericDatumReader.Create(schema).Read(apacheBytes).AsRecord();
string name = read["name"].AsString();
string status = read["status"].AsEnumSymbol();
bool noEmail = read["email"].IsNull;

ok &= string.Equals(apacheName, name, StringComparison.Ordinal) && string.Equals(apacheStatus, status, StringComparison.Ordinal) && noEmail;
Console.WriteLine($"Generic read: {name}, {status}, no email: {noEmail}");

// --- Schema resolution: data written with one version, read as another ---
var newer = AvroSchema.Parse(UserJson.Replace(
    """{"name":"email","type":["null","string"],"default":null},""",
    """{"name":"email","type":["null","string"],"default":null},{"name":"tier","type":"int","default":1},""",
    StringComparison.Ordinal));
var resolved = GenericDatumReader.Create(schema, newer).Read(apacheBytes).AsRecord();
int tier = resolved["tier"].AsInt32();

ok &= tier == 1;
Console.WriteLine($"Resolution: read as the newer version, tier {tier} from its default");

// --- Container files ---
var apacheFile = new MemoryStream();
using (var apacheWriter = Avro.File.DataFileWriter<Avro.Generic.GenericRecord>.OpenWriter(
    new Avro.Generic.GenericDatumWriter<Avro.Generic.GenericRecord>(apacheSchema), apacheFile, Avro.File.Codec.CreateCodec(Avro.File.Codec.Type.Deflate), leaveOpen: true))
{
    apacheWriter.Append(apacheRecord);
}

apacheFile.Position = 0;
using (var fileReader = AvroFileReader.OpenGeneric(apacheFile))
{
    ok &= string.Equals(fileReader.Codec.Name, AvroCodecNames.Deflate, StringComparison.Ordinal) && fileReader.ReadAll().Single().Equals((AvroValue)record);
}

var file = new MemoryStream();
using (var fileWriter = AvroFileWriter.CreateGeneric(file, schema, new AvroFileWriterOptions { Codec = AvroCodec.Deflate, LeaveOpen = true }))
{
    fileWriter.Write(record);
}

file.Position = 0;
using (var apacheReader = Avro.File.DataFileReader<Avro.Generic.GenericRecord>.OpenReader(file))
{
    ok &= apacheReader.HasNext() && string.Equals((string)apacheReader.Next()["name"], "Ada", StringComparison.Ordinal);
}

Console.WriteLine("Container files: each library reads the other's deflate file");

// --- Generated types, in the Apache.Avro compatibility mode (Schemas/user.avsc) ---
var user = new migration.User { id = 42, name = "Ada", email = null, status = migration.Status.ACTIVE, joined = new DateTime(2020, 1, 15) };

using var specificOutput = new MemoryStream();
new SpecificDatumWriter<migration.User>(migration.User._SCHEMA).Write(user, new Avro.IO.BinaryEncoder(specificOutput));

byte[] userBytes = user.ToAvroBytes();
var sameUser = migration.User.FromAvroBytes(specificOutput.ToArray());

var apacheUser = new SpecificDatumReader<migration.User>(migration.User._SCHEMA, migration.User._SCHEMA)
    .Read(null!, new Avro.IO.BinaryDecoder(new MemoryStream(userBytes)));

var usersFile = new MemoryStream();
using (var users = AvroFileWriter.Create<migration.User>(usersFile, new AvroFileWriterOptions { LeaveOpen = true }))
{
    users.Write(user);
}

usersFile.Position = 0;
using (var usersReader = AvroFileReader.Open<migration.User>(usersFile))
{
    ok &= usersReader.ReadAll().Single().joined == user.joined;
}

ok &= userBytes.AsSpan().SequenceEqual(specificOutput.ToArray())
    && string.Equals(sameUser.name, "Ada", StringComparison.Ordinal)
    && apacheUser.joined == user.joined && apacheUser.status == migration.Status.ACTIVE;
Console.WriteLine($"Generated types: {userBytes.Length} bytes, the same as Apache's SpecificDatumWriter: {userBytes.AsSpan().SequenceEqual(specificOutput.ToArray())}");

// --- Exceptions ---
try
{
    AvroSchema.Parse("""{"type":"record","name":"Broken"}""");
    ok = false;
}
catch (AvroSchemaException ex)
{
    Console.WriteLine($"Exceptions: an invalid schema is an AvroSchemaException: {ex.Message}");
}

try
{
    GenericDatumReader.Create(schema).Read([0x54]);
    ok = false;
}
catch (AvroDataException ex)
{
    Console.WriteLine($"Exceptions: malformed data is an AvroDataException: {ex.Message}");
}

ok &= canonical.Length > 0;
Console.WriteLine(ok ? "OK" : "FAILED");
return ok ? 0 : 1;
