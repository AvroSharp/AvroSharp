using System.Collections.Generic;
using System.Linq;

namespace AvroSharp.Generic;

/// <summary>
/// Collects where in a value an error happened while the exception propagates. Nodes add their segment from
/// exception filters, which never catch; the public entry point catches once and prefixes the message. Catching and
/// rethrowing at every level instead nests exception handling, which overflows the stack on .NET Framework.
/// </summary>
internal static class ErrorPath
{
    private const string Key = "AvroSharp.ErrorPath";

    /// <summary>Exception filter: adds a segment (innermost first) and returns false, so the exception keeps propagating.</summary>
    public static bool Add(AvroException ex, string segment)
    {
        if (ex.Data[Key] is not List<string> path)
        {
            ex.Data[Key] = path = [];
        }

        path.Add(segment);
        return false;
    }

    /// <summary>Gets the collected segments, innermost first, or <see langword="null"/> when none were added.</summary>
    public static List<string>? Get(AvroException ex) => ex.Data[Key] as List<string>;

    /// <summary>Formats record fields as <c>Field 'a.B.x' > 'a.C.y': </c>, shortening long paths.</summary>
    public static string DescribeFields(List<string> innermostFirst)
    {
        const int Shown = 8;
        var outermostFirst = Enumerable.Reverse(innermostFirst).ToList();
        var parts = outermostFirst.Count <= Shown
            ? outermostFirst
            : [.. outermostFirst.Take(Shown / 2), "...", .. outermostFirst.Skip(outermostFirst.Count - (Shown / 2))];
        return "Field '" + string.Join("' > '", parts) + "': ";
    }

    /// <summary>Formats JSON locations as a path such as <c>$.user.emails[2]</c>.</summary>
    public static string DescribeJson(List<string> innermostFirst) =>
        "$" + string.Concat(Enumerable.Reverse(innermostFirst));
}
