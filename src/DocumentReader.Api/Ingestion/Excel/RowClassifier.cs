using System.Text.RegularExpressions;

namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>
/// Classifies table rows as items, section headers, subtotals or totals using the row's text,
/// numbers, formulas and (when available) bold formatting. The rules are deliberately simple and
/// conservative; rows that fit no rule are <see cref="RowKind.Unknown"/>.
/// </summary>
public static partial class RowClassifier
{
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
    /// An unqualified "Total" / "Grand total" label marks the table's total; a qualified label such as
    /// "Subtotal", "Section 2 total" or "Carried forward" marks a subtotal. Labels must be short so
    /// item descriptions like "Total station hire" are not mistaken for totals.
    /// </summary>
    private static RowKind? FindTotalKeyword(IReadOnlyCollection<GridCell> cells, bool hasNumberOrDate)
    {
        foreach (var cell in cells.Where(cell => cell.Kind == CellValueKind.Text))
        {
            var text = cell.DisplayText.Trim();
            if (text.Length > 60)
            {
                continue;
            }

            if (SubtotalLabel().IsMatch(text))
            {
                return RowKind.Subtotal;
            }

            if (TotalLabel().IsMatch(text))
            {
                return RowKind.Total;
            }

            if (hasNumberOrDate && QualifiedTotalLabel().IsMatch(text))
            {
                return RowKind.Subtotal;
            }
        }

        return null;
    }

    private static bool IsSumFormula(GridCell cell) =>
        cell.Formula is { } formula && formula.TrimStart('=', ' ').StartsWith("SUM(", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(sub[\s-]?total|section\s+total|page\s+total|carried\s+forward|brought\s+forward|c/f|b/f)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubtotalLabel();

    [GeneratedRegex(@"^(grand\s+)?total(\s+(amount|cost|price|value|sum|due|payable))?\s*(\([^)]*\))?\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TotalLabel();

    [GeneratedRegex(@"\S.*\btotal\s*(\([^)]*\))?\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex QualifiedTotalLabel();
}
