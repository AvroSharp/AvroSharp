using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using AvroSharp.Generic;
using AvroSharp.Schemas;
using Confluent.SchemaRegistry;

namespace AvroSharp.Confluent;

/// <summary>
/// Field rules, such as field-level encryption (<c>ENCRYPT</c>) and <c>CEL_FIELD</c>: the field transformer Confluent's
/// field rule executors call, which walks a value of the rules' schema as Confluent's <c>AvroUtils.Transform</c> does.
/// Each record field is entered with its tags (the field's <c>confluent:tags</c>, and the schema metadata's, which
/// <see cref="RuleContext.EnterField"/> adds), and each value whose tags the rule's tags meet is transformed.
/// </summary>
/// <remarks>
/// The rules' message is the value as the other rules see it: the type's value, or Apache.Avro's generic model for CEL.
/// It is converted to an <see cref="AvroValue"/> of the rules' schema for the walk, and back, so rules of both kinds can
/// share a rule set. A leaf is given to the transform as .NET's value: a <see cref="string"/>, a <c>byte[]</c> (bytes and
/// fixed), a number, a <see cref="bool"/>, the symbol of an enum, or <see langword="null"/>.
/// </remarks>
internal static class FieldTransforms
{
    private static readonly ConditionalWeakTable<AvroSchema, GenericDatumReader> Readers = new();
    private static readonly ConditionalWeakTable<AvroSchema, GenericDatumWriter> Writers = new();
    private static readonly ConditionalWeakTable<AvroSchema, FieldTransformer> ApacheTransformers = new();

    /// <summary>The transformer for messages that <paramref name="toAvro"/> and <paramref name="fromAvro"/> convert.</summary>
    public static FieldTransformer For(AvroSchema schema, Func<object, AvroValue> toAvro, Func<AvroValue, object> fromAvro) =>
        async (context, transform, message) => message is null
            ? message!
            : fromAvro(await WalkAsync(context, schema, toAvro(message), transform).ConfigureAwait(false));

    /// <summary>The transformer for messages in Apache.Avro's generic model, as CEL rules get them.</summary>
    public static FieldTransformer ForApache(AvroSchema schema) =>
        ApacheTransformers.GetValue(schema, s => For(
            s,
            message => Decode(s, ApacheAvroGeneric.Encode(s, message).Span),
            value => ApacheAvroGeneric.Decode(s, s, Encode(s, value))!));

    /// <summary>Reads a value of <paramref name="schema"/>.</summary>
    public static AvroValue Decode(AvroSchema schema, ReadOnlySpan<byte> data) =>
        Readers.GetValue(schema, s => GenericDatumReader.Create(s)).Read(data);

    /// <summary>Writes a value of <paramref name="schema"/>.</summary>
    public static byte[] Encode(AvroSchema schema, in AvroValue value) =>
        Writers.GetValue(schema, s => GenericDatumWriter.Create(s)).WriteToArray(value);

    private static async Task<AvroValue> WalkAsync(RuleContext context, AvroSchema schema, AvroValue value, IFieldTransform transform)
    {
        var field = context.CurrentField();
        if (field is not null)
        {
            field.Type = TypeOf(schema);
        }

        switch (schema)
        {
            case UnionSchema union:
                return Branch(union, value) is { } branch ? await WalkAsync(context, branch, value, transform).ConfigureAwait(false) : value;
            case ArraySchema array when value.Kind == AvroValueKind.Array:
                var items = new List<AvroValue>(value.AsArray().Count);
                foreach (var item in value.AsArray())
                {
                    items.Add(await WalkAsync(context, array.Items, item, transform).ConfigureAwait(false));
                }

                return AvroValue.FromArray(items);
            case MapSchema map when value.Kind == AvroValueKind.Map:
                var entries = new Dictionary<string, AvroValue>(value.AsMap().Count, StringComparer.Ordinal);
                foreach (var entry in value.AsMap())
                {
                    entries[entry.Key] = await WalkAsync(context, map.Values, entry.Value, transform).ConfigureAwait(false);
                }

                return AvroValue.FromMap(entries);
            case RecordSchema record when value.Kind == AvroValueKind.Record:
                var fields = value.AsRecord();
                for (var i = 0; i < record.Fields.Count; i++)
                {
                    var f = record.Fields[i];
                    using (context.EnterField(fields, record.FullName + "." + f.Name, f.Name, TypeOf(f.Schema), InlineTags(f)))
                    {
                        fields[i] = await WalkAsync(context, f.Schema, fields[i], transform).ConfigureAwait(false);
                    }
                }

                return value;
            default:
                return field is not null && Matches(context.Rule, field)
                    ? await LeafAsync(context, field, schema, value, transform).ConfigureAwait(false)
                    : value;
        }
    }

    // A rule with no tags applies to every field, as in Confluent's AvroUtils.
    private static bool Matches(Rule rule, RuleContext.FieldContext field) =>
        rule.Tags is not { Count: > 0 } tags || tags.Overlaps(field.Tags);

    private static async Task<AvroValue> LeafAsync(RuleContext context, RuleContext.FieldContext field, AvroSchema schema, AvroValue value, IFieldTransform transform)
    {
        var result = await transform.Transform(context, field, ToClr(value)).ConfigureAwait(false);
        if (context.Rule.Kind == RuleKind.Condition)
        {
            return result is false ? throw new RuleConditionException(context.Rule) : value;
        }

        return FromClr(schema, result);
    }

    private static object? ToClr(AvroValue value) => value.Kind switch
    {
        AvroValueKind.Null => null,
        AvroValueKind.Boolean => value.AsBoolean(),
        AvroValueKind.Int => value.AsInt32(),
        AvroValueKind.Long => value.AsInt64(),
        AvroValueKind.Float => value.AsSingle(),
        AvroValueKind.Double => value.AsDouble(),
        AvroValueKind.String => value.AsString(),
        AvroValueKind.Bytes => value.AsBytes(),
        AvroValueKind.Fixed => value.AsFixed().Bytes.ToArray(),
        AvroValueKind.Enum => value.AsEnumSymbol(),
        _ => throw new InvalidOperationException($"A field rule's transform got a {value.Kind}, which only records, arrays and maps hold."),
    };

    private static AvroValue FromClr(AvroSchema schema, object? result) => (schema, result) switch
    {
        (_, null) => AvroValue.Null,
        (_, AvroValue value) => value,
        ({ Type: AvroSchemaType.String }, string s) => s,
        ({ Type: AvroSchemaType.Bytes }, byte[] b) => AvroValue.FromBytes(b),
        (FixedSchema fixedSchema, byte[] b) => AvroValue.FromFixed(new GenericFixed(fixedSchema, b)),
        (EnumSchema enumSchema, string symbol) => AvroValue.FromEnum(enumSchema, symbol),
        ({ Type: AvroSchemaType.Boolean }, bool b) => b,
        ({ Type: AvroSchemaType.Int }, IConvertible n) => Convert.ToInt32(n, System.Globalization.CultureInfo.InvariantCulture),
        ({ Type: AvroSchemaType.Long }, IConvertible n) => Convert.ToInt64(n, System.Globalization.CultureInfo.InvariantCulture),
        ({ Type: AvroSchemaType.Float }, IConvertible n) => Convert.ToSingle(n, System.Globalization.CultureInfo.InvariantCulture),
        ({ Type: AvroSchemaType.Double }, IConvertible n) => Convert.ToDouble(n, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"A field rule's transform returned a {result.GetType().Name}, which isn't a value of the field's schema {schema.CanonicalForm}."),
    };

    // The union branch a value is of: a named type by its full name, anything else by its type.
    private static AvroSchema? Branch(UnionSchema union, AvroValue value)
    {
        string? name = value.Kind switch
        {
            AvroValueKind.Record => value.AsRecord().Schema.FullName,
            AvroValueKind.Fixed => value.AsFixed().Schema.FullName,
            AvroValueKind.Enum => value.EnumSchema!.FullName,
            _ => null,
        };
        var type = value.Kind switch
        {
            AvroValueKind.Null => AvroSchemaType.Null,
            AvroValueKind.Boolean => AvroSchemaType.Boolean,
            AvroValueKind.Int => AvroSchemaType.Int,
            AvroValueKind.Long => AvroSchemaType.Long,
            AvroValueKind.Float => AvroSchemaType.Float,
            AvroValueKind.Double => AvroSchemaType.Double,
            AvroValueKind.String => AvroSchemaType.String,
            AvroValueKind.Bytes => AvroSchemaType.Bytes,
            AvroValueKind.Array => AvroSchemaType.Array,
            AvroValueKind.Map => AvroSchemaType.Map,
            AvroValueKind.Record => AvroSchemaType.Record,
            AvroValueKind.Enum => AvroSchemaType.Enum,
            _ => AvroSchemaType.Fixed,
        };
        return union.Branches.FirstOrDefault(b => b.Type == type && (name is null || string.Equals(((NamedSchema)b).FullName, name, StringComparison.Ordinal)));
    }

    // The field's own tags: its "confluent:tags" property.
    private static HashSet<string> InlineTags(RecordField field)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (field.Properties.TryGetValue("confluent:tags", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in value.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String)
                {
                    tags.Add(tag.GetString()!);
                }
            }
        }

        return tags;
    }

    private static RuleContext.Type TypeOf(AvroSchema schema) => schema.Type switch
    {
        AvroSchemaType.Record => RuleContext.Type.Record,
        AvroSchemaType.Enum => RuleContext.Type.Enum,
        AvroSchemaType.Array => RuleContext.Type.Array,
        AvroSchemaType.Map => RuleContext.Type.Map,
        AvroSchemaType.Union => RuleContext.Type.Combined,
        AvroSchemaType.Fixed => RuleContext.Type.Fixed,
        AvroSchemaType.String => RuleContext.Type.String,
        AvroSchemaType.Bytes => RuleContext.Type.Bytes,
        AvroSchemaType.Int => RuleContext.Type.Int,
        AvroSchemaType.Long => RuleContext.Type.Long,
        AvroSchemaType.Float => RuleContext.Type.Float,
        AvroSchemaType.Double => RuleContext.Type.Double,
        AvroSchemaType.Boolean => RuleContext.Type.Boolean,
        _ => RuleContext.Type.Null,
    };
}
