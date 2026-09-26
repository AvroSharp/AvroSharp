using System.Collections.Generic;
using System.Linq;
using AvroSharp.Schemas;

namespace AvroSharp.Interop.Tests;

/// <summary>Schema properties that decide whether an Apache.Avro code path can be used as an oracle.</summary>
internal static class SchemaShapes
{
    /// <summary>
    /// Whether a record contains itself. Apache.Avro 1.12.2 recurses without end on such schemas in its JSON grammar
    /// generator and in its resolving reader's skip builder (a stack overflow that kills the test process).
    /// </summary>
    public static bool IsRecursive(AvroSchema schema)
    {
        return Visit(schema, []);

        static bool Visit(AvroSchema schema, HashSet<RecordSchema> open) => schema switch
        {
            RecordSchema record => !open.Add(record) || VisitFields(record, open),
            ArraySchema array => Visit(array.Items, open),
            MapSchema map => Visit(map.Values, open),
            UnionSchema union => union.Branches.Any(b => Visit(b, open)),
            _ => false,
        };

        static bool VisitFields(RecordSchema record, HashSet<RecordSchema> open)
        {
            foreach (var field in record.Fields)
            {
                if (Visit(field.Schema, open))
                {
                    return true;
                }
            }

            open.Remove(record);
            return false;
        }
    }
}