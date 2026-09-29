using System.Runtime.CompilerServices;
using AvroSharp.Schemas;
using AvroSharp.Serialization;

namespace AvroSharp.Generic;

/// <summary>
/// Resolution plans for generated records: which writer field fills which reader field and how, built once per pair
/// of schemas with the same rules as the resolving reader (field names and aliases, skips, defaults, promotions, enum
/// symbols). Generated code reads the fields itself; only differences it has no code for are transcoded, field by field.
/// </summary>
public sealed partial class GenericDatumReader
{
    // Keyed by the reader's schema, then the writer's, like the resolving reader's cache (#129): a value lives as long
    // as its key, so a value that referenced the outer key would keep it alive for as long as the inner key lives.
    private static readonly ConditionalWeakTable<AvroSchema, ConditionalWeakTable<AvroSchema, PlanHolder>> s_plans = new();

    /// <summary>
    /// Gets the cached plan for reading records of <paramref name="writerSchema"/> as <paramref name="readerSchema"/>, or
    /// <see langword="null"/> when they are not two records of matching names (the caller then resolves the whole value).
    /// </summary>
    /// <exception cref="AvroSchemaException">The schemas cannot be resolved.</exception>
    internal static AvroRecordPlan? GetRecordPlan(AvroSchema writerSchema, AvroSchema readerSchema)
    {
        var byWriter = s_plans.GetValue(readerSchema, static _ => new ConditionalWeakTable<AvroSchema, PlanHolder>());
        return byWriter.GetValue(writerSchema, writer => new PlanHolder(CreateRecordPlan(writer, readerSchema))).Plan;
    }

    private static AvroRecordPlan? CreateRecordPlan(AvroSchema writerSchema, AvroSchema readerSchema)
    {
        if (writerSchema is not RecordSchema writer || readerSchema is not RecordSchema reader || !ResolvingBuilder.NamesMatch(writer, reader))
        {
            return null;
        }

        var skips = new ResolvingBuilder();
        var assigned = new bool[reader.Fields.Count];
        var steps = new AvroRecordPlan.Step[writer.Fields.Count];
        for (var i = 0; i < steps.Length; i++)
        {
            var writerField = writer.Fields[i];
            var readerField = skips.FindField(reader, writerField.Name);
            if (readerField is null || assigned[readerField.Position])
            {
                var skip = skips.BuildSkipNode(writerField.Schema);
                steps[i] = new AvroRecordPlan.Step(-1, AvroConversion.None, (ref r) =>
                {
                    var state = ReadState.ForDefaultOptions();
                    skip.Read(ref r, ref state);
                }, transcoder: null);
                continue;
            }

            assigned[readerField.Position] = true;
            steps[i] = StepFor(writerField.Schema, readerField, "$." + readerField.Name);
        }

        var defaultTargets = new System.Collections.Generic.List<int>();
        var defaults = new System.Collections.Generic.List<byte[]>();
        foreach (var readerField in reader.Fields)
        {
            if (assigned[readerField.Position])
            {
                continue;
            }

            var value = readerField.DefaultValue
                ?? throw new AvroSchemaException(
                    $"At $: the reader's field '{reader.FullName}.{readerField.Name}' is not in the writer's schema and has no default value.");
            defaultTargets.Add(readerField.Position);
            defaults.Add(GenericDatumWriter.Create(readerField.Schema).WriteToArray(GenericDatumJsonReader.ReadDefault(readerField.Schema, value)));
        }

        return new AvroRecordPlan(steps, [.. defaultTargets], [.. defaults]);
    }

    // Every step that generated code may not read directly gets a transcoder too, built now so that schemas that
    // cannot be resolved fail here, as the resolving reader does.
    private static AvroRecordPlan.Step StepFor(AvroSchema writer, RecordField readerField, string path)
    {
        var reader = readerField.Schema;
        if (AvroGeneratedCode.IsSameSchema(writer, reader))
        {
            return new AvroRecordPlan.Step(readerField.Position, AvroConversion.None, skip: null, transcoder: null);
        }

        var transcoder = new Transcoder(writer, reader, path);
        AvroRecordPlan.StepTranscoder transcode = transcoder.TranscodeToBuffer;
        if (writer is EnumSchema writerEnum && reader is EnumSchema readerEnum && ResolvingBuilder.NamesMatch(writerEnum, readerEnum))
        {
            return new AvroRecordPlan.Step(readerField.Position, AvroConversion.EnumRemap, skip: null, transcode,
                ResolvingBuilder.EnumMap(writerEnum, readerEnum), writerEnum.Symbols, readerEnum.FullName);
        }

        var conversion = (writer, reader) switch
        {
            (PrimitiveSchema, PrimitiveSchema) => Promotion(writer.Type, reader.Type, AvroConversion.FromInt, AvroConversion.FromLong, AvroConversion.FromFloat),
            (ArraySchema { Items: PrimitiveSchema w }, ArraySchema { Items: PrimitiveSchema r }) =>
                Promotion(w.Type, r.Type, AvroConversion.ItemsFromInt, AvroConversion.ItemsFromLong, AvroConversion.ItemsFromFloat),
            _ => AvroConversion.Transcode,
        };
        return new AvroRecordPlan.Step(readerField.Position, conversion, skip: null, transcode);
    }

    private static AvroConversion Promotion(AvroSchemaType writer, AvroSchemaType reader, AvroConversion fromInt, AvroConversion fromLong, AvroConversion fromFloat) => (writer, reader) switch
    {
        (AvroSchemaType.Int, AvroSchemaType.Long or AvroSchemaType.Float or AvroSchemaType.Double) => fromInt,
        (AvroSchemaType.Long, AvroSchemaType.Float or AvroSchemaType.Double) => fromLong,
        (AvroSchemaType.Float, AvroSchemaType.Double) => fromFloat,
        _ => AvroConversion.Transcode,
    };

    private sealed class PlanHolder(AvroRecordPlan? plan)
    {
        public AvroRecordPlan? Plan { get; } = plan;
    }
}
