using System.Text.RegularExpressions;

namespace DocumentReader.Api.Search;

/// <summary>
/// Splits text into lowercase word tokens (letters and digits in any script) for keyword search.
/// Very common English words are dropped; no stemming is applied.
/// </summary>
public static partial class TextTokenizer
{
    private const int MaxTokenLength = 40;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it",
        "of", "on", "or", "that", "the", "this", "to", "was", "were", "with",
    };

    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match match in Word().Matches(text.ToLowerInvariant()))
        {
            if (match.Length <= MaxTokenLength && !StopWords.Contains(match.Value))
            {
                tokens.Add(match.Value);
            }
        }

        return tokens;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
