using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AvroSharp.Tests;

/// <summary>Conventions of the test sources themselves, checked by reading them.</summary>
public class TestConventionTests
{
    /// <summary>
    /// TUnit's <c>IsEquivalentTo</c> on a collection defaults to <c>CollectionOrdering.Any</c>, which passes
    /// <c>[3, 2, 1]</c> for <c>[1, 2, 3]</c> and a permuted <c>byte[]</c> for the right one (#133). Every call names its
    /// ordering: <c>Matching</c>, or <c>Any</c> where order means nothing (file names, hint names).
    /// </summary>
    [Test]
    public async Task CollectionEquivalence_AlwaysNamesItsOrdering()
    {
        var tests = Path.Combine(RepositoryRoot(), "tests");
        var separator = Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal) && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            const string Call = ".IsEquivalentTo(";
            for (var at = text.IndexOf(Call, StringComparison.Ordinal); at >= 0; at = text.IndexOf(Call, at + Call.Length, StringComparison.Ordinal))
            {
                // Text in a string literal, such as Call itself, is not a call.
                if (at > 0 && text[at - 1] != '"' && !Arguments(text, at + Call.Length).Contains("CollectionOrdering", StringComparison.Ordinal))
                {
                    var line = text.Take(at).Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(tests, file)}:{line}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    // The text of a call's arguments, from just after its opening parenthesis to the matching closing one.
    private static string Arguments(string text, int start)
    {
        var depth = 1;
        var end = start;
        while (end < text.Length && depth > 0)
        {
            depth += text[end] switch { '(' => 1, ')' => -1, _ => 0 };
            end++;
        }

        return text[start..(end - 1)];
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AvroSharp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (with AvroSharp.slnx) is not above the test's output folder.");
    }
}
