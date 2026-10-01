using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AvroSharp.IO;
using AvroSharp.Schemas;

namespace AvroSharp.Generic;

// Schema resolution: reading data written with one schema as another (the specification's "Schema Resolution").
public sealed partial class GenericDatumReader
{
    // Keyed by the reader's schema, then the writer's (#129): each value references both schemas, and a value lives as
    // long as its key, so keyed the other way round a long-lived reader schema kept every writer schema alive.
    private static readonly ConditionalWeakTable<AvroSchema, ConditionalWeakTable<AvroSchema, GenericDatumReader>> s_resolvingCache = new();

    private GenericDatumReader(AvroSchema writerSchema, AvroSchema readerSchema, GenericDatumReaderOptions options)
    {
        WriterSchema = writerSchema;
        ReaderSchema = readerSchema;
        _limits = new ReadLimits(options);
        _root = new ResolvingBuilder().Build(writerSchema, readerSchema, "$");
    }

    /// <summary>
    /// Gets a reader for data written with <paramref name="writerSchema"/> that returns values of
    /// <paramref name="readerSchema"/>, resolving the differences the specification allows:
    /// <list type="bullet">
    /// <item>record fields are matched by name or by the reader field's <see cref="RecordField.Aliases">aliases</see>; writer fields the reader does not have are
    /// skipped, and reader fields the writer does not have take their <see cref="RecordField.DefaultValue">default value</see>;</item>
    /// <item>named types match by full name, unqualified name, or the reader's <see cref="NamedSchema.Aliases">aliases</see>;</item>
    /// <item>numbers are promoted (<c>int</c> to <c>long</c>, <c>float</c> or <c>double</c>; <c>long</c> to <c>float</c> or
    /// <c>double</c>; <c>float</c> to <c>double</c>), and <c>string</c> and <c>bytes</c> convert to each other;</item>
    /// <item>enum symbols are matched by name, and a symbol the reader lacks takes the reader's <see cref="EnumSchema.DefaultSymbol">enum default</see>;</item>
    /// <item>a writer union reads each branch as the reader schema (or the first matching branch of a reader union), and
    /// a non-union writer schema is read as the first matching branch of a reader union.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Readers with the default options are cached per pair of schemas. A mismatch that the data may never contain (a
    /// union branch with no counterpart, an enum symbol without a default) is reported with
    /// <see cref="AvroDataException"/> only when such a value is read, as in the Java implementation.
    /// </remarks>
    /// <param name="writerSchema">The schema the data was written with.</param>
    /// <param name="readerSchema">The schema to read the data as.</param>
    /// <param name="options">Limits for malformed input, or <see langword="null"/> for <see cref="GenericDatumReaderOptions.Default"/>.</param>
    /// <exception cref="AvroSchemaException">The schemas cannot be resolved (for example a reader field without a default that the writer lacks).</exception>
    public static GenericDatumReader Create(AvroSchema writerSchema, AvroSchema readerSchema, GenericDatumReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writerSchema);
        ArgumentNullException.ThrowIfNull(readerSchema);
        if (ReferenceEquals(writerSchema, readerSchema))
        {
            return Create(writerSchema, options);
        }

        if (options is not null && !ReferenceEquals(options, GenericDatumReaderOptions.Default))
        {
            return new GenericDatumReader(writerSchema, readerSchema, options);
        }

        var byWriter = s_resolvingCache.GetValue(readerSchema, static _ => new ConditionalWeakTable<AvroSchema, GenericDatumReader>());
        return byWriter.GetValue(writerSchema, writer => new GenericDatumReader(writer, readerSchema, GenericDatumReaderOptions.Default));
    }

    /// <summary>
    /// The resolving reader's rules, for <see cref="AvroSchemaCompatibility"/>, so the check decides each pair as the
    /// reader does. One instance per check: field matching caches each reader record's aliases.
    /// </summary>
    internal sealed class ResolutionRules
    {
        private readonly ResolvingBuilder _builder = new();

        public static bool NamesMatch(NamedSchema writer, NamedSchema reader) => ResolvingBuilder.NamesMatch(writer, reader);

        public static bool FullNamesMatch(NamedSchema writer, NamedSchema reader) => ResolvingBuilder.FullNamesMatch(writer, reader);

        public static AvroSchema? BestBranch(AvroSchema writer, UnionSchema reader) => ResolvingBuilder.BestBranch(writer, reader);

        public static bool CanPromote(AvroSchemaType writer, AvroSchemaType reader) => ResolvingBuilder.Promote(writer, reader) is not null;

        public RecordField? FindField(RecordSchema reader, string writerName) => _builder.FindField(reader, writerName);
    }

    /// <summary>Builds reader nodes that read the writer's encoding and produce values of the reader schema.</summary>
    private sealed class ResolvingBuilder
    {
        // Schemas compare by reference; a pair is registered before its fields are built, for recursive records.
        private readonly Dictionary<(RecordSchema Writer, RecordSchema Reader), ResolvedRecordNode> _records = [];
        private readonly Builder _identity = new();
        private readonly Dictionary<RecordSchema, SkipRecordNode> _skips = [];
        private readonly Dictionary<RecordSchema, Dictionary<string, RecordField>> _fieldAliases = [];

        public ReaderNode Build(AvroSchema writer, AvroSchema reader, string path)
        {
            if (writer is UnionSchema writerUnion)
            {
                // Each writer branch is resolved on its own; one without a counterpart fails only if it is read.
                return new WriterUnionNode([.. writerUnion.Branches.Select(branch => BuildOrDefer(branch, reader, path))]);
            }

            if (reader is UnionSchema readerUnion)
            {
                var branch = BestBranch(writer, readerUnion)
                    ?? throw Incompatible(writer, reader, path, "no branch of the reader's union matches");
                return Build(writer, branch, path);
            }

            return BuildNonUnion(writer, reader, path);
        }

        private ReaderNode BuildOrDefer(AvroSchema writer, AvroSchema reader, string path)
        {
            try
            {
                return Build(writer, reader, path);
            }
            catch (AvroSchemaException ex)
            {
                return new ErrorNode(ex.Message, MinimumSizeOf(writer));
            }
        }

        private ReaderNode BuildNonUnion(AvroSchema writer, AvroSchema reader, string path)
        {
            return (writer, reader) switch
            {
                (RecordSchema w, RecordSchema r) when NamesMatch(w, r) => BuildRecord(w, r, path),
                (EnumSchema w, EnumSchema r) when NamesMatch(w, r) => BuildEnum(w, r),
                (FixedSchema w, FixedSchema r) when NamesMatch(w, r) => w.Size == r.Size
                    ? new ResolvedFixedNode(r)
                    : throw Incompatible(writer, reader, path, $"the fixed sizes differ ({w.Size} and {r.Size})"),
                (ArraySchema w, ArraySchema r) => new ArrayNode(Build(w.Items, r.Items, path + "[]")),
                (MapSchema w, MapSchema r) => new MapNode(Build(w.Values, r.Values, path + "{}")),
                (PrimitiveSchema, PrimitiveSchema) => Promote(writer.Type, reader.Type) ?? throw Incompatible(writer, reader, path, "the types differ and no promotion applies"),
                _ => throw Incompatible(writer, reader, path, writer is NamedSchema && reader is NamedSchema ? "the names differ" : "the types differ"),
            };
        }

        public static ReaderNode? Promote(AvroSchemaType writer, AvroSchemaType reader) => (writer, reader) switch
        {
            _ when writer == reader => reader switch
            {
                AvroSchemaType.Null => NullNode.Instance,
                AvroSchemaType.Boolean => BooleanNode.Instance,
                AvroSchemaType.Int => IntNode.Instance,
                AvroSchemaType.Long => LongNode.Instance,
                AvroSchemaType.Float => FloatNode.Instance,
                AvroSchemaType.Double => DoubleNode.Instance,
                AvroSchemaType.Bytes => BytesNode.Instance,
                _ => StringNode.Instance,
            },
            (AvroSchemaType.Int, AvroSchemaType.Long) => new PromoteNode(1, static (ref r) => (long)r.ReadInt()),
            (AvroSchemaType.Int, AvroSchemaType.Float) => new PromoteNode(1, static (ref r) => (float)r.ReadInt()),
            (AvroSchemaType.Int, AvroSchemaType.Double) => new PromoteNode(1, static (ref r) => (double)r.ReadInt()),
            (AvroSchemaType.Long, AvroSchemaType.Float) => new PromoteNode(1, static (ref r) => (float)r.ReadLong()),
            (AvroSchemaType.Long, AvroSchemaType.Double) => new PromoteNode(1, static (ref r) => (double)r.ReadLong()),
            (AvroSchemaType.Float, AvroSchemaType.Double) => new PromoteNode(sizeof(float), static (ref r) => (double)r.ReadFloat()),
            (AvroSchemaType.String, AvroSchemaType.Bytes) => new PromoteNode(1, static (ref r) => r.ReadStringUtf8().ToArray()),
            (AvroSchemaType.Bytes, AvroSchemaType.String) => new PromoteNode(1, static (ref r) => Encoding.UTF8.GetString(r.ReadBytes())),
            _ => null,
        };

        private ResolvedRecordNode BuildRecord(RecordSchema writer, RecordSchema reader, string path)
        {
            if (_records.TryGetValue((writer, reader), out var existing))
            {
                return existing;
            }

            var node = new ResolvedRecordNode(reader);
            _records.Add((writer, reader), node);

            var assigned = new bool[reader.Fields.Count];
            var steps = new List<(int Target, ReaderNode Node)>(writer.Fields.Count);
            var size = 0L;
            foreach (var writerField in writer.Fields)
            {
                var readerField = FindField(reader, writerField.Name);
                if (readerField is null || assigned[readerField.Position])
                {
                    var skip = BuildSkip(writerField.Schema);
                    steps.Add((-1, skip));
                    size += skip.MinimumSize;
                    continue;
                }

                assigned[readerField.Position] = true;
                var child = Build(writerField.Schema, readerField.Schema, path + "." + readerField.Name);
                steps.Add((readerField.Position, child));
                size += child.MinimumSize;
            }

            var defaults = new List<(int Target, AvroSchema Schema, JsonElement Value, string Field)>();
            foreach (var readerField in reader.Fields)
            {
                if (assigned[readerField.Position])
                {
                    continue;
                }

                var value = readerField.DefaultValue
                    ?? throw new AvroSchemaException(
                        $"At {path}: the reader's field '{reader.FullName}.{readerField.Name}' is not in the writer's schema and has no default value.");
                defaults.Add((readerField.Position, readerField.Schema, value, $"{reader.FullName}.{readerField.Name}"));
            }

            node.Steps = [.. steps];
            node.Defaults = [.. defaults.Select(d => new FieldDefault(d.Target, d.Schema, d.Value, path, d.Field))];
            node.Size = (int)Math.Min(size, int.MaxValue);
            return node;
        }

        /// <summary>
        /// The reader field for a writer field. A reader field's aliases name the writer fields it replaces, and take
        /// precedence over a reader field of the writer field's name (#130): the specification defines aliases as
        /// rewriting the writer's schema, so the writer field is renamed before names are matched, as in Java.
        /// </summary>
        public RecordField? FindField(RecordSchema reader, string writerName)
        {
            if (!_fieldAliases.TryGetValue(reader, out var aliases))
            {
                // Built once per reader record, so a writer record of many fields costs no scan of the reader's per field.
                aliases = new Dictionary<string, RecordField>(StringComparer.Ordinal);
                foreach (var readerField in reader.Fields)
                {
                    foreach (var alias in readerField.Aliases)
                    {
                        aliases.TryAdd(alias, readerField);
                    }
                }

                _fieldAliases.Add(reader, aliases);
            }

            return aliases.TryGetValue(writerName, out var aliased) ? aliased : reader.TryGetField(writerName, out var field) ? field : null;
        }

        private static EnumRemapNode BuildEnum(EnumSchema writer, EnumSchema reader) => new(writer, reader, EnumMap(writer, reader));

        /// <summary>Maps each writer symbol to the reader's ordinal, the reader's default, or -1.</summary>
        public static int[] EnumMap(EnumSchema writer, EnumSchema reader)
        {
            var map = new int[writer.Symbols.Count];
            var readerDefault = reader.DefaultSymbol is { } symbol && reader.TryGetOrdinal(symbol, out var d) ? d : -1;
            for (var i = 0; i < map.Length; i++)
            {
                map[i] = reader.TryGetOrdinal(writer.Symbols[i], out var ordinal) ? ordinal : readerDefault;
            }

            return map;
        }

        /// <summary>
        /// The reader union's branch for a writer schema, as the Java implementation chooses: a branch of the same type
        /// and, for a named type, the same full name (or a reader alias for it); only when no branch has that, one of
        /// the same unqualified name; then one the writer type promotes to. Each is looked for across the whole union,
        /// so <c>com.y.Event</c> is not read as an earlier <c>com.x.Event</c> branch (#130).
        /// </summary>
        public static AvroSchema? BestBranch(AvroSchema writer, UnionSchema reader)
        {
            foreach (var branch in reader.Branches)
            {
                if (branch.Type == writer.Type && (branch is not NamedSchema named || FullNamesMatch((NamedSchema)writer, named)))
                {
                    return branch;
                }
            }

            foreach (var branch in reader.Branches)
            {
                if (branch.Type == writer.Type && branch is NamedSchema named
                    && string.Equals(((NamedSchema)writer).Name.Name, named.Name.Name, StringComparison.Ordinal))
                {
                    return branch;
                }
            }

            foreach (var branch in reader.Branches)
            {
                if (branch is PrimitiveSchema && writer is PrimitiveSchema && Promote(writer.Type, branch.Type) is not null)
                {
                    return branch;
                }
            }

            return null;
        }

        public static bool NamesMatch(NamedSchema writer, NamedSchema reader) =>
            FullNamesMatch(writer, reader) || string.Equals(writer.Name.Name, reader.Name.Name, StringComparison.Ordinal);

        public static bool FullNamesMatch(NamedSchema writer, NamedSchema reader) =>
            string.Equals(writer.FullName, reader.FullName, StringComparison.Ordinal)
            || reader.Aliases.Any(alias => string.Equals(alias.FullName, writer.FullName, StringComparison.Ordinal));

        public ReaderNode BuildSkipNode(AvroSchema writer) => BuildSkip(writer);

        private ReaderNode BuildSkip(AvroSchema writer) => writer switch
        {
            RecordSchema record => SkipRecord(record),
            EnumSchema => SkipVarintNode.Instance,
            FixedSchema fixedSchema => new SkipFixedNode(fixedSchema.Size),
            ArraySchema array => new SkipBlocksNode(BuildSkip(array.Items), isMap: false),
            MapSchema map => new SkipBlocksNode(BuildSkip(map.Values), isMap: true),
            UnionSchema union => new WriterUnionNode([.. union.Branches.Select(BuildSkip)]),
            _ => writer.Type switch
            {
                AvroSchemaType.Null => NullNode.Instance,
                AvroSchemaType.Boolean => new SkipFixedNode(1),
                AvroSchemaType.Int or AvroSchemaType.Long => SkipVarintNode.Instance,
                AvroSchemaType.Float => new SkipFixedNode(sizeof(float)),
                AvroSchemaType.Double => new SkipFixedNode(sizeof(double)),
                _ => SkipBytesNode.Instance,
            },
        };

        private SkipRecordNode SkipRecord(RecordSchema record)
        {
            if (_skips.TryGetValue(record, out var existing))
            {
                return existing;
            }

            var node = new SkipRecordNode();
            _skips.Add(record, node);
            node.Fields = record.Fields.ConvertAll(f => BuildSkip(f.Schema));
            node.Size = (int)Math.Min(node.Fields.Sum(f => (long)f.MinimumSize), int.MaxValue);
            return node;
        }

        // The writer's encoding decides how many bytes a value takes, whatever it is read as.
        private int MinimumSizeOf(AvroSchema writer) => _identity.Build(writer).MinimumSize;

        public static AvroSchemaException Incompatible(AvroSchema writer, AvroSchema reader, string path, string reason) =>
            new($"At {path}: data written as {writer.CanonicalForm} cannot be read as {reader.CanonicalForm}: {reason}.");
    }

    private delegate AvroValue PromoteRead(ref AvroReader reader);

    /// <summary>Reads a writer primitive and converts it to the reader's type.</summary>
    private sealed class PromoteNode(int minimumSize, PromoteRead read) : ReaderNode
    {
        public override int MinimumSize => minimumSize;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => read(ref reader);
    }

    /// <summary>
    /// A reader field the writer does not have, and its default from the reader's schema, converted from JSON once
    /// (#135). A value that cannot be changed through the returned record (null, a number, a string, an enum) is shared;
    /// bytes, fixed values, records, arrays and maps are kept as their binary encoding and decoded per read, so each
    /// record gets its own, as the generated-code plan does.
    /// </summary>
    private sealed class FieldDefault
    {
        // The encoding comes from the reader's own schema, not from input, so the limit on zero-size items, which
        // guards against hostile data, doesn't apply to it: a default of more such items than the limit is valid.
        private static readonly GenericDatumReaderOptions DefaultOptions = new() { MaxZeroSizeItems = int.MaxValue };

        private readonly AvroValue _shared;
        private readonly GenericDatumReader? _decoder;
        private readonly byte[] _encoded = [];

        public FieldDefault(int target, AvroSchema schema, JsonElement value, string path, string field)
        {
            Target = target;
            AvroValue converted;
            try
            {
                converted = GenericDatumJsonReader.ReadDefault(schema, value);
            }
            catch (AvroDataException ex)
            {
                // A field built in code is not checked against its default, as a parsed one is.
                throw new AvroSchemaException($"At {path}: the default of the reader's field '{field}' is not a value of its schema: {ex.Message}", ex);
            }

            if (converted.Kind is AvroValueKind.Bytes or AvroValueKind.Fixed or AvroValueKind.Record or AvroValueKind.Array or AvroValueKind.Map)
            {
                _decoder = GenericDatumReader.Create(schema, DefaultOptions);
                _encoded = GenericDatumWriter.Create(schema).WriteToArray(converted);
            }
            else
            {
                _shared = converted;
            }
        }

        public int Target { get; }

        public AvroValue Create() => _decoder is null ? _shared : _decoder.Read(_encoded);
    }

    /// <summary>Reads the writer's fields in the writer's order into a record of the reader's schema.</summary>
    private sealed class ResolvedRecordNode(RecordSchema readerSchema) : ReaderNode
    {
        public (int Target, ReaderNode Node)[] Steps { get; set; } = [];

        public FieldDefault[] Defaults { get; set; } = [];

        public int Size { get; set; }

        public override int MinimumSize => Size;

        // The record, the writer's fields and the reader's defaults; a recursive record counts itself once.
        public override long ZeroSizeCost => _zeroSizeCost != 0 ? _zeroSizeCost : _zeroSizeCost = RecordCost(Steps.Select(s => s.Node), Defaults.Length, ref _zeroSizeCost);

        private long _zeroSizeCost;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            EnterRecord(ref state);

            var record = new GenericRecord(readerSchema, readerSchema.Fields.Count);
            foreach (var (target, node) in Steps)
            {
                var value = node.Read(ref reader, ref state);
                if (target >= 0)
                {
                    record.ValueAt(target) = value;
                }
            }

            foreach (var fieldDefault in Defaults)
            {
                record.ValueAt(fieldDefault.Target) = fieldDefault.Create();
            }

            ExitRecord(ref state);
            return record;
        }
    }

    private sealed class EnumRemapNode(EnumSchema writer, EnumSchema reader, int[] map) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader r, ref ReadState state)
        {
            var ordinal = r.ReadEnum();
            if ((uint)ordinal >= (uint)map.Length)
            {
                throw new AvroDataException($"Enum ordinal {ordinal} is out of range for '{writer.FullName}' ({map.Length} symbols).");
            }

            var target = map[ordinal];
            return target >= 0
                ? AvroValue.FromEnumUnchecked(reader, target)
                : throw new AvroDataException($"The symbol '{writer.Symbols[ordinal]}' is not in the reader's enum '{reader.FullName}', which has no default.");
        }
    }

    private sealed class ResolvedFixedNode(FixedSchema reader) : ReaderNode
    {
        public override int MinimumSize => reader.Size;

        public override AvroValue Read(ref AvroReader r, ref ReadState state) =>
            new GenericFixed(reader, r.ReadFixedSpan(reader.Size).ToArray());
    }

    /// <summary>A writer union: reads the branch index, then the branch as resolved against the reader schema.</summary>
    private sealed class WriterUnionNode(ReaderNode[] branches) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            var index = reader.ReadUnionIndex();
            if ((uint)index >= (uint)branches.Length)
            {
                throw new AvroDataException($"Union branch index {index} is out of range ({branches.Length} branches).");
            }

            // Not counted as nesting: a union cannot hold a union, so the arrays, maps and records in it count (#129).
            return branches[index].Read(ref reader, ref state);
        }
    }

    /// <summary>A writer schema with no counterpart in the reader's; reported only if such a value is read.</summary>
    private sealed class ErrorNode(string message, int minimumSize) : ReaderNode
    {
        public override int MinimumSize => minimumSize;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state) => throw new AvroDataException(message);
    }

    private sealed class SkipVarintNode : ReaderNode
    {
        public static SkipVarintNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            reader.SkipVarint();
            return AvroValue.Null;
        }
    }

    private sealed class SkipBytesNode : ReaderNode
    {
        public static SkipBytesNode Instance { get; } = new();

        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            reader.SkipBytes();
            return AvroValue.Null;
        }
    }

    private sealed class SkipFixedNode(int size) : ReaderNode
    {
        public override int MinimumSize => size;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            reader.SkipRaw(size);
            return AvroValue.Null;
        }
    }

    private sealed class SkipRecordNode : ReaderNode
    {
        public ReaderNode[] Fields { get; set; } = [];

        public int Size { get; set; }

        public override int MinimumSize => Size;

        // Skipping creates no values, but still visits each field.
        public override long ZeroSizeCost => _zeroSizeCost != 0 ? _zeroSizeCost : _zeroSizeCost = RecordCost(Fields, 0, ref _zeroSizeCost);

        private long _zeroSizeCost;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            EnterRecord(ref state);

            foreach (var field in Fields)
            {
                field.Read(ref reader, ref state);
            }

            ExitRecord(ref state);
            return AvroValue.Null;
        }
    }

    /// <summary>
    /// The zero-size cost of a record whose fields are read by <paramref name="fields"/>, plus
    /// <paramref name="extra"/>. <paramref name="cache"/> is set to 1 while the fields are summed, so a record that
    /// holds itself counts as one value there instead of recursing.
    /// </summary>
    private static long RecordCost(IEnumerable<ReaderNode> fields, long extra, ref long cache)
    {
        cache = 1;
        var total = ZeroSizeValues.Add(1, extra);
        foreach (var field in fields)
        {
            total = ZeroSizeValues.Add(total, field.ZeroSizeCost);
        }

        return total;
    }

    /// <summary>Skips an array or map; a block whose byte size the writer recorded is skipped in one step.</summary>
    private sealed class SkipBlocksNode(ReaderNode items, bool isMap) : ReaderNode
    {
        public override int MinimumSize => 1;

        public override AvroValue Read(ref AvroReader reader, ref ReadState state)
        {
            EnterNesting(ref state);
            long count;
            while ((count = reader.ReadBlockCount(out var byteSize)) != 0)
            {
                if (byteSize >= 0)
                {
                    reader.SkipRaw(byteSize);
                    continue;
                }

                CheckBlockCount(ref reader, ref state, count, 0, (isMap ? 1 : 0) + items.MinimumSize, items.ZeroSizeCost);
                for (var i = 0L; i < count; i++)
                {
                    if (isMap)
                    {
                        reader.SkipBytes();
                    }

                    items.Read(ref reader, ref state);
                }
            }

            state.Nesting--;
            return AvroValue.Null;
        }
    }
}
