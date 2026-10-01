using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AvroSharp.Schemas;

/// <summary>Whether data written with one schema can be read with another (<see cref="AvroSchemaCompatibility"/>).</summary>
public enum AvroCompatibilityVerdict
{
    /// <summary>Every value written with the writer's schema can be read with the reader's.</summary>
    Compatible,

    /// <summary>
    /// The reader can be created, but some values cannot be read: an enum symbol the reader lacks (and has no default
    /// for), or a writer union branch with no counterpart. The issues name them. Java's <c>SchemaCompatibility</c> and
    /// schema registries call this incompatible.
    /// </summary>
    Partial,

    /// <summary>The reader cannot be created, or can read no value of the writer's schema.</summary>
    Incompatible,
}

/// <summary>
/// The kind of an <see cref="AvroCompatibilityIssue"/>. The first seven are incompatibilities; the six named as Java's
/// <c>SchemaIncompatibilityType</c> values mean the same. The rest are warnings: the specification allows them, but
/// the values read may differ from the values written.
/// </summary>
public enum AvroCompatibilityKind
{
    /// <summary>The types differ and no promotion applies (Java's <c>TYPE_MISMATCH</c>).</summary>
    TypeMismatch,

    /// <summary>Two records, enums or fixed types have different names, and no reader alias matches (Java's <c>NAME_MISMATCH</c>).</summary>
    NameMismatch,

    /// <summary>Two fixed types have different sizes (Java's <c>FIXED_SIZE_MISMATCH</c>).</summary>
    FixedSizeMismatch,

    /// <summary>A reader field is not in the writer's record and has no default (Java's <c>READER_FIELD_MISSING_DEFAULT_VALUE</c>).</summary>
    MissingDefault,

    /// <summary>The reader's enum lacks some of the writer's symbols and has no default (Java's <c>MISSING_ENUM_SYMBOLS</c>).</summary>
    MissingEnumSymbols,

    /// <summary>A reader union has no branch for a writer type (Java's <c>MISSING_UNION_BRANCH</c>).</summary>
    MissingUnionBranch,

    /// <summary>A reader field the writer lacks has a default that is not a value of the field's schema.</summary>
    InvalidDefault,

    /// <summary>
    /// Warning: a promotion that can change values: <c>int</c> or <c>long</c> to <c>float</c>, <c>long</c> to
    /// <c>double</c> (large values are rounded), or <c>bytes</c> to <c>string</c> (bytes that are not UTF-8 are replaced).
    /// </summary>
    LossyPromotion,

    /// <summary>Warning: the logical type differs, so the same encoded number or bytes mean a different value (for example <c>date</c> read as <c>time-millis</c>).</summary>
    LogicalTypeChanged,

    /// <summary>Warning: the decimal's precision or scale differs; a changed scale reads every value as a different number.</summary>
    DecimalChanged,

    /// <summary>Warning: a named type matched only by its unqualified name, as the specification allows, though its namespace differs.</summary>
    UnqualifiedNameMatch,

    /// <summary>Warning: writer enum symbols the reader lacks are read as the reader's enum default.</summary>
    EnumDefaultUsed,

    /// <summary>Warning: two writer fields match one reader field (by name or alias); the later one is skipped.</summary>
    AmbiguousFieldAlias,
}

/// <summary>One reason data written with one schema cannot be read with another, or a warning about how it is read.</summary>
public sealed class AvroCompatibilityIssue
{
    internal AvroCompatibilityIssue(AvroCompatibilityKind kind, string path, string message, AvroSchema writerSchema, AvroSchema readerSchema)
    {
        Kind = kind;
        Path = path;
        Message = message;
        WriterSchema = writerSchema;
        ReaderSchema = readerSchema;
    }

    /// <summary>Gets the kind of issue.</summary>
    public AvroCompatibilityKind Kind { get; }

    /// <summary>Gets a value indicating whether the issue is a warning rather than an incompatibility.</summary>
    public bool IsWarning => Kind >= AvroCompatibilityKind.LossyPromotion;

    /// <summary>
    /// Gets where in the data the issue is: <c>$</c> for the whole value, then <c>.field</c> (the reader's field name),
    /// <c>[]</c> for array items, <c>{}</c> for map values, and <c>[index:type]</c> for a writer union branch, as in
    /// <c>$.payment[1:Card].number</c>.
    /// </summary>
    public string Path { get; }

    /// <summary>Gets the other paths with the same issue, where the same pair of named types appears again.</summary>
    public IReadOnlyList<string> OtherPaths => OtherPathList;

    /// <summary>Gets a description of the issue.</summary>
    public string Message { get; }

    /// <summary>Gets the part of the writer's schema at <see cref="Path"/>.</summary>
    public AvroSchema WriterSchema { get; }

    /// <summary>Gets the part of the reader's schema at <see cref="Path"/>.</summary>
    public AvroSchema ReaderSchema { get; }

    internal List<string> OtherPathList { get; } = [];

    /// <summary>Returns the path and the message.</summary>
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>The result of <see cref="AvroSchemaCompatibility.Check(AvroSchema, AvroSchema, AvroCompatibilityOptions?)"/>.</summary>
public sealed class AvroCompatibilityResult
{
    internal AvroCompatibilityResult(AvroSchema writerSchema, AvroSchema readerSchema, AvroCompatibilityVerdict verdict, IReadOnlyList<AvroCompatibilityIssue> issues, IReadOnlyList<AvroCompatibilityIssue> warnings, AvroCompatibilityOptions options)
    {
        WriterSchema = writerSchema;
        ReaderSchema = readerSchema;
        Verdict = options.Strict && warnings.Count > 0 ? AvroCompatibilityVerdict.Incompatible : verdict;
        Issues = issues;
        Warnings = warnings;
        IsCompatible = Verdict == AvroCompatibilityVerdict.Compatible || (Verdict == AvroCompatibilityVerdict.Partial && options.AllowPartial);
    }

    /// <summary>Gets the schema the data is written with.</summary>
    public AvroSchema WriterSchema { get; }

    /// <summary>Gets the schema the data is read with.</summary>
    public AvroSchema ReaderSchema { get; }

    /// <summary>Gets the verdict. With <see cref="AvroCompatibilityOptions.Strict"/>, any warning makes it <see cref="AvroCompatibilityVerdict.Incompatible"/>.</summary>
    public AvroCompatibilityVerdict Verdict { get; }

    /// <summary>
    /// Gets a value indicating whether the verdict is <see cref="AvroCompatibilityVerdict.Compatible"/>, or
    /// <see cref="AvroCompatibilityVerdict.Partial"/> when <see cref="AvroCompatibilityOptions.AllowPartial"/> is set.
    /// </summary>
    public bool IsCompatible { get; }

    /// <summary>Gets every incompatibility, in the order the schemas are walked. Warnings are in <see cref="Warnings"/>, also with <see cref="AvroCompatibilityOptions.Strict"/>.</summary>
    public IReadOnlyList<AvroCompatibilityIssue> Issues { get; }

    /// <summary>Gets the warnings: differences the specification allows that can change the values read.</summary>
    public IReadOnlyList<AvroCompatibilityIssue> Warnings { get; }

    /// <summary>Throws when <see cref="IsCompatible"/> is <see langword="false"/>, with every issue in the message.</summary>
    /// <exception cref="AvroSchemaException">The schemas are not compatible.</exception>
    public void ThrowIfIncompatible()
    {
        if (!IsCompatible)
        {
            throw new AvroSchemaException(ToString());
        }
    }

    /// <summary>Returns the verdict, then one line per issue and warning.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(Describe(Verdict));
        AvroCompatibilityReport.AppendIssues(text, Issues, Warnings, "  ");
        return text.ToString();
    }

    internal static string Describe(AvroCompatibilityVerdict verdict) => verdict switch
    {
        AvroCompatibilityVerdict.Compatible => "Compatible.",
        AvroCompatibilityVerdict.Partial => "Partially compatible: some values written with the writer's schema cannot be read with the reader's.",
        _ => "Incompatible.",
    };
}

/// <summary>Options for <see cref="AvroSchemaCompatibility"/>.</summary>
public sealed class AvroCompatibilityOptions
{
    /// <summary>Gets the default options: only <see cref="AvroCompatibilityVerdict.Compatible"/> passes, and warnings don't fail.</summary>
    public static AvroCompatibilityOptions Default { get; } = new();

    /// <summary>Gets a value indicating whether a <see cref="AvroCompatibilityVerdict.Partial"/> verdict counts as compatible.</summary>
    public bool AllowPartial { get; init; }

    /// <summary>Gets a value indicating whether warnings make the verdict <see cref="AvroCompatibilityVerdict.Incompatible"/>.</summary>
    public bool Strict { get; init; }
}

/// <summary>A schema registry's compatibility levels, as Confluent Schema Registry defines them.</summary>
public enum AvroCompatibilityLevel
{
    /// <summary>No check.</summary>
    None,

    /// <summary>The new schema can read data written with the latest earlier version.</summary>
    Backward,

    /// <summary>The new schema can read data written with every earlier version.</summary>
    BackwardTransitive,

    /// <summary>The latest earlier version can read data written with the new schema.</summary>
    Forward,

    /// <summary>Every earlier version can read data written with the new schema.</summary>
    ForwardTransitive,

    /// <summary><see cref="Backward"/> and <see cref="Forward"/>.</summary>
    Full,

    /// <summary><see cref="BackwardTransitive"/> and <see cref="ForwardTransitive"/>.</summary>
    FullTransitive,
}

/// <summary>Which way a <see cref="AvroCompatibilityCheck"/> reads.</summary>
public enum AvroCompatibilityDirection
{
    /// <summary>The new schema reads data written with the earlier version.</summary>
    Backward,

    /// <summary>The earlier version reads data written with the new schema.</summary>
    Forward,
}

/// <summary>One pair checked by <see cref="AvroSchemaCompatibility.Check(AvroSchema, IReadOnlyList{AvroSchema}, AvroCompatibilityLevel, AvroCompatibilityOptions?)"/>.</summary>
public sealed class AvroCompatibilityCheck
{
    internal AvroCompatibilityCheck(int version, AvroCompatibilityDirection direction, AvroCompatibilityResult result)
    {
        Version = version;
        Direction = direction;
        Result = result;
    }

    /// <summary>Gets the index of the earlier version in the list given.</summary>
    public int Version { get; }

    /// <summary>Gets which way the pair reads.</summary>
    public AvroCompatibilityDirection Direction { get; }

    /// <summary>Gets the result for the pair.</summary>
    public AvroCompatibilityResult Result { get; }
}

/// <summary>The result of checking a new schema against earlier versions at a <see cref="AvroCompatibilityLevel"/>.</summary>
public sealed class AvroCompatibilityReport
{
    internal AvroCompatibilityReport(AvroCompatibilityLevel level, IReadOnlyList<AvroCompatibilityCheck> checks)
    {
        Level = level;
        Checks = checks;
        Verdict = checks.Count == 0 ? AvroCompatibilityVerdict.Compatible : checks.Max(c => c.Result.Verdict);
        IsCompatible = checks.All(c => c.Result.IsCompatible);
    }

    /// <summary>Gets the level checked.</summary>
    public AvroCompatibilityLevel Level { get; }

    /// <summary>Gets the worst verdict of the pairs checked.</summary>
    public AvroCompatibilityVerdict Verdict { get; }

    /// <summary>Gets a value indicating whether every pair is compatible.</summary>
    public bool IsCompatible { get; }

    /// <summary>Gets the pairs checked, by earlier version, backward before forward.</summary>
    public IReadOnlyList<AvroCompatibilityCheck> Checks { get; }

    /// <summary>Throws when <see cref="IsCompatible"/> is <see langword="false"/>, with every issue in the message.</summary>
    /// <exception cref="AvroSchemaException">The new schema is not compatible at the level.</exception>
    public void ThrowIfIncompatible()
    {
        if (!IsCompatible)
        {
            throw new AvroSchemaException(ToString());
        }
    }

    /// <summary>Returns the verdict, then each pair's verdict and issues.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(AvroCompatibilityResult.Describe(Verdict));
        foreach (var check in Checks)
        {
            text.AppendLine().Append(check.Direction == AvroCompatibilityDirection.Backward
                ? $"Reading version {check.Version}'s data with the new schema: "
                : $"Reading the new schema's data with version {check.Version}: ").Append(AvroCompatibilityResult.Describe(check.Result.Verdict));
            AppendIssues(text, check.Result.Issues, check.Result.Warnings, "  ");
        }

        return text.ToString();
    }

    internal static void AppendIssues(StringBuilder text, IReadOnlyList<AvroCompatibilityIssue> issues, IReadOnlyList<AvroCompatibilityIssue> warnings, string indent)
    {
        foreach (var issue in issues.Concat(warnings))
        {
            text.AppendLine().Append(indent).Append(issue.IsWarning ? "warning " : string.Empty).Append(issue);
            if (issue.OtherPaths.Count > 0)
            {
                text.Append(" (also at ").Append(string.Join(", ", issue.OtherPaths)).Append(')');
            }
        }
    }
}
