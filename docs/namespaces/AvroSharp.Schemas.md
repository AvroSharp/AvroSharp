---
uid: AvroSharp.Schemas
summary: *content
---
The schema model: [`AvroSchema`](xref:AvroSharp.Schemas.AvroSchema) and its subclasses, such as [`RecordSchema`](xref:AvroSharp.Schemas.RecordSchema), [`UnionSchema`](xref:AvroSharp.Schemas.UnionSchema) and [`EnumSchema`](xref:AvroSharp.Schemas.EnumSchema), and the logical types ([`AvroLogicalType`](xref:AvroSharp.Schemas.AvroLogicalType)).

- Parse schema JSON with [`AvroSchema.Parse`](xref:AvroSharp.Schemas.AvroSchema.Parse%2A), or with an [`AvroSchemaParser`](xref:AvroSharp.Schemas.AvroSchemaParser) when later schemas refer to named types from earlier ones. [`AvroSchemaParseOptions`](xref:AvroSharp.Schemas.AvroSchemaParseOptions) sets the limits and the validation.
- [`AvroSchema.CanonicalForm`](xref:AvroSharp.Schemas.AvroSchema.CanonicalForm%2A) gives the Parsing Canonical Form, and [`SchemaFingerprint`](xref:AvroSharp.Schemas.SchemaFingerprint) its CRC-64-AVRO, MD5 and SHA-256 fingerprints.
- [`AvroSchemaCompatibility`](xref:AvroSharp.Schemas.AvroSchemaCompatibility) checks whether data written with one schema can be read with another, with every [issue](xref:AvroSharp.Schemas.AvroCompatibilityIssue) and its path, or a new version against earlier ones at a schema registry's [level](xref:AvroSharp.Schemas.AvroCompatibilityLevel).
- [`AvroNames`](xref:AvroSharp.Schemas.AvroNames) holds the validation rules for names, namespaces and enum symbols.

The other namespaces take their schemas from here: <xref:AvroSharp.Generic>, <xref:AvroSharp.Containers>, <xref:AvroSharp.Messages>, <xref:AvroSharp.Streams> and <xref:AvroSharp.CodeGen>.
