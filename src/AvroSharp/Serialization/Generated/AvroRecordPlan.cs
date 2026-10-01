using System;
using System.Collections.Generic;
using System.ComponentModel;
using AvroSharp.IO;

namespace AvroSharp.Serialization.Generated;

/// <summary>How a generated reader turns one writer field into its reader field.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum AvroConversion
{
    /// <summary>The schemas encode alike: read the field with the reader's own code.</summary>
    None,

    /// <summary>The writer wrote an <c>int</c> the reader reads as a wider number.</summary>
    FromInt,

    /// <summary>The writer wrote a <c>long</c> the reader reads as a floating-point number.</summary>
    FromLong,

    /// <summary>The writer wrote a <c>float</c> the reader reads as a <c>double</c>.</summary>
    FromFloat,

    /// <summary>The writer's enum ordinal maps to the reader's (<see cref="AvroRecordPlan.MapEnum"/>).</summary>
    EnumRemap,

    /// <summary>An array of <c>int</c> the reader reads as an array of a wider number.</summary>
    ItemsFromInt,

    /// <summary>An array of <c>long</c> the reader reads as an array of a floating-point number.</summary>
    ItemsFromLong,

    /// <summary>An array of <c>float</c> the reader reads as an array of <c>double</c>.</summary>
    ItemsFromFloat,

    /// <summary>Any other difference: <see cref="AvroRecordPlan.Transcode"/> gives the field in the reader's encoding.</summary>
    Transcode,
}

/// <summary>
/// How a generated record reads data of another version of its schema, built once per pair of schemas: for each
/// writer field in the writer's order, the reader field it fills (or -1 to skip it) and how, then the reader fields
/// the writer lacks, with their defaults already encoded. The generated <c>ReadResolved</c> method follows it.
/// </summary>
/// <remarks>
/// Every step that is not <see cref="AvroConversion.None"/> can also be transcoded, so generated code handles the
/// conversions it knows for a field's type and transcodes the rest.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class AvroRecordPlan
{
    private readonly Step[] _steps;
    private readonly int[] _defaultTargets;
    private readonly byte[][] _defaults;

    internal AvroRecordPlan(Step[] steps, int[] defaultTargets, byte[][] defaults)
    {
        _steps = steps;
        _defaultTargets = defaultTargets;
        _defaults = defaults;
    }

    internal delegate void StepReader(ref AvroReader reader);

    internal delegate ReadOnlySpan<byte> StepTranscoder(ref AvroReader reader);

    /// <summary>Gets the number of writer fields.</summary>
    public int StepCount => _steps.Length;

    /// <summary>Gets the number of reader fields that take their default value.</summary>
    public int DefaultCount => _defaults.Length;

    /// <summary>
    /// Gets the reader field position that writer field <paramref name="step"/> fills (or -1 to skip it) and how it is
    /// read, with one lookup.
    /// </summary>
    /// <param name="step">The writer field position.</param>
    /// <param name="conversion">How the field is read.</param>
    public int Target(int step, out AvroConversion conversion)
    {
        ref readonly var s = ref _steps[step];
        conversion = s.Conversion;
        return s.Target;
    }

    /// <summary>Skips writer field <paramref name="step"/>, which the reader does not have.</summary>
    /// <param name="step">The writer field position.</param>
    /// <param name="reader">The source.</param>
    public void Skip(int step, ref AvroReader reader) => _steps[step].Skip!(ref reader);

    /// <summary>
    /// Reads writer field <paramref name="step"/> and returns it in the reader field's encoding, in a per-thread
    /// buffer that the next call on the thread overwrites.
    /// </summary>
    /// <param name="step">The writer field position.</param>
    /// <param name="reader">The source.</param>
    public ReadOnlySpan<byte> Transcode(int step, ref AvroReader reader) => _steps[step].Transcoder!(ref reader);

    /// <summary>Maps a writer enum ordinal of field <paramref name="step"/> to the reader's.</summary>
    /// <param name="step">The writer field position.</param>
    /// <param name="ordinal">The ordinal read.</param>
    /// <exception cref="AvroDataException">The ordinal is out of range, or its symbol is not in the reader's enum, which has no default.</exception>
    public int MapEnum(int step, int ordinal)
    {
        ref readonly var s = ref _steps[step];
        var map = s.EnumMap!;
        if ((uint)ordinal >= (uint)map.Length)
        {
            throw new AvroDataException($"Enum ordinal {ordinal} is out of range ({map.Length} symbols).");
        }

        var target = map[ordinal];
        return target >= 0
            ? target
            : throw new AvroDataException($"The symbol '{s.WriterSymbols![ordinal]}' is not in the reader's enum '{s.ReaderEnum}', which has no default.");
    }

    /// <summary>Gets the reader field position of default <paramref name="index"/>.</summary>
    /// <param name="index">The default's index.</param>
    public int DefaultTarget(int index) => _defaultTargets[index];

    /// <summary>Gets default <paramref name="index"/> in the reader field's encoding.</summary>
    /// <param name="index">The default's index.</param>
    public ReadOnlySpan<byte> DefaultValue(int index) => _defaults[index];

    /// <summary>
    /// Gets a reader of default <paramref name="index"/>, with no limit on zero-size items: the encoding comes from the
    /// reader's own schema, not from input, so a default of more such items than <see cref="AvroGeneratedCode.MaxZeroSizeItems"/>
    /// is read, as the resolving generic reader reads it (#162).
    /// </summary>
    /// <param name="index">The default's index.</param>
    public AvroReader DefaultReader(int index) => new(_defaults[index]) { ZeroSizeItems = long.MinValue / 2 };

    /// <summary>One writer field.</summary>
    internal readonly struct Step(int target, AvroConversion conversion, StepReader? skip, StepTranscoder? transcoder, int[]? enumMap = null, IReadOnlyList<string>? writerSymbols = null, string? readerEnum = null)
    {
        public int Target { get; } = target;

        public AvroConversion Conversion { get; } = conversion;

        public StepReader? Skip { get; } = skip;

        public StepTranscoder? Transcoder { get; } = transcoder;

        public int[]? EnumMap { get; } = enumMap;

        public IReadOnlyList<string>? WriterSymbols { get; } = writerSymbols;

        public string? ReaderEnum { get; } = readerEnum;
    }
}
