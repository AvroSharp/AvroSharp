// Newtonsoft.Json is banned in this repository in favor of System.Text.Json. Confluent's migration executors (JSONata)
// take and return the message as Newtonsoft's JToken, so this file, and only this one, converts to and from it.
// Confluent.SchemaRegistry depends on Newtonsoft.Json, so it is always there.
#pragma warning disable RS0030

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AvroSharp.Confluent;

/// <summary>The message of migration rules: Avro JSON as Newtonsoft's JToken, which Confluent's executors take.</summary>
internal static class MigrationJson
{
    public static object Parse(string json) => JToken.Parse(json);

    public static string ToJson(object message) => ((JToken)message).ToString(Formatting.None);
}
