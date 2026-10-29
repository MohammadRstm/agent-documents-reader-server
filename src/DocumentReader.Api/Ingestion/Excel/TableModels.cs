namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>What a table row represents. Aggregations exclude subtotal and total rows by default.</summary>
public enum RowKind
{
    Item,
    SectionHeader,
    Subtotal,
    Total,
    Unknown,
}

public enum ColumnType
{
    Empty,
    Text,
    Number,
    Date,
    Boolean,
    Mixed,
}

/// <summary>A table column. <see cref="Currency"/> is set only when the file states it consistently.</summary>
public sealed record TableColumn(int ColumnNumber, string Name, ColumnType Type, string? Currency)
{
    public string Letter => CellAddress.ColumnLetter(ColumnNumber);
}

/// <summary>A data row of a table; <see cref="Cells"/> is keyed by column number.</summary>
public sealed record TableRow(int RowNumber, RowKind Kind, IReadOnlyDictionary<int, GridCell> Cells);

/// <summary>A table detected on a worksheet, with its header rows, typed columns and classified rows.</summary>
public sealed record DetectedTable(
    string SheetName,
    int HeaderFirstRow,
    int HeaderLastRow,
    int FirstColumn,
    int LastColumn,
    int LastRow,
    IReadOnlyList<TableColumn> Columns,
    IReadOnlyList<TableRow> Rows,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The whole table including headers, in A1 notation.</summary>
    public string Range => CellAddress.Range(HeaderFirstRow, FirstColumn, LastRow, LastColumn);

    /// <summary>One row of the table across all its columns, in A1 notation.</summary>
    public string RowRange(int row) => CellAddress.Range(row, FirstColumn, row, LastColumn);
}

/// <summary>The detected tables of a sheet plus the non-empty rows that belong to no table.</summary>
public sealed record SheetLayout(
    SheetGrid Sheet,
    IReadOnlyList<DetectedTable> Tables,
    IReadOnlyList<int> RowsOutsideTables,
    IReadOnlyList<string> Warnings);
