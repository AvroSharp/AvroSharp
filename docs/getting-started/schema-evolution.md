# Schema evolution

Schemas change: fields are added, removed, renamed and widened. This page reads data written with one version of a `Customer` schema as another version, one change per section, with the generic model. The rules are the Avro specification's [schema resolution](https://avro.apache.org/docs/1.12.0/specification/#schema-resolution).

## The idea: the writer's schema and the reader's schema

Avro's binary encoding holds no field names or types, so data can only be decoded with the schema it was written with, the writer's schema. The reader's schema is the version the application wants. [`GenericDatumReader.Create(writerSchema, readerSchema)`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericDatumReader.Create.html) returns a reader that decodes with the first and returns records of the second. Here version 1 is written, and read as a version that lists the same fields in another order:

```csharp
var v1 = (RecordSchema)AvroSchema.Parse("""
    {"type":"record","name":"Customer","namespace":"shop","fields":[
      {"name":"id","type":"int"},
      {"name":"name","type":"string"},
      {"name":"phone","type":"string"}]}
    """);
var ada = new GenericRecord(v1) { ["id"] = 42, ["name"] = "Ada Lovelace", ["phone"] = "+44 20 7946 0000" };
byte[] v1Bytes = GenericDatumWriter.Create(v1).WriteToArray(ada);
```

```csharp
GenericRecord read = GenericDatumReader.Create(writerSchema: v1, readerSchema: reordered).Read(v1Bytes).AsRecord();
```

Fields are matched by name, not by position. With the default options, resolving readers are cached per pair of schemas, as plain readers are per schema.

## Adding a field with a default

A reader field that the writer's schema lacks takes its [default value](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.RecordField.DefaultValue.html). Version 2 adds `loyalty_points` with a default of 0, and reading version 1 data gives 0:

```csharp
GenericRecord withDefault = GenericDatumReader.Create(v1, withPoints).Read(v1Bytes).AsRecord();
Console.WriteLine($"Added field: loyalty_points = {withDefault["loyalty_points"].AsInt32()}");
```

Without a default there is no value to give the field, so `Create` throws an [`AvroSchemaException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroSchemaException.html). Its message is "At $: the reader's field 'shop.Customer.loyalty_points' is not in the writer's schema and has no default value.":

```csharp
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
```

So a field added to a schema needs a default if data written before it must stay readable.

## Removing a field

A writer field that the reader's schema lacks is skipped. The record has only the reader's fields, so [`TryGetValue`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Generic.GenericRecord.TryGetValue.html) finds no `phone`:

```csharp
GenericRecord removed = GenericDatumReader.Create(v1, withoutPhone).Read(v1Bytes).AsRecord();
Console.WriteLine($"Removed field: phone present = {removed.TryGetValue("phone", out _)}");
```

The reverse, data written without `phone` read by version 1, works only if version 1 gave `phone` a default.

## Type promotion

A value can be read as a wider type: `int` as `long`, `float` or `double`; `long` as `float` or `double`; `float` as `double`. `string` and `bytes` convert to each other, as UTF-8. Here `id`, `balance` and `notes` are widened:

```csharp
var grace = new GenericRecord(narrow) { ["id"] = 7, ["balance"] = 12.5f, ["notes"] = "prefers email" };
GenericRecord promoted = GenericDatumReader.Create(narrow, wide).Read(GenericDatumWriter.Create(narrow).WriteToArray(grace)).AsRecord();
Console.WriteLine($"Promoted: id {promoted["id"].Kind}, balance {promoted["balance"].Kind}, notes {promoted["notes"].Kind}");
```

It prints `id Long, balance Double, notes Bytes`. No narrowing is allowed: `long` data can't be read as `int`, as the last section shows.

Logical types don't take part, as in Java: a `date` reads as an `int` of any logical type, and a decimal as the reader's precision and scale, so a changed scale reads every value as another number. [Every reason, with where it is](#every-reason-with-where-it-is) shows the check that warns about it.

## Renaming through aliases

A field is renamed by giving the reader's field the old name in its [`aliases`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.RecordField.Aliases.html). A record, enum or fixed type is renamed the same way, with the old full name in the named type's [`aliases`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.NamedSchema.Aliases.html). Here `shop.Customer` becomes `crm.Client`, and `name` becomes `full_name`:

```csharp
var renamed = AvroSchema.Parse("""
    {"type":"record","name":"Client","namespace":"crm","aliases":["shop.Customer"],"fields":[
      {"name":"id","type":"int"},
      {"name":"full_name","type":"string","aliases":["name"]},
      {"name":"phone","type":"string"}]}
    """);
GenericRecord client = GenericDatumReader.Create(v1, renamed).Read(v1Bytes).AsRecord();
```

Named types also match by their unqualified name, so a change of namespace alone needs no alias.

## Enum evolution

Enum symbols are matched by name, so reordering them is safe. A symbol the reader's enum lacks takes that enum's [`default`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.EnumSchema.DefaultSymbol.html). A newer writer added `PLATINUM`, and an older reader with `"default":"UNKNOWN"` reads it as `UNKNOWN`:

```csharp
var tier = (EnumSchema)newerTiers.GetField("tier").Schema;
var platinum = new GenericRecord(newerTiers) { ["id"] = 42, ["tier"] = AvroValue.FromEnum(tier, "PLATINUM") };
GenericRecord tiered = GenericDatumReader.Create(newerTiers, olderTiers).Read(GenericDatumWriter.Create(newerTiers).WriteToArray(platinum)).AsRecord();
Console.WriteLine($"Enum: PLATINUM read as {tiered["tier"].AsEnumSymbol()}");
```

An enum that may gain symbols should have a default from its first version, so that older readers keep working.

## Unions: making a field nullable

A value written as a plain type is read as the first matching branch of a reader [union](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.UnionSchema.html), so `phone` can become `["null","string"]`. A new nullable field, `email`, takes its default of `null` when the writer lacks it:

```csharp
GenericRecord nullable = GenericDatumReader.Create(v1, nullablePhone).Read(v1Bytes).AsRecord();
Console.WriteLine($"Nullable: phone = {nullable["phone"].AsString()}, email is null = {nullable["email"].IsNull}");
```

The reverse, `["null","string"]` data read as `string`, resolves too: strings are read, and a null fails when it is read.

## Checking compatibility up front

`Create` resolves the whole pair of schemas, so a pair that can't be resolved throws an [`AvroSchemaException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroSchemaException.html) there, not at the first read. Creating the reader when the application starts checks a new schema against the versions it must read. Here `long` data can't be read as `int`, and the message is "At $.id: data written as "long" cannot be read as "int": the types differ and no promotion applies.":

```csharp
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
```

Two mismatches are left until a value needs them, because the data may never contain one, as in the Java implementation: a writer union branch with no counterpart in the reader's schema, and an enum symbol the reader lacks when the reader's enum has no default. Reading such a value throws an [`AvroDataException`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.AvroDataException.html):

```csharp
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
```

## Every reason, with where it is

[`AvroSchemaCompatibility.Check`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroSchemaCompatibility.html) applies the same rules, but collects every problem instead of stopping at the first. Each [issue](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroCompatibilityIssue.html) has a kind, a message, and a path into the data, such as `$.items[].sku`, or `$.payment[1:Card].number` for a writer union's branch.

The mismatches that `Create` defers come back as [`Partial`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroCompatibilityVerdict.html), naming the symbols or branches that can't be read. `IsCompatible` is true only for `Compatible`, as Java's `SchemaCompatibility` and schema registries decide:

```csharp
AvroCompatibilityResult check = AvroSchemaCompatibility.Check(writerSchema: newerTiers, readerSchema: noDefaultTiers);
Console.WriteLine($"Check: {check.Verdict}, {check.Incompatibilities[0]}");
```

The result also has [warnings](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroCompatibilityKind.html) for differences that the specification allows but that can change the values read:
- a decimal whose scale changes;
- a `date` read as `time-millis`;
- `long` to `double`, which rounds large values;
- a name matched without its namespace;
- symbols read as the enum's default.

[`AvroCompatibilityOptions`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroCompatibilityOptions.html) has two settings:
- `WarningsAsErrors` makes warnings fail the check (the verdict stays what it is);
- `AllowPartial` lets `Partial` pass.

A new version can be checked against the earlier ones, oldest first, at a schema registry's [level](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Schemas.AvroCompatibilityLevel.html). The levels are Backward, Forward and Full, each with a transitive version, as Confluent Schema Registry defines them:

```csharp
AvroCompatibilityReport report = AvroSchemaCompatibility.CheckVersions(withPoints, [v1, reordered], AvroCompatibilityLevel.BackwardTransitive);
Console.WriteLine($"Backward transitive: {report.Verdict}");
```

`result.ThrowIfIncompatible()` makes either check a one-line test in CI. From the command line, the same check is [`avrosharp schema compat`](../cli.md#schema-compat).

## Where the writer's schema comes from

- **Generated types** resolve the same way: `FromAvroBytes(bytes, writerSchema)` reads data of another version of the type's schema. See the [GeneratedTypes sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/GeneratedTypes).
- **Container files** carry their writer's schema in the header, so [`AvroFileReader.OpenGeneric`](https://avrosharp.github.io/AvroSharp/docs/api/AvroSharp.Containers.AvroFileReader.OpenGeneric.html) needs only the reader's schema. See [Container files](containers.md).
- **Single-object messages** carry the writer schema's fingerprint, and a schema resolver finds the schema by it; **registry messages** carry a schema ID that an ID resolver looks up. See [single-object encoding](https://github.com/AvroSharp/AvroSharp#single-object-encoding) and [schema registries](https://github.com/AvroSharp/AvroSharp#schema-registries) in the README.

The whole program is the [SchemaEvolution sample](https://github.com/AvroSharp/AvroSharp/tree/main/samples/SchemaEvolution).

## Next

- [Getting started](index.md), [Container files](containers.md), [JSON](json.md) and [Logical types](logical-types.md).
- [Code generation](../code-generation.md#schema-evolution): schema evolution with generated types.
