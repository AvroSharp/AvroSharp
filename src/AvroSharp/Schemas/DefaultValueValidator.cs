using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>Checks a JSON default value against a schema, following the specification's table of default value types.</summary>
internal static class DefaultValueValidator
{
    public static bool IsValid(AvroSchema schema, JsonElement value) => schema switch
    {
        UnionSchema union => IsValidUnion(union, value),
        RecordSchema record => IsValidRecord(record, value),
        EnumSchema enumSchema => value.ValueKind == JsonValueKind.String && enumSchema.TryGetOrdinal(value.GetString()!, out _),
        FixedSchema fixedSchema => value.ValueKind == JsonValueKind.String && IsByteString(value.GetString()!, fixedSchema.Size),
        ArraySchema array => IsValidArray(array, value),
        MapSchema map => IsValidMap(map, value),
        _ => IsValidPrimitive(schema.Type, value),
    };

    // Avro 1.12: the default corresponds to the first branch that matches.
    private static bool IsValidUnion(UnionSchema union, JsonElement value)
    {
        foreach (var branch in union.Branches)
        {
            if (IsValid(branch, value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidRecord(RecordSchema record, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var field in record.Fields)
        {
            if (value.TryGetProperty(field.Name, out var fieldValue))
            {
                if (!IsValid(field.Schema, fieldValue))
                {
                    return false;
                }
            }
            else if (!field.HasDefaultValue)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidArray(ArraySchema array, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (!IsValid(array.Items, item))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidMap(MapSchema map, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var entry in value.EnumerateObject())
        {
            if (!IsValid(map.Values, entry.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidPrimitive(AvroSchemaType type, JsonElement value) => type switch
    {
        AvroSchemaType.Null => value.ValueKind == JsonValueKind.Null,
        AvroSchemaType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        AvroSchemaType.Int => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
        AvroSchemaType.Long => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        AvroSchemaType.Float or AvroSchemaType.Double => value.ValueKind == JsonValueKind.Number,
        AvroSchemaType.Bytes => value.ValueKind == JsonValueKind.String && IsByteString(value.GetString()!, -1),
        AvroSchemaType.String => value.ValueKind == JsonValueKind.String,
        _ => false,
    };

    /// <summary>Bytes and fixed defaults are strings whose code points 0-255 each stand for one byte.</summary>
    private static bool IsByteString(string value, int requiredLength)
    {
        if (requiredLength >= 0 && value.Length != requiredLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c > 'ÿ')
            {
                return false;
            }
        }

        return true;
    }
}
