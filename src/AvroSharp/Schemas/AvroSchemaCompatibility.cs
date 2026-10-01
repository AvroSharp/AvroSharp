using System;
using System.Collections.Generic;
using System.Linq;
using AvroSharp.Generic;
using ResolutionRules = AvroSharp.Generic.GenericDatumReader.ResolutionRules;

namespace AvroSharp.Schemas;

/// <summary>
/// Checks whether data written with one schema can be read with another, and why not: every incompatibility, with
/// where it is, and warnings about differences the specification allows that can change the values read.
/// </summary>
/// <remarks>
/// The check follows the same rules as <see cref="GenericDatumReader.Create(AvroSchema, AvroSchema, GenericDatumReaderOptions?)"/>
/// and generated types' resolving reads, so a schema pair it calls <see cref="AvroCompatibilityVerdict.Incompatible"/>
/// fails there (or can read no value), and a <see cref="AvroCompatibilityVerdict.Compatible"/> one reads every value.
/// Where Java's <c>SchemaCompatibility</c> says compatible, so does this check; where Java says incompatible, this
/// check says <see cref="AvroCompatibilityVerdict.Partial"/> or <see cref="AvroCompatibilityVerdict.Incompatible"/>.
/// </remarks>
/// <seealso cref="AvroCompatibilityResult"/>
/// <seealso cref="AvroCompatibilityLevel"/>
public static class AvroSchemaCompatibility
{
    /// <summary>Checks whether data written with <paramref name="writerSchema"/> can be read with <paramref name="readerSchema"/>.</summary>
    /// <param name="writerSchema">The schema the data is written with.</param>
    /// <param name="readerSchema">The schema to read the data with.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="AvroCompatibilityOptions.Default"/>.</param>
    /// <returns>The verdict, every incompatibility and the warnings.</returns>
    /// <example>
    /// <code>
    /// var result = AvroSchemaCompatibility.Check(writerSchema: v1, readerSchema: v2);
    /// if (!result.IsCompatible)
    /// {
    ///     Console.WriteLine(result); // the verdict, then "$.items[].sku: ..." lines
    /// }
    /// </code>
    /// </example>
    public static AvroCompatibilityResult Check(AvroSchema writerSchema, AvroSchema readerSchema, AvroCompatibilityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writerSchema);
        ArgumentNullException.ThrowIfNull(readerSchema);
        var checker = new Checker();
        var outcome = ReferenceEquals(writerSchema, readerSchema) ? Outcome.Full : checker.Check(writerSchema, readerSchema, "$");
        var verdict = outcome switch
        {
            Outcome.Full => AvroCompatibilityVerdict.Compatible,
            Outcome.Partial => AvroCompatibilityVerdict.Partial,
            _ => AvroCompatibilityVerdict.Incompatible,
        };
        return new AvroCompatibilityResult(writerSchema, readerSchema, verdict, checker.Issues, checker.Warnings, options ?? AvroCompatibilityOptions.Default);
    }

    /// <summary>
    /// Checks a new version of a schema against earlier ones at a schema registry's <paramref name="level"/>:
    /// backward levels check that <paramref name="schema"/> reads the earlier versions' data, forward levels that the
    /// earlier versions read its data, and transitive levels check every earlier version rather than the latest.
    /// </summary>
    /// <param name="schema">The new schema.</param>
    /// <param name="previousVersions">The earlier versions, oldest first.</param>
    /// <param name="level">The compatibility level.</param>
    /// <param name="options">The options for each pair, or <see langword="null"/> for <see cref="AvroCompatibilityOptions.Default"/>.</param>
    /// <returns>Each pair checked and the worst verdict.</returns>
    public static AvroCompatibilityReport Check(AvroSchema schema, IReadOnlyList<AvroSchema> previousVersions, AvroCompatibilityLevel level, AvroCompatibilityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(previousVersions);
        if (level is < AvroCompatibilityLevel.None or > AvroCompatibilityLevel.FullTransitive)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "The level is not an AvroCompatibilityLevel value.");
        }

        var backward = level is AvroCompatibilityLevel.Backward or AvroCompatibilityLevel.BackwardTransitive or AvroCompatibilityLevel.Full or AvroCompatibilityLevel.FullTransitive;
        var forward = level is AvroCompatibilityLevel.Forward or AvroCompatibilityLevel.ForwardTransitive or AvroCompatibilityLevel.Full or AvroCompatibilityLevel.FullTransitive;
        var transitive = level is AvroCompatibilityLevel.BackwardTransitive or AvroCompatibilityLevel.ForwardTransitive or AvroCompatibilityLevel.FullTransitive;
        var checks = new List<AvroCompatibilityCheck>();
        for (var version = transitive ? 0 : Math.Max(previousVersions.Count - 1, 0); version < previousVersions.Count; version++)
        {
            var previous = previousVersions[version] ?? throw new ArgumentException("The list holds null.", nameof(previousVersions));
            if (backward)
            {
                checks.Add(new(version, AvroCompatibilityDirection.Backward, Check(previous, schema, options)));
            }

            if (forward)
            {
                checks.Add(new(version, AvroCompatibilityDirection.Forward, Check(schema, previous, options)));
            }
        }

        return new AvroCompatibilityReport(level, checks);
    }

    // Ordered from best to worst, so the worst of two is the larger.
    private enum Outcome
    {
        Full,
        Partial,
        Fail,
    }

    private static Outcome Worst(Outcome a, Outcome b) => a > b ? a : b;

    /// <summary>
    /// Walks a writer and a reader schema as the resolving reader builds its nodes (GenericDatumReader.Resolution.cs),
    /// with its rule helpers, collecting what the reader would throw or defer instead of stopping at the first.
    /// </summary>
    private sealed class Checker
    {
        // Pairs of records, by reference: a pair met again (recursion, or a type used twice) is not walked again.
        private readonly Dictionary<(RecordSchema Writer, RecordSchema Reader), Visit> _records = [];
        private readonly ResolutionRules _fields = new();

        public List<AvroCompatibilityIssue> Issues { get; } = [];

        public List<AvroCompatibilityIssue> Warnings { get; } = [];

        public Outcome Check(AvroSchema writer, AvroSchema reader, string path)
        {
            if (writer is UnionSchema writerUnion)
            {
                return CheckWriterUnion(writerUnion, reader, path);
            }

            if (reader is UnionSchema readerUnion)
            {
                // As the reader does: the writer type is read as one branch, chosen by type and name alone.
                var branch = ResolutionRules.BestBranch(writer, readerUnion);
                return branch is null
                    ? Fail(AvroCompatibilityKind.MissingUnionBranch, path, writer, reader, $"The reader's union {reader.CanonicalForm} has no branch for {Describe(writer)}.")
                    : Check(writer, branch, path);
            }

            var outcome = (writer, reader) switch
            {
                (RecordSchema w, RecordSchema r) when ResolutionRules.NamesMatch(w, r) => CheckRecord(w, r, path),
                (EnumSchema w, EnumSchema r) when ResolutionRules.NamesMatch(w, r) => CheckEnum(w, r, path),
                (FixedSchema w, FixedSchema r) when ResolutionRules.NamesMatch(w, r) => CheckFixed(w, r, path),
                (ArraySchema w, ArraySchema r) => Check(w.Items, r.Items, path + "[]"),
                (MapSchema w, MapSchema r) => Check(w.Values, r.Values, path + "{}"),
                (PrimitiveSchema, PrimitiveSchema) => CheckPrimitive(writer, reader, path),
                (NamedSchema, NamedSchema) when writer.Type == reader.Type =>
                    Fail(AvroCompatibilityKind.NameMismatch, path, writer, reader, $"{Describe(writer)} cannot be read as {Describe(reader)}: the names differ, and no alias of the reader's matches."),
                _ => Fail(AvroCompatibilityKind.TypeMismatch, path, writer, reader, $"{Describe(writer)} cannot be read as {Describe(reader)}."),
            };

            if (outcome != Outcome.Fail)
            {
                CheckLogicalType(writer, reader, path);
            }

            return outcome;
        }

        // Each writer branch is resolved on its own, as the reader does: one that can't be read fails only when a value
        // of it is read.
        private Outcome CheckWriterUnion(UnionSchema writer, AvroSchema reader, string path)
        {
            if (writer.Branches.Count == 0)
            {
                return Outcome.Full;
            }

            int full = 0, failed = 0;
            for (var i = 0; i < writer.Branches.Count; i++)
            {
                var branch = writer.Branches[i];
                var outcome = Check(branch, reader, $"{path}[{i}:{BranchLabel(branch)}]");
                full += outcome == Outcome.Full ? 1 : 0;
                failed += outcome == Outcome.Fail ? 1 : 0;
            }

            return failed == writer.Branches.Count ? Outcome.Fail : full == writer.Branches.Count ? Outcome.Full : Outcome.Partial;
        }

        private Outcome CheckRecord(RecordSchema writer, RecordSchema reader, string path)
        {
            if (_records.TryGetValue((writer, reader), out var seen))
            {
                return seen.Revisit(path, Issues, Warnings);
            }

            var visit = new Visit(path, Issues.Count, Warnings.Count);
            _records.Add((writer, reader), visit);
            WarnIfUnqualified(writer, reader, path);

            var outcome = Outcome.Full;
            var assignedFrom = new string?[reader.Fields.Count];
            foreach (var writerField in writer.Fields)
            {
                var readerField = _fields.FindField(reader, writerField.Name);
                if (readerField is null)
                {
                    continue;
                }

                var fieldPath = path + "." + readerField.Name;
                if (assignedFrom[readerField.Position] is { } first)
                {
                    Warn(AvroCompatibilityKind.AmbiguousFieldAlias, fieldPath, writer, reader,
                        $"The writer's fields '{first}' and '{writerField.Name}' both match the reader's field '{readerField.Name}'; '{writerField.Name}' is skipped.");
                    continue;
                }

                assignedFrom[readerField.Position] = writerField.Name;
                outcome = Worst(outcome, Check(writerField.Schema, readerField.Schema, fieldPath));
            }

            foreach (var readerField in reader.Fields.Where(field => assignedFrom[field.Position] is null))
            {
                outcome = Worst(outcome, CheckDefault(writer, reader, readerField, path + "." + readerField.Name));
            }

            visit.Complete(outcome, Issues.Count, Warnings.Count);
            return outcome;
        }

        private Outcome CheckDefault(RecordSchema writer, RecordSchema reader, RecordField readerField, string path)
        {
            if (readerField.DefaultValue is not { } value)
            {
                return Fail(AvroCompatibilityKind.MissingDefault, path, writer, reader,
                    $"The reader's field '{reader.FullName}.{readerField.Name}' is not in the writer's record {writer.FullName} and has no default value.");
            }

            try
            {
                GenericDatumJsonReader.ReadDefault(readerField.Schema, value);
                return Outcome.Full;
            }
            catch (AvroDataException ex)
            {
                return Fail(AvroCompatibilityKind.InvalidDefault, path, writer, reader,
                    $"The default of the reader's field '{reader.FullName}.{readerField.Name}', which the writer lacks, is not a value of its schema: {ex.Message}");
            }
        }

        private Outcome CheckEnum(EnumSchema writer, EnumSchema reader, string path)
        {
            WarnIfUnqualified(writer, reader, path);
            var missing = writer.Symbols.Where(symbol => !reader.TryGetOrdinal(symbol, out _)).ToList();
            if (missing.Count == 0)
            {
                return Outcome.Full;
            }

            var symbols = string.Join(", ", missing);
            if (reader.DefaultSymbol is { } fallback && reader.TryGetOrdinal(fallback, out _))
            {
                Warn(AvroCompatibilityKind.EnumDefaultUsed, path, writer, reader, $"The writer's symbols [{symbols}] are not in the reader's enum {reader.FullName}, and are read as its default '{fallback}'.");
                return Outcome.Full;
            }

            var outcome = missing.Count == writer.Symbols.Count ? Outcome.Fail : Outcome.Partial;
            Issues.Add(new(AvroCompatibilityKind.MissingEnumSymbols, path,
                $"The reader's enum {reader.FullName} lacks the writer's symbols [{symbols}] and has no default: values of them cannot be read.", writer, reader));
            return outcome;
        }

        private Outcome CheckFixed(FixedSchema writer, FixedSchema reader, string path)
        {
            if (writer.Size != reader.Size)
            {
                return Fail(AvroCompatibilityKind.FixedSizeMismatch, path, writer, reader, $"{Describe(writer)} has {writer.Size} bytes, and {Describe(reader)} {reader.Size}.");
            }

            WarnIfUnqualified(writer, reader, path);
            return Outcome.Full;
        }

        private Outcome CheckPrimitive(AvroSchema writer, AvroSchema reader, string path)
        {
            if (!ResolutionRules.CanPromote(writer.Type, reader.Type))
            {
                return Fail(AvroCompatibilityKind.TypeMismatch, path, writer, reader, $"{Describe(writer)} cannot be read as {Describe(reader)}: no promotion applies.");
            }

            var loss = (writer.Type, reader.Type) switch
            {
                (AvroSchemaType.Int or AvroSchemaType.Long, AvroSchemaType.Float) or (AvroSchemaType.Long, AvroSchemaType.Double) => "large values are rounded",
                (AvroSchemaType.Bytes, AvroSchemaType.String) => "bytes that are not UTF-8 are read as replacement characters",
                _ => null,
            };
            if (loss is not null)
            {
                Warn(AvroCompatibilityKind.LossyPromotion, path, writer, reader, $"{Describe(writer)} is read as {Describe(reader)}: {loss}.");
            }

            return Outcome.Full;
        }

        // Logical types don't take part in resolution (the specification's rule): the encoding is read as the reader's
        // logical type, so a different one, or a decimal of another scale, reads as another value.
        private void CheckLogicalType(AvroSchema writer, AvroSchema reader, string path)
        {
            var (w, r) = (writer.LogicalType, reader.LogicalType);
            if (w is DecimalLogicalType wd && r is DecimalLogicalType rd)
            {
                if (wd.Scale != rd.Scale)
                {
                    Warn(AvroCompatibilityKind.DecimalChanged, path, writer, reader, $"A decimal of scale {wd.Scale} is read with scale {rd.Scale}, so every value is read as a different number.");
                }
                else if (rd.Precision < wd.Precision)
                {
                    Warn(AvroCompatibilityKind.DecimalChanged, path, writer, reader, $"A decimal of precision {wd.Precision} is read with precision {rd.Precision}, which may not hold every value.");
                }
            }
            else if (!string.Equals(w?.Name, r?.Name, StringComparison.Ordinal))
            {
                Warn(AvroCompatibilityKind.LogicalTypeChanged, path, writer, reader, $"{Describe(writer)} is read as {Describe(reader)}: the same encoding means another value.");
            }
        }

        private void WarnIfUnqualified(NamedSchema writer, NamedSchema reader, string path)
        {
            if (!ResolutionRules.FullNamesMatch(writer, reader))
            {
                Warn(AvroCompatibilityKind.UnqualifiedNameMatch, path, writer, reader, $"{Describe(writer)} is read as {Describe(reader)}, matched by its unqualified name.");
            }
        }

        private Outcome Fail(AvroCompatibilityKind kind, string path, AvroSchema writer, AvroSchema reader, string message)
        {
            Issues.Add(new(kind, path, message, writer, reader));
            return Outcome.Fail;
        }

        private void Warn(AvroCompatibilityKind kind, string path, AvroSchema writer, AvroSchema reader, string message) =>
            Warnings.Add(new(kind, path, message, writer, reader));

        private static string BranchLabel(AvroSchema branch) => branch is NamedSchema named ? named.Name.Name : TypeName(branch);

        private static string TypeName(AvroSchema schema) => schema.Type switch
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
            AvroSchemaType.Fixed => "fixed",
            _ => "union",
        };

        private static string Describe(AvroSchema schema) => schema switch
        {
            NamedSchema named => $"{TypeName(schema)} {named.FullName}{LogicalSuffix(schema)}",
            UnionSchema => $"the union {schema.CanonicalForm}",
            _ => $"{TypeName(schema)}{LogicalSuffix(schema)}",
        };

        private static string LogicalSuffix(AvroSchema schema) => schema.LogicalType switch
        {
            null => string.Empty,
            DecimalLogicalType d => $" (decimal({d.Precision},{d.Scale}))",
            var logical => $" ({logical.Name})",
        };
    }

    /// <summary>A record pair walked once; met again elsewhere, its issues also name the new path.</summary>
    private sealed class Visit(string path, int firstIssue, int firstWarning)
    {
        private Outcome? _outcome;
        private int _endIssue;
        private int _endWarning;

        public void Complete(Outcome outcome, int endIssue, int endWarning)
        {
            _outcome = outcome;
            _endIssue = endIssue;
            _endWarning = endWarning;
        }

        public Outcome Revisit(string newPath, List<AvroCompatibilityIssue> issues, List<AvroCompatibilityIssue> warnings)
        {
            // Still being walked: a recursive record, whose rest is checked where it was first met.
            if (_outcome is not { } outcome)
            {
                return Outcome.Full;
            }

            AddPaths(issues, firstIssue, _endIssue, newPath);
            AddPaths(warnings, firstWarning, _endWarning, newPath);
            return outcome;
        }

        private void AddPaths(List<AvroCompatibilityIssue> list, int start, int end, string newPath)
        {
            for (var i = start; i < end; i++)
            {
                if (list[i].Path.StartsWith(path, StringComparison.Ordinal))
                {
                    list[i].OtherPathList.Add(newPath + list[i].Path[path.Length..]);
                }
            }
        }
    }
}
