// Schemas and the generic data model: write and read a record, read it as a newer schema version, and use JSON.
using System;
using AvroSharp.Generic;
using AvroSharp.Schemas;

var userV1 = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"User","namespace":"example","fields":[
      {"name":"id","type":"int"},
      {"name":"name","type":"string"},
      {"name":"email","type":["null","string"],"default":null}]}
    """);

// Binary encoding. Readers and writers are cached per schema and are thread-safe.
var ada = new GenericRecord(userV1) { ["id"] = 1, ["name"] = "Ada", ["email"] = "ada@example.com" };
byte[] bytes = GenericDatumWriter.Create(userV1).WriteToArray(ada);
GenericRecord copy = GenericDatumReader.Create(userV1).Read(bytes).AsRecord();
Console.WriteLine($"Binary: {bytes.Length} bytes, name = {copy["name"].AsString()}, email = {copy["email"].AsString()}");

// Schema evolution: version 2 widens id to long, drops email and adds a field with a default.
var userV2 = AvroSchema.Parse("""
    {"type":"record","name":"User","namespace":"example","fields":[
      {"name":"id","type":"long"},
      {"name":"name","type":"string"},
      {"name":"active","type":"boolean","default":true}]}
    """);
GenericRecord upgraded = GenericDatumReader.Create(writerSchema: userV1, readerSchema: userV2).Read(bytes).AsRecord();
Console.WriteLine($"Read as v2: id = {upgraded["id"].AsInt64()}, active = {upgraded["active"].AsBoolean()}");

// JSON encoding, as the specification defines it (union values are wrapped in their branch's name).
string json = GenericDatumJsonWriter.Create(userV1).WriteToString(ada);
AvroValue fromJson = GenericDatumJsonReader.Create(userV1).Read(json);
Console.WriteLine($"JSON: {json}");

// Canonical form and fingerprint, for comparing schemas and for schema stores.
Console.WriteLine($"Fingerprint: 0x{userV1.Fingerprint64:X16}");

var ok = copy.Equals(ada)
    && upgraded["id"].AsInt64() == 1 && upgraded["active"].AsBoolean()
    && fromJson.Equals((AvroValue)ada)
    && string.Equals(json, """{"id":1,"name":"Ada","email":{"string":"ada@example.com"}}""", StringComparison.Ordinal);
Console.WriteLine(ok ? "OK" : "FAILED");
return ok ? 0 : 1;
