using DocumentReader.Api.Configuration;

namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>
/// Finds tables on a worksheet without assuming a fixed layout.
/// <list type="bullet">
/// <item>A header row has at least two cells, all short text, and is followed by a data-like row.
/// Headers may start at any row or column.</item>
/// <item>Two adjacent header rows form one header when the upper row has a horizontally merged
/// group label (names become "Group / Column").</item>
/// <item>A table ends at two or more consecutive blank rows, or where a new header follows a blank
/// row. Rows repeating the current header (e.g. at page breaks) are skipped.</item>
/// <item>Merged cells spread their value over the merged area for headers; in data rows only text
/// is spread, never numbers, so amounts are not counted twice.</item>
/// <item>Rows that belong to no table are reported separately so they can still be indexed as text.</item>
/// </list>
/// </summary>
public static class TableDetector
{
    private const int MaxHeaderTextLength = 80;
    private const int DataLookaheadRows = 3;
    private const double DominantTypeShare = 0.8;

    public static SheetLayout Detect(SheetGrid sheet, LimitsOptions limits)
    {
        var context = new SheetContext(sheet, limits.MaxMergedRangeCells);
        var tables = new List<DetectedTable>();
        var rowsOutsideTables = new List<int>();
        var warnings = new List<string>();
        var rows = sheet.RowNumbers.ToList();

        TableBuilder? current = null;
        var previousRow = 0;
        var index = 0;

        void FinishCurrent()
        {
            if (current is null)
            {
                return;
            }

            if (current.Build(context) is { } table)
            {
                tables.Add(table);
                warnings.AddRange(table.Warnings);
            }
            else
            {
                rowsOutsideTables.AddRange(current.HeaderRows);
            }

            current = null;
        }

        while (index < rows.Count)
        {
            var row = rows[index];
            var blankRowsBefore = row - previousRow - 1;
            previousRow = row;

            if (current is not null && blankRowsBefore >= 2)
            {
                FinishCurrent();
            }

            if (current is not null)
            {
                if (current.IsRepeatedHeader(context.HeaderCells(row)))
                {
                    current.RepeatedHeaderCount++;
                    index++;
                    continue;
                }

                var startsNewTable = blankRowsBefore >= 1
                    && IsHeaderCandidate(context, row)
                    && (context.IsBoldRow(row) || NextDataRowHasNumbers(context, rows, index));
                if (!startsNewTable)
                {
                    var cells = context.DataCells(row);
                    if (cells.Any(cell => current.CoversColumn(cell.Column)))
                    {
                        current.AddRow(row, cells);
                    }
                    else
                    {
                        rowsOutsideTables.Add(row);
                    }

                    index++;
                    continue;
                }

                FinishCurrent();
            }

            if (IsHeaderCandidate(context, row) && NextRowIsDataLike(context, rows, index))
            {
                current = StartTable(context, rows, ref index);
                previousRow = current.HeaderRows[^1];
                continue;
            }

            rowsOutsideTables.Add(row);
            index++;
        }

        FinishCurrent();
        rowsOutsideTables.Sort();
        return new SheetLayout(sheet, tables, rowsOutsideTables, warnings);
    }

    private static TableBuilder StartTable(SheetContext context, List<int> rows, ref int index)
    {
        var headerRows = new List<int> { rows[index] };
        var next = index + 1;
        if (next < rows.Count
            && rows[next] == rows[index] + 1
            && context.HasHorizontalMerge(rows[index])
            && IsHeaderCandidate(context, rows[next]))
        {
            headerRows.Add(rows[next]);
            next++;
        }

        index = next;
        return new TableBuilder(context.Sheet.Name, headerRows, context);
    }

    private static bool IsHeaderCandidate(SheetContext context, int row)
    {
        var cells = context.HeaderCells(row);
        // Total rows are not mistaken for headers because they contain numbers (or a single label).
        // A title merged across several columns repeats one text, so at least two distinct texts are required.
        return cells.Count >= 2
            && cells.All(cell => cell.Kind == CellValueKind.Text && cell.DisplayText.Length <= MaxHeaderTextLength)
            && cells.Select(cell => cell.DisplayText).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
    }

    /// <summary>
    /// One of the next few non-empty rows (looking past e.g. a section heading) has a number/date
    /// or at least two values.
    /// </summary>
    private static bool NextRowIsDataLike(SheetContext context, List<int> rows, int index) =>
        FollowingRows(rows, index).Any(next =>
            context.DataCells(next) is var cells && (cells.Count >= 2 || cells.Any(IsNumberOrDate)));

    private static bool NextDataRowHasNumbers(SheetContext context, List<int> rows, int index) =>
        FollowingRows(rows, index).Any(next => context.DataCells(next).Any(IsNumberOrDate));

    /// <summary>Up to <see cref="DataLookaheadRows"/> non-empty rows that follow closely after rows[index].</summary>
    private static IEnumerable<int> FollowingRows(List<int> rows, int index) =>
        rows.Skip(index + 1)
            .Take(DataLookaheadRows)
            .TakeWhile(row => row - rows[index] <= DataLookaheadRows + 2);

    private static bool IsNumberOrDate(GridCell cell) => cell.IsNumeric || cell.Kind == CellValueKind.DateTime;

    /// <summary>Per-sheet helpers, including how merged ranges spread values.</summary>
    private sealed class SheetContext
    {
        private readonly Dictionary<int, List<(int Column, GridCell Source)>> _mergedFill = [];
        private readonly HashSet<int> _rowsWithHorizontalMerge = [];

        public SheetContext(SheetGrid sheet, int maxMergedRangeCells)
        {
            Sheet = sheet;
            foreach (var range in sheet.MergedRanges)
            {
                var area = (long)(range.LastRow - range.FirstRow + 1) * (range.LastColumn - range.FirstColumn + 1);
                if (area > maxMergedRangeCells || sheet.GetCell(range.FirstRow, range.FirstColumn) is not { } source)
                {
                    continue;
                }

                if (range.IsHorizontal)
                {
                    _rowsWithHorizontalMerge.Add(range.FirstRow);
                }

                for (var row = range.FirstRow; row <= range.LastRow; row++)
                {
                    for (var column = range.FirstColumn; column <= range.LastColumn; column++)
                    {
                        if (row == range.FirstRow && column == range.FirstColumn)
                        {
                            continue;
                        }

                        if (!_mergedFill.TryGetValue(row, out var fills))
                        {
                            fills = [];
                            _mergedFill[row] = fills;
                        }

                        fills.Add((column, source));
                    }
                }
            }
        }

        public SheetGrid Sheet { get; }

        public bool HasHorizontalMerge(int row) => _rowsWithHorizontalMerge.Contains(row);

        public bool IsBoldRow(int row)
        {
            var cells = Sheet.GetRow(row);
            return Sheet.StylesAvailable && cells.Count > 0 && cells.All(cell => cell.IsBold);
        }

        /// <summary>Row cells with merged values spread across their merged area.</summary>
        public IReadOnlyList<GridCell> HeaderCells(int row) => CellsWithMergedFill(row, textOnly: false);

        /// <summary>Row cells with only merged text spread (numbers stay in their own cell).</summary>
        public IReadOnlyList<GridCell> DataCells(int row) => CellsWithMergedFill(row, textOnly: true);

        private List<GridCell> CellsWithMergedFill(int row, bool textOnly)
        {
            var cells = Sheet.GetRow(row).ToDictionary(cell => cell.Column);
            if (_mergedFill.TryGetValue(row, out var fills))
            {
                foreach (var (column, source) in fills)
                {
                    if (!cells.ContainsKey(column) && (!textOnly || source.Kind == CellValueKind.Text))
                    {
                        cells[column] = source with { Row = row, Column = column };
                    }
                }
            }

            return cells.Values.OrderBy(cell => cell.Column).ToList();
        }
    }

    /// <summary>Collects the rows of one table and turns them into a <see cref="DetectedTable"/>.</summary>
    private sealed class TableBuilder
    {
        private readonly string _sheetName;
        private readonly SortedDictionary<int, string> _headerNames = [];
        private readonly Dictionary<int, string> _lastHeaderRowTexts;
        private readonly List<(int Row, IReadOnlyList<GridCell> Cells)> _rows = [];
        private readonly SortedSet<int> _unnamedColumns = [];

        public TableBuilder(string sheetName, List<int> headerRows, SheetContext context)
        {
            _sheetName = sheetName;
            HeaderRows = headerRows;

            var parts = new Dictionary<int, List<string>>();
            foreach (var headerRow in headerRows)
            {
                foreach (var cell in context.HeaderCells(headerRow))
                {
                    if (!parts.TryGetValue(cell.Column, out var columnParts))
                    {
                        columnParts = [];
                        parts[cell.Column] = columnParts;
                    }

                    if (columnParts.Count == 0 || !columnParts[^1].Equals(cell.DisplayText, StringComparison.OrdinalIgnoreCase))
                    {
                        columnParts.Add(cell.DisplayText);
                    }
                }
            }

            foreach (var (column, columnParts) in parts)
            {
                _headerNames[column] = string.Join(" / ", columnParts);
            }

            _lastHeaderRowTexts = context.HeaderCells(headerRows[^1]).ToDictionary(cell => cell.Column, cell => Normalize(cell.DisplayText));
        }

        public List<int> HeaderRows { get; }

        public int RepeatedHeaderCount { get; set; }

        public bool CoversColumn(int column) =>
            column >= _headerNames.Keys.First() - 1 && column <= _headerNames.Keys.Last() + 1 || _headerNames.ContainsKey(column);

        public bool IsRepeatedHeader(IReadOnlyList<GridCell> cells) =>
            cells.Count == _lastHeaderRowTexts.Count
            && cells.All(cell => _lastHeaderRowTexts.TryGetValue(cell.Column, out var text) && text == Normalize(cell.DisplayText));

        public void AddRow(int row, IReadOnlyList<GridCell> cells)
        {
            foreach (var cell in cells.Where(cell => !_headerNames.ContainsKey(cell.Column)))
            {
                _headerNames[cell.Column] = $"Column {CellAddress.ColumnLetter(cell.Column)}";
                _unnamedColumns.Add(cell.Column);
            }

            _rows.Add((row, cells));
        }

        public DetectedTable? Build(SheetContext context)
        {
            if (_rows.Count == 0)
            {
                return null;
            }

            var firstColumn = _headerNames.Keys.First();
            var lastColumn = _headerNames.Keys.Last();
            var location = $"Sheet \"{_sheetName}\", table {CellAddress.Range(HeaderRows[0], firstColumn, _rows[^1].Row, lastColumn)}";
            var warnings = new List<string>();

            var rows = _rows
                .Select(row => new TableRow(
                    row.Row,
                    RowClassifier.Classify(row.Cells, _headerNames.Count, context.Sheet.StylesAvailable),
                    row.Cells.ToDictionary(cell => cell.Column)))
                .ToList();

            var columns = BuildColumns(rows, location, warnings);

            if (_unnamedColumns.Count > 0)
            {
                warnings.Add($"{location}: values in column(s) {string.Join(", ", _unnamedColumns.Select(CellAddress.ColumnLetter))} have no header.");
            }

            if (RepeatedHeaderCount > 0)
            {
                warnings.Add($"{location}: {RepeatedHeaderCount} repeated header row(s) were skipped.");
            }

            var unknownRows = rows.Count(row => row.Kind == RowKind.Unknown);
            if (unknownRows > 0)
            {
                warnings.Add($"{location}: {unknownRows} row(s) could not be classified.");
            }

            return new DetectedTable(
                _sheetName, HeaderRows[0], HeaderRows[^1], firstColumn, lastColumn, _rows[^1].Row, columns, rows, warnings);
        }

        private List<TableColumn> BuildColumns(List<TableRow> rows, string location, List<string> warnings)
        {
            var columns = new List<TableColumn>();
            var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var itemRows = rows.Where(row => row.Kind == RowKind.Item).ToList();

            foreach (var (column, headerName) in _headerNames)
            {
                var name = headerName;
                if (usedNames.TryGetValue(name, out var count))
                {
                    usedNames[headerName] = count + 1;
                    name = $"{headerName} ({count + 1})";
                    warnings.Add($"{location}: duplicate column name \"{headerName}\" in column {CellAddress.ColumnLetter(column)} was renamed to \"{name}\".");
                }
                else
                {
                    usedNames[name] = 1;
                }

                var cells = itemRows
                    .Select(row => row.Cells.GetValueOrDefault(column))
                    .OfType<GridCell>()
                    .ToList();
                var type = InferType(cells);
                if (type == ColumnType.Mixed)
                {
                    warnings.Add($"{location}: column \"{name}\" ({CellAddress.ColumnLetter(column)}) has mixed value types.");
                }

                var currency = type == ColumnType.Number ? InferCurrency(cells, name, location, warnings) : null;
                columns.Add(new TableColumn(column, name, type, currency));
            }

            return columns;
        }

        private static ColumnType InferType(List<GridCell> cells)
        {
            if (cells.Count == 0)
            {
                return ColumnType.Empty;
            }

            var dominant = cells.GroupBy(cell => cell.Kind).OrderByDescending(group => group.Count()).First();
            if (dominant.Count() < cells.Count * DominantTypeShare)
            {
                return ColumnType.Mixed;
            }

            return dominant.Key switch
            {
                CellValueKind.Number => ColumnType.Number,
                CellValueKind.DateTime => ColumnType.Date,
                CellValueKind.Boolean => ColumnType.Boolean,
                CellValueKind.Text => ColumnType.Text,
                _ => ColumnType.Mixed,
            };
        }

        private static string? InferCurrency(List<GridCell> cells, string columnName, string location, List<string> warnings)
        {
            var formatCurrencies = cells
                .Where(cell => cell.IsNumeric)
                .Select(cell => CurrencyDetector.FromNumberFormat(cell.NumberFormat))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var headerCurrency = CurrencyDetector.FromText(columnName);

            var resolution = CurrencyDetector.Resolve(formatCurrencies, headerCurrency);
            switch (resolution.Conflict)
            {
                case CurrencyConflict.BetweenFormats:
                    warnings.Add($"{location}: column \"{columnName}\" uses more than one currency ({string.Join(", ", formatCurrencies)}).");
                    break;
                case CurrencyConflict.HeaderAndFormats:
                    warnings.Add($"{location}: column \"{columnName}\" header says {headerCurrency} but its cells are formatted as {string.Join(", ", formatCurrencies)}.");
                    break;
            }

            return resolution.Currency;
        }

        private static string Normalize(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}
