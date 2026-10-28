using System.Text.RegularExpressions;

namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>
/// Classifies table rows as items, section headers, subtotals or totals using the row's text,
/// numbers, formulas and (when available) bold formatting. The rules are deliberately simple and
/// conservative; rows that fit no rule are <see cref="RowKind.Unknown"/>.
/// </summary>
public static partial class RowClassifier
{
    private const int MaxLabelLength = 60;

    public static RowKind Classify(IReadOnlyCollection<GridCell> cells, int tableColumnCount, bool stylesAvailable)
    {
        var hasNumberOrDate = cells.Any(cell => cell.IsNumeric || cell.Kind == CellValueKind.DateTime);

        if (FindTotalKeyword(cells, hasNumberOrDate) is { } keywordKind)
        {
            return keywordKind;
        }

        // A row whose numbers are all SUM formulas adds up rows above it (only .xlsx/.xlsm keep formulas).
        var numericCells = cells.Where(cell => cell.IsNumeric).ToList();
        if (numericCells.Count > 0 && numericCells.All(IsSumFormula))
        {
            return RowKind.Subtotal;
        }

        var isBold = stylesAvailable && cells.Count > 0 && cells.All(cell => cell.IsBold);
        if (!hasNumberOrDate && tableColumnCount >= 3 && (cells.Count == 1 || (isBold && cells.Count <= 2)))
        {
            return RowKind.SectionHeader;
        }

        return cells.Count >= 2 || hasNumberOrDate ? RowKind.Item : RowKind.Unknown;
    }

    /// <summary>
    /// Finds total and subtotal rows from their labels (short text cells only, so long item
    /// descriptions are not mistaken for totals):
    /// <list type="number">
    /// <item>"subtotal", "sub-total" or "sub total" anywhere as a word, or "carried forward",
    /// "brought forward", "carried to summary", "c/f", "b/f": subtotal.</item>
    /// <item>An unqualified total label ("Total", "Grand total", "Contract total", "Total amount"): total.</item>
    /// <item>A qualified total label with a value ("Section 2 total", "Total for Section 2",
    /// "Total Section 2"): subtotal.</item>
    /// <item>A summary-shaped row: its only text is a label containing the word "total" and it has
    /// exactly one number (no item code, unit, quantity or rate): subtotal.</item>
    /// </list>
    /// "Total station hire" with a code, unit, quantity and rate matches none of these and stays an item.
    /// </summary>
    private static RowKind? FindTotalKeyword(IReadOnlyCollection<GridCell> cells, bool hasNumberOrDate)
    {
        var texts = cells
            .Where(cell => cell.Kind == CellValueKind.Text)
            .Select(cell => cell.DisplayText.Trim())
            .Where(text => text.Length > 0)
            .ToList();
        var labels = texts.Where(text => text.Length <= MaxLabelLength).ToList();

        if (labels.Any(text => SubtotalWord().IsMatch(text) || CarriedLabel().IsMatch(text)))
        {
            return RowKind.Subtotal;
        }

        if (labels.Any(text => TotalLabel().IsMatch(text)))
        {
            return RowKind.Total;
        }

        if (hasNumberOrDate && labels.Any(text => QualifiedTotalLabel().IsMatch(text)))
        {
            return RowKind.Subtotal;
        }

        var distinctTexts = texts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var isSummaryShaped = distinctTexts.Count == 1
            && labels.Count > 0
            && cells.Count(cell => cell.IsNumeric) == 1
            && cells.All(cell => cell.Kind is CellValueKind.Text or CellValueKind.Number);
        if (isSummaryShaped && TotalWord().IsMatch(distinctTexts[0]))
        {
            return RowKind.Subtotal;
        }

        return null;
    }

    private static bool IsSumFormula(GridCell cell) =>
        cell.Formula is { } formula && formula.TrimStart('=', ' ').StartsWith("SUM(", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\bsub[\s-]?totals?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubtotalWord();

    [GeneratedRegex(@"\b(carried|brought)\s+(forward|fwd)\b|\bcarried\s+to\s+(the\s+)?(summary|collection)\b|(^|\s)[cb]/f\b", RegexOptions.IgnoreCase)]
    private static partial Regex CarriedLabel();

    [GeneratedRegex(@"^((grand|contract|overall|project|final)\s+)?total(\s+(amount|cost|price|value|sum|due|payable))?\s*(\([^)]*\))?\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TotalLabel();

    // "Section 2 total", "Earthworks total (USD)", "Total for section 2", "Total of bill 3", "Total Section 2".
    [GeneratedRegex(@"\S.*\btotal\s*(\([^)]*\))?\s*:?\s*$|^total\s+(for|of|section|bill|part|page|division|trade|works)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualifiedTotalLabel();

    [GeneratedRegex(@"\btotals?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TotalWord();
}
