using DocumentReader.Api.Configuration;

namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>
/// Shared bookkeeping for the Excel readers: applies the Excel limits from <see cref="LimitsOptions"/> while cells are added
/// and records why reading stopped early.
/// </summary>
internal sealed class GridBuilder(LimitsOptions limits, CancellationToken cancellationToken)
{
    private readonly List<SheetGrid> _sheets = [];
    private readonly List<string> _warnings = [];
    private int _totalCells;
    private bool _skippedOutOfRangeCells;

    public bool IsTruncated { get; private set; }

    /// <summary>False once a limit or the time budget stops reading.</summary>
    public bool CanContinue => !IsTruncated;

    /// <summary>Starts a new sheet, or returns null if no more sheets may be read.</summary>
    public SheetGrid? StartSheet(string name, bool isHidden, bool stylesAvailable)
    {
        if (!CheckTime())
        {
            return null;
        }

        if (_sheets.Count >= limits.MaxSheetsPerWorkbook)
        {
            Stop($"Only the first {limits.MaxSheetsPerWorkbook} sheets were read (sheet limit).");
            return null;
        }

        var sheet = new SheetGrid(CleanText(name), isHidden, stylesAvailable);
        _sheets.Add(sheet);
        return sheet;
    }

    /// <summary>Adds a cell; returns false when reading must stop.</summary>
    public bool TryAddCell(SheetGrid sheet, GridCell cell)
    {
        if (cell.Row > limits.MaxExcelRows || cell.Column > limits.MaxExcelColumns)
        {
            // Cells far outside the normal area are skipped individually rather than stopping everything.
            _skippedOutOfRangeCells = true;
            return true;
        }

        if (_totalCells >= limits.MaxCellsPerWorkbook)
        {
            Stop($"Only the first {limits.MaxCellsPerWorkbook:N0} cells were read (cell limit).");
            return false;
        }

        if (_totalCells % 1000 == 0 && !CheckTime())
        {
            return false;
        }

        sheet.AddCell(cell);
        _totalCells++;
        return true;
    }

    /// <summary>Cleans cell or sheet text and applies the configured length limit.</summary>
    public string CleanText(string text) => CellText.Clean(text, limits.MaxCellTextLength);

    public WorkbookGrid Build()
    {
        if (_skippedOutOfRangeCells)
        {
            IsTruncated = true;
            _warnings.Add($"Cells beyond row {limits.MaxExcelRows:N0} or column {CellAddress.ColumnLetter(limits.MaxExcelColumns)} were skipped.");
        }

        return new WorkbookGrid(_sheets, _warnings, IsTruncated);
    }

    private bool CheckTime()
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Stop("Processing stopped because the time limit was reached; later content was not read.");
            return false;
        }

        return true;
    }

    private void Stop(string warning)
    {
        if (!IsTruncated)
        {
            IsTruncated = true;
            _warnings.Add(warning);
        }
    }
}
