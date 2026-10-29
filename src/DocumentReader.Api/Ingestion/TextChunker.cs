namespace DocumentReader.Api.Ingestion;

/// <summary>
/// Splits text into chunks of bounded length, preferring paragraph, then line, then word
/// boundaries so that each chunk stays readable as a citation.
/// </summary>
public static class TextChunker
{
    /// <summary>Default chunk size; well under the contract's 4000-character evidence limit.</summary>
    public const int DefaultMaxChunkCharacters = 1500;

    public static IReadOnlyList<string> Split(string text, int maxChunkCharacters = DefaultMaxChunkCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunkCharacters, 1);

        var chunks = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > maxChunkCharacters)
        {
            var splitAt = FindSplitPosition(remaining, maxChunkCharacters);
            AddIfNotEmpty(chunks, remaining[..splitAt]);
            remaining = remaining[splitAt..].TrimStart();
        }

        AddIfNotEmpty(chunks, remaining);
        return chunks;
    }

    private static int FindSplitPosition(string text, int maxLength)
    {
        // Only accept a boundary in the second half of the window, so chunks are not tiny.
        var minimum = maxLength / 2;
        foreach (var separator in new[] { "\n\n", "\n", " " })
        {
            var index = text.LastIndexOf(separator, maxLength - 1, maxLength, StringComparison.Ordinal);
            if (index >= minimum)
            {
                return index + separator.Length;
            }
        }

        // No natural boundary (e.g. one very long token): split hard at the limit,
        // without cutting a surrogate pair in half.
        return char.IsHighSurrogate(text[maxLength - 1]) && maxLength > 1 ? maxLength - 1 : maxLength;
    }

    private static void AddIfNotEmpty(List<string> chunks, string chunk)
    {
        var trimmed = chunk.Trim();
        if (trimmed.Length > 0)
        {
            chunks.Add(trimmed);
        }
    }
}
