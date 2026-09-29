namespace AvroSharp.CodeGen;

/// <summary>Ordinal string replacement on every target: netstandard2.0 has no overload that takes a comparison.</summary>
internal static class StringExtensions
{
    public static string ReplaceOrdinal(this string text, string oldValue, string newValue) =>
#if NET
        text.Replace(oldValue, newValue, System.StringComparison.Ordinal);
#else
        text.Replace(oldValue, newValue);
#endif
}
