using DocumentReader.Api.Ingestion.Excel;

namespace DocumentReader.Tests.Ingestion.Excel;

public sealed class CurrencyAndAddressTests
{
    [Theory]
    [InlineData("$#,##0.00", "$")]
    [InlineData("_($* #,##0.00_)", "$")]
    [InlineData("[$€-x-euro2] #,##0.00", "EUR")]
    [InlineData("#,##0.00 [$USD]", "USD")]
    [InlineData("\"AED\" #,##0.00", "AED")]
    [InlineData("£#,##0", "GBP")]
    [InlineData("#,##0.00", null)]
    [InlineData("[$-409]d-mmm-yy", null)]
    [InlineData("[Red]#,##0.00", null)]
    [InlineData(null, null)]
    public void FromNumberFormat_ReportsStatedCurrencyOnly(string? numberFormat, string? expected)
    {
        Assert.Equal(expected, CurrencyDetector.FromNumberFormat(numberFormat));
    }

    [Theory]
    [InlineData("Amount (USD)", "USD")]
    [InlineData("Rate SAR", "SAR")]
    [InlineData("Price €", "EUR")]
    [InlineData("Amount", null)]
    [InlineData("QTY", null)]
    public void FromText_FindsIsoCodesAndSymbols(string text, string? expected)
    {
        Assert.Equal(expected, CurrencyDetector.FromText(text));
    }

    [Theory]
    [InlineData("USD", "USD", "$")]
    [InlineData("CAD", "CAD", "$")]
    [InlineData("JPY", "JPY", "¥")]
    [InlineData("GBP", "GBP", "GBP")]
    [InlineData("USD", null, "$", "USD")]
    [InlineData("$", null, "$")]
    [InlineData("USD", "USD")]
    [InlineData("$", "$")]
    [InlineData(null, null)]
    public void Resolve_ConsistentCurrencies_CombineToTheMostSpecific(string? expected, string? header, params string[] formats)
    {
        var resolution = CurrencyDetector.Resolve(formats, header);

        Assert.Equal(expected, resolution.Currency);
        Assert.Equal(CurrencyConflict.None, resolution.Conflict);
    }

    [Theory]
    [InlineData("EUR", "GBP")]
    [InlineData("USD", "GBP")]
    [InlineData("EUR", "$")]
    [InlineData("GBP", "¥")]
    public void Resolve_HeaderDisagreesWithFormats_IsAConflict(string header, string format)
    {
        var resolution = CurrencyDetector.Resolve([format], header);

        Assert.Null(resolution.Currency);
        Assert.Equal(CurrencyConflict.HeaderAndFormats, resolution.Conflict);
    }

    [Theory]
    [InlineData("EUR", "AED")]
    [InlineData("$", "¥")]
    [InlineData("USD", "CAD")]
    public void Resolve_DifferentFormatCurrencies_IsAConflict(string first, string second)
    {
        Assert.Equal(CurrencyConflict.BetweenFormats, CurrencyDetector.Resolve([first, second], null).Conflict);
    }

    [Theory]
    [InlineData("\"$\"#,##0.00", "$")]
    [InlineData("[$£-809]#,##0.00", "GBP")]
    public void FromNumberFormat_QuotedAndLocaleSymbols(string numberFormat, string expected)
    {
        Assert.Equal(expected, CurrencyDetector.FromNumberFormat(numberFormat));
    }

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(702, "ZZ")]
    [InlineData(703, "AAA")]
    public void ColumnLetter_UsesExcelNumbering(int column, string expected)
    {
        Assert.Equal(expected, CellAddress.ColumnLetter(column));
    }

    [Fact]
    public void Range_FormatsA1Notation()
    {
        Assert.Equal("B5:G5", CellAddress.Range(5, 2, 5, 7));
    }
}
