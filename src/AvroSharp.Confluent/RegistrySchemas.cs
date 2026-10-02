using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AvroSharp.Schemas;
using Confluent.SchemaRegistry;
using ConfluentSchema = Confluent.SchemaRegistry.Schema;

namespace AvroSharp.Confluent;

/// <summary>Parses schemas from the registry, with the schemas they reference.</summary>
internal static class RegistrySchemas
{
    // The registry is the authority on the schemas it holds: a schema with an invalid field default (which older
    // registries and Apache.Avro, so Confluent's serde, accept) still describes the data written with it, and a writer
    // schema's defaults are never used to read it. Rejecting it would stop a consumer at the first such message.
    private static readonly AvroSchemaParseOptions Lenient = new() { ValidateDefaults = false };

    /// <summary>
    /// Parses a registered schema after its references, which Confluent's base class fetches (recursively). A
    /// reference can itself use another, so they are parsed in rounds until each one's names are known.
    /// </summary>
    public static async Task<AvroSchema> ParseAsync(ConfluentSchema schema, Func<ConfluentSchema, Task<IDictionary<string, string>>> resolveReferences)
    {
        var parser = new AvroSchemaParser(Lenient);
        var pending = (await resolveReferences(schema).ConfigureAwait(false)).Values.ToList();
        while (pending.Count > 0)
        {
            var failed = new List<string>();
            foreach (var json in pending)
            {
                try
                {
                    parser.Parse(json);
                }
                catch (AvroSchemaException)
                {
                    failed.Add(json);
                }
            }

            if (failed.Count == pending.Count)
            {
                // No reference parsed in this round: one of them is invalid, or uses a name nothing defines.
                parser.Parse(failed[0]);
            }

            pending = failed;
        }

        return parser.Parse(schema.SchemaString);
    }
}

/// <summary>Data contract rules: what this version supports.</summary>
internal static class RuleSupport
{
    /// <summary>
    /// The field transformer for rules that change fields, such as field-level encryption (CSFLE): not supported
    /// yet, so such a rule fails rather than leaving the field unchanged.
    /// </summary>
    public static Task<object> NoFieldTransformsAsync(RuleContext context, IFieldTransform transform, object message) =>
        throw new NotSupportedException($"The rule '{context.Rule.Name}' changes fields (such as field-level encryption), which AvroSharp.Confluent doesn't support yet.");
}
