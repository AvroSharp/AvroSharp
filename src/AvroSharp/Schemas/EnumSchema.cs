using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AvroSharp.Schemas;

/// <summary>An enum schema: a named set of symbols.</summary>
public sealed class EnumSchema : NamedSchema
{
    private readonly Dictionary<string, int> _ordinals;

    /// <summary>Initializes an enum schema.</summary>
    /// <param name="name">The enum name.</param>
    /// <param name="symbols">The symbols; each must be a valid Avro name and appear once.</param>
    /// <param name="defaultSymbol">The symbol used during resolution for unknown writer symbols, or <see langword="null"/>.</param>
    /// <param name="doc">Optional documentation.</param>
    /// <param name="aliases">Optional alternative names.</param>
    /// <param name="properties">Optional custom properties.</param>
    /// <exception cref="AvroSchemaException">A symbol is invalid or duplicated, or the default is not a symbol.</exception>
    public EnumSchema(
        SchemaName name,
        IEnumerable<string> symbols,
        string? defaultSymbol = null,
        string? doc = null,
        IEnumerable<SchemaName>? aliases = null,
        IReadOnlyDictionary<string, JsonElement>? properties = null)
        : this(name, symbols?.ToArray()!, defaultSymbol, doc, aliases, properties, validate: true)
    {
    }

    internal EnumSchema(
        SchemaName name,
        string[] symbols,
        string? defaultSymbol,
        string? doc,
        IEnumerable<SchemaName>? aliases,
        IReadOnlyDictionary<string, JsonElement>? properties,
        bool validate)
        : base(AvroSchemaType.Enum, name, aliases, doc, logicalType: null, properties)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        _ordinals = new Dictionary<string, int>(symbols.Length, StringComparer.Ordinal);
        for (var i = 0; i < symbols.Length; i++)
        {
            var symbol = symbols[i] ?? throw new AvroSchemaException($"Enum '{name.FullName}' has a null symbol.");
            if (validate && !AvroNames.IsValidName(symbol.AsSpan()))
            {
                throw new AvroSchemaException($"'{symbol}' is not a valid symbol of enum '{name.FullName}': it must start with [A-Za-z_] and contain only [A-Za-z0-9_].");
            }

#if NETSTANDARD2_0
            if (_ordinals.ContainsKey(symbol))
            {
                throw new AvroSchemaException($"Enum '{name.FullName}' has the symbol '{symbol}' more than once.");
            }

            _ordinals.Add(symbol, i);
#else
            if (!_ordinals.TryAdd(symbol, i))
            {
                throw new AvroSchemaException($"Enum '{name.FullName}' has the symbol '{symbol}' more than once.");
            }
#endif
        }

        if (defaultSymbol is not null && !_ordinals.ContainsKey(defaultSymbol))
        {
            throw new AvroSchemaException($"The default '{defaultSymbol}' of enum '{name.FullName}' is not one of its symbols.");
        }

        Symbols = symbols;
        DefaultSymbol = defaultSymbol;
    }

    /// <summary>Gets the symbols, in declaration order (the order defines each symbol's ordinal).</summary>
    public IReadOnlyList<string> Symbols { get; }

    /// <summary>Gets the default symbol used during schema resolution, or <see langword="null"/>.</summary>
    public string? DefaultSymbol { get; }

    /// <summary>Gets the ordinal of a symbol.</summary>
    /// <param name="symbol">The symbol (case-sensitive).</param>
    /// <param name="ordinal">The zero-based ordinal, when found.</param>
    public bool TryGetOrdinal(string symbol, out int ordinal) => _ordinals.TryGetValue(symbol, out ordinal);
}
