using DocumentReader.Api.Ingestion;

namespace DocumentReader.Tests.Infrastructure;

/// <summary>
/// A test <see cref="IDocumentExtractor"/> whose behaviour each test sets: it can wait for other
/// extractions (to prove they overlap), block until released, or hang. It records how many
/// extractions ran at the same time.
/// </summary>
public sealed class ControllableExtractor : IDocumentExtractor
{
    private int _active;
    private int _maxActive;

    public Func<byte[], CancellationToken, ExtractionResult> Behavior { get; set; } = (_, _) => Indexed();

    public int Active => Volatile.Read(ref _active);

    public int MaxActive => Volatile.Read(ref _maxActive);

    public void Reset()
    {
        Behavior = (_, _) => Indexed();
        Interlocked.Exchange(ref _maxActive, 0);
    }

    public ExtractionResult Extract(byte[] content, FileType fileType, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        int observed;
        while (active > (observed = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, active, observed) != observed)
        {
        }

        try
        {
            return Behavior(content, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>Waits (up to a few seconds) until <paramref name="count"/> extractions run at once.</summary>
    public void WaitForConcurrent(int count) =>
        SpinWait.SpinUntil(() => Active >= count, TimeSpan.FromSeconds(5));

    public static ExtractionResult Indexed() => new(
        DocumentStatus.Indexed,
        1,
        null,
        [],
        [new ExtractedChunk(EvidenceKind.TextChunk, new PdfPageLocation(1), "Extracted text")],
        []);

    /// <summary>True when the content ends with the given ASCII marker (used to single out one file).</summary>
    public static bool HasMarker(byte[] content, string marker) =>
        content.AsSpan().EndsWith(System.Text.Encoding.ASCII.GetBytes(marker));
}
