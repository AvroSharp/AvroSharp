using System;
#if NET8_0_OR_GREATER
using System.Buffers;
#endif

namespace AvroSharp.Schemas;

/// <summary>Validation rules for Avro names, namespaces and enum symbols.</summary>
public static class AvroNames
{
#if NET8_0_OR_GREATER
    private static readonly SearchValues<char> s_nameChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_");
#endif

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is a valid simple name:
    /// it starts with <c>[A-Za-z_]</c> and continues with <c>[A-Za-z0-9_]</c>.
    /// The same rule applies to record field names and enum symbols.
    /// </summary>
    /// <param name="name">The name to check.</param>
    public static bool IsValidName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || !IsNameStart(name[0]))
        {
            return false;
        }

#if NET8_0_OR_GREATER
        return name[1..].IndexOfAnyExcept(s_nameChars) < 0;
#else
        for (var i = 1; i < name.Length; i++)
        {
            if (!IsNamePart(name[i]))
            {
                return false;
            }
        }

        return true;
#endif
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a valid namespace: empty (the null namespace)
    /// or a dot-separated sequence of valid names.
    /// </summary>
    /// <param name="value">The namespace to check.</param>
    public static bool IsValidNamespace(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return true;
        }

        while (true)
        {
            var dot = value.IndexOf('.');
            if (dot < 0)
            {
                return IsValidName(value);
            }

            if (!IsValidName(value[..dot]))
            {
                return false;
            }

            value = value[(dot + 1)..];
        }
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="name"/> is one of the eight primitive type names.</summary>
    /// <param name="name">The name to check.</param>
    public static bool IsPrimitiveTypeName(string name) => TryGetPrimitiveType(name, out _);

    internal static bool TryGetPrimitiveType(string name, out AvroSchemaType type)
    {
        type = name switch
        {
            "null" => AvroSchemaType.Null,
            "boolean" => AvroSchemaType.Boolean,
            "int" => AvroSchemaType.Int,
            "long" => AvroSchemaType.Long,
            "float" => AvroSchemaType.Float,
            "double" => AvroSchemaType.Double,
            "bytes" => AvroSchemaType.Bytes,
            "string" => AvroSchemaType.String,
            _ => AvroSchemaType.Record,
        };
        return type != AvroSchemaType.Record;
    }

    internal static string GetTypeName(AvroSchemaType type) => type switch
    {
        AvroSchemaType.Null => "null",
        AvroSchemaType.Boolean => "boolean",
        AvroSchemaType.Int => "int",
        AvroSchemaType.Long => "long",
        AvroSchemaType.Float => "float",
        AvroSchemaType.Double => "double",
        AvroSchemaType.Bytes => "bytes",
        AvroSchemaType.String => "string",
        AvroSchemaType.Record => "record",
        AvroSchemaType.Enum => "enum",
        AvroSchemaType.Array => "array",
        AvroSchemaType.Map => "map",
        AvroSchemaType.Union => "union",
        AvroSchemaType.Fixed => "fixed",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static bool IsNameStart(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

#if !NET8_0_OR_GREATER
    private static bool IsNamePart(char c) => IsNameStart(c) || c is >= '0' and <= '9';
#endif
}
